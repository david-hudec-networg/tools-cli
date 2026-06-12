using System.Configuration;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Tooling.Connector;
using Microsoft.Xrm.Tooling.Dmt.DataMigCommon.Utility;
using Microsoft.Xrm.Tooling.Dmt.ImportProcessor.DataInteraction;
using TALXIS.CLI.Logging;

namespace TALXIS.CLI.Platform.Xrm;

/// <summary>
/// Standalone CMT data import runner — uses <c>ImportCrmDataHandler</c>
/// directly (no Package Deployer / CoreObjects wrapper). Mirrors the
/// approach taken by <c>pac data import</c>.
///
/// Shared infrastructure (Cecil patching, assembly resolvers) is provided
/// by <see cref="LegacyAssemblyRuntime"/>.
/// </summary>
public sealed class CmtImportRunner
{
    static CmtImportRunner()
    {
        LegacyAssemblyRuntime.EnsureInitialized();
    }

    /// <summary>
    /// Instance-level assembly map that extends the static map with
    /// assemblies found inside the extracted data package directory.
    /// </summary>
    private readonly Dictionary<string, Assembly> _assemblyMap;
    private readonly HashSet<string> _unresolvedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = TxcLoggerFactory.CreateLogger(nameof(CmtImportRunner));

    /// <summary>
    /// Tracks the number of failed stages reported by CMT progress events.
    /// Checked after <c>ImportDataToCrm</c> returns to detect silent failures.
    /// </summary>
    private int _failedStageCount;

    /// <summary>
    /// Guards the one-shot LookupKeys pre-population so it fires exactly once
    /// when CMT fires the "Schema Validation Complete" progress event (which is
    /// when <c>ImportCommonMethods.dataEntities</c> is first populated).
    /// </summary>
    private int _lookupKeysPrepopulated;

    /// <summary>
    /// Runtime type of the ImportCrmDataHandler (set during RunInternalAsync).
    /// Used by <see cref="PrePopulateLookupKeysFromPackage"/> to navigate to
    /// the correct runtime-loaded DataMigCommon assembly, which may differ from
    /// the net462 compile-time reference if the assembly resolver returned a
    /// different instance.
    /// </summary>
    private Type? _handlerRuntimeType;

    public CmtImportRunner()
    {
        _assemblyMap = new Dictionary<string, Assembly>(
            LegacyAssemblyRuntime.StaticAssemblyMap,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs a standalone CMT data import. The flow follows the same
    /// pattern as <c>pac data import</c>:
    /// <list type="number">
    ///   <item>Extract the data.zip into a working directory</item>
    ///   <item>Connect to Dataverse (connection string or interactive auth)</item>
    ///   <item>Create <see cref="ImportCrmDataHandler"/></item>
    ///   <item>Wire progress events</item>
    ///   <item>Validate schema</item>
    ///   <item>Import data</item>
    /// </list>
    /// </summary>
    public Task<CmtImportResult> RunAsync(CmtImportRequest request, string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return RunInternalAsync(request, () => new CrmServiceClient(connectionString), cancellationToken);
    }

    /// <summary>
    /// Token-provider overload matching
    /// <see cref="PackageDeployerRunner.RunAsync(PackageDeployerRequest, Uri, Func{string, Task{string}}, CancellationToken)"/>.
    /// Primary and clone <see cref="CrmServiceClient"/> instances are built
    /// via the capture-free <c>(Uri, Func&lt;string, Task&lt;string&gt;&gt;, ...)</c>
    /// constructor, so no static auth state is involved.
    /// </summary>
    public Task<CmtImportResult> RunAsync(
        CmtImportRequest request,
        Uri environmentUrl,
        Func<string, Task<string>> tokenProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environmentUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        if (!environmentUrl.IsAbsoluteUri)
            throw new ArgumentException($"Environment URL '{environmentUrl}' must be absolute.", nameof(environmentUrl));

        return RunInternalAsync(
            request,
            () => new CrmServiceClient(environmentUrl, tokenProvider, useUniqueInstance: true),
            cancellationToken);
    }

    private async Task<CmtImportResult> RunInternalAsync(
        CmtImportRequest request,
        Func<CrmServiceClient> clientFactory,
        CancellationToken cancellationToken)
    {        bool isDirectory = Directory.Exists(request.DataPath);
        bool isFile = !isDirectory && File.Exists(request.DataPath);

        if (!isDirectory && !isFile)
        {
            return new CmtImportResult(false, $"Data package not found: '{request.DataPath}'");
        }

        // Register instance-level assembly resolver for any DLLs shipped
        // alongside the data package.
        AppDomain.CurrentDomain.AssemblyResolve += OnResolveAssembly;
        AssemblyLoadContext.Default.Resolving += OnResolveAssemblyLoadContext;

        string? workingFolder = null;
        // Track whether we created a temp folder that should be cleaned up.
        bool ownsWorkingFolder = false;
        CrmServiceClient? crmServiceClient = null;

        try
        {
            // 1. Resolve working folder — extract zip or use folder directly.
            if (isDirectory)
            {
                workingFolder = request.DataPath;

                // Validate the folder contains the required CMT files.
                string schemaFile = Path.Combine(workingFolder, "data_schema.xml");
                string dataFile = Path.Combine(workingFolder, "data.xml");
                if (!File.Exists(schemaFile) || !File.Exists(dataFile))
                {
                    return new CmtImportResult(false,
                        $"Folder '{workingFolder}' does not contain required CMT files (data.xml and data_schema.xml).");
                }

                _logger.LogInformation("Using data folder: {Folder}", workingFolder);
            }
            else
            {
                workingFolder = Path.Combine(
                    Path.GetTempPath(),
                    "txc",
                    "cmt-import",
                    $"{Path.GetFileNameWithoutExtension(request.DataPath)}-{Guid.NewGuid():N}");

                Directory.CreateDirectory(workingFolder);
                ownsWorkingFolder = true;
                _logger.LogInformation("Extracting data package to {Folder}...", workingFolder);
                ZipFile.ExtractToDirectory(request.DataPath, workingFolder, overwriteFiles: true);
            }

            // Register extracted directory for assembly probing.
            RegisterExtractedDirectoryForProbing(workingFolder);

            // 2. Connect to Dataverse.
            crmServiceClient = clientFactory();

            if (!crmServiceClient.IsReady)
            {
                string error = crmServiceClient.LastCrmException?.Message ?? crmServiceClient.LastCrmError ?? "Unknown connection error.";
                return new CmtImportResult(false, error);
            }

            if (request.Verbose)
            {
                _logger.LogInformation("Connected to: {Url}", crmServiceClient.ConnectedOrgUriActual);
                _logger.LogInformation("Organization version: {Version}", crmServiceClient.ConnectedOrgVersion);
            }

            // 3. Create ImportCrmDataHandler and set connection via reflection.
            // The CrmConnection property type is the legacy CrmServiceClient
            // (strong-named v4.0.0.0). At runtime our shim satisfies the
            // reference via the assembly resolver, but the compiler can't
            // see the match, so we set it reflectively.
            var handler = new ImportCrmDataHandler();
            var crmConnProp = handler.GetType().GetProperty("CrmConnection")
                ?? throw new InvalidOperationException("ImportCrmDataHandler.CrmConnection property not found.");
            crmConnProp.SetValue(handler, crmServiceClient);

            // Apply import tuning options via handler properties.
            handler.EnabledBatchMode = request.BatchMode;
            handler.RequestedBatchSize = request.BatchSize;
            handler.OverrideDataImportSafetyChecks = request.OverrideSafetyChecks;
            handler.PrefetchRecordLimitSize = request.PrefetchLimit;

            // Also set via AppSettings — the handler's internal methods may
            // re-read these from AppSettingsHelper, overwriting the property values.
            // This AppSettings mutation is safe because CMT runs in a subprocess
            // and does not share process state with the parent CLI host.
            ConfigurationManager.AppSettings["DMT.EnableBatchMode"] = request.BatchMode ? "true" : "false";
            ConfigurationManager.AppSettings["DMT.RequestedBatchSize"] = request.BatchSize.ToString();
            ConfigurationManager.AppSettings["DMT.OverrideSafetyChecks"] = request.OverrideSafetyChecks ? "true" : "false";
            ConfigurationManager.AppSettings["DMT.PrefetchRecordLimitCount"] = request.PrefetchLimit.ToString();
            // SchemaValidator uses this to recognize FileAttributeMetadata columns (type="filedata").
            ConfigurationManager.AppSettings["ExportFiles"] = "true";

            // 4. Wire progress event handlers.
            // Capture the RUNTIME type so PrePopulateLookupKeysFromPackage can
            // navigate to the correct DataMigCommon assembly instance (which may
            // differ from the net462 compile-time reference).
            _handlerRuntimeType = handler.GetType();
            handler.AddNewProgressItem += OnAddNewProgressItem;
            handler.UpdateProgressItem += OnUpdateProgressItem;

            // 5. Create clone connections for parallel import.
            // ImportConnections expects Dictionary<int, CrmServiceClient> where
            // keys are 1-based connection indices.
            if (request.ConnectionCount > 1)
            {
                var clones = new Dictionary<int, CrmServiceClient>();
                for (int i = 1; i < request.ConnectionCount; i++)
                {
                    try
                    {
                        // Create additional CrmServiceClient instances using the
                        // same auth as the primary client (connection string or
                        // token-provider callback, via the shared factory).
                        CrmServiceClient clone = clientFactory();

                        if (clone.IsReady)
                        {
                            clones.Add(i, clone);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (request.Verbose)
                        {
                            _logger.LogWarning("Failed to create clone connection {Index}: {Error}", i + 1, ex.Message);
                        }
                    }
                }

                if (clones.Count > 0)
                {
                    // Set ImportConnections via reflection (same type mismatch as CrmConnection).
                    var importConnProp = handler.GetType().GetProperty("ImportConnections");
                    importConnProp?.SetValue(handler, clones);
                    _logger.LogInformation("Using {Count} parallel connections for import", clones.Count + 1);
                }
            }

            // 6. Wire CMT console trace logging.
            TryWireCmtConsoleLogging(handler, request.Verbose);

            // 7. Validate schema.
            _logger.LogInformation("Running Schema Validation...");
            bool schemaValid = handler.ValidateSchemaFile(workingFolder);

            if (!schemaValid)
            {
                return new CmtImportResult(false, "Schema validation failed. Check the data package schema against the target environment.");
            }

            _logger.LogInformation("Schema Validation Complete.");

            // 8. Import data (synchronous — blocks via internal WaitOne).
            _logger.LogInformation("Starting data import...");
            // NOTE: The deleteBeforeAdd parameter is accepted by CMT's API but
            // is never actually used internally — the delete functionality was
            // never implemented in ImportCrmDataHandler.
            await Task.Run(() => handler.ImportDataToCrm(workingFolder, deleteBeforeAdd: false));

            // CMT often swallows exceptions internally and only reports
            // failures through progress events. Check whether any stages
            // were reported as failed.
            if (_failedStageCount > 0)
            {
                return new CmtImportResult(false,
                    $"Data import completed with {_failedStageCount} failed stage(s). See console output above for details.");
            }

            _logger.LogInformation("Data import completed successfully.");
            return new CmtImportResult(true, null);
        }
        catch (Exception ex)
        {
            string message = ex.InnerException?.Message ?? ex.Message;
            return new CmtImportResult(false, $"Data import failed: {message}");
        }
        finally
        {
            crmServiceClient?.Dispose();

            AppDomain.CurrentDomain.AssemblyResolve -= OnResolveAssembly;
            AssemblyLoadContext.Default.Resolving -= OnResolveAssemblyLoadContext;

            // Clean up extracted working directory (only if we created it).
            if (ownsWorkingFolder && workingFolder is not null)
            {
                try { Directory.Delete(workingFolder, recursive: true); }
                catch (Exception ex) { _logger.LogDebug(ex, "Best-effort cleanup of working folder failed."); }
            }

            if (_unresolvedAssemblies.Count > 0 && request.Verbose)
            {
                _logger.LogDebug("Unresolved assemblies during CMT import: {Assemblies}", string.Join(", ", _unresolvedAssemblies));
            }
        }
    }

    /// <summary>
    /// Wires a <see cref="ConsoleTraceListener"/> to CMT's internal
    /// TraceSource so that import progress and errors are visible in
    /// the console output in real time, and sets the trace level based
    /// on the <paramref name="verbose"/> flag.
    /// </summary>
    private void TryWireCmtConsoleLogging(ImportCrmDataHandler handler, bool verbose)
    {
        try
        {
            // handler.Logger is a DataMigCommon.Utility.TraceLogger.
            // It exposes a TraceSource via the base class hierarchy.
            object? logger = handler.GetType()
                .GetProperty("Logger", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(handler);

            if (logger is null)
            {
                if (verbose)
                    _logger.LogDebug("CMT Logger property not found — console logging skipped");
                return;
            }

            // Set trace level — Verbose when --verbose, Information otherwise.
            // Without this, CMT's TraceLogger may filter out lower-level messages.
            MethodInfo? setLevel = logger.GetType()
                .GetMethod("SetTraceLevel", BindingFlags.Public | BindingFlags.Instance);
            if (setLevel is not null)
            {
                SourceLevels level = verbose ? SourceLevels.Verbose : SourceLevels.Information;
                setLevel.Invoke(logger, new object[] { level });
            }

            // Try AddTraceListener(TraceListener) — available on TraceLogger.
            MethodInfo? addListener = logger.GetType()
                .GetMethod("AddTraceListener", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(TraceListener) }, null);

            if (addListener is not null)
            {
                addListener.Invoke(logger, new object[] { new ConsoleTraceListener() });
                if (verbose)
                    _logger.LogDebug("CMT console trace listener wired (level: Verbose)");
            }
            else if (verbose)
            {
                _logger.LogDebug("AddTraceListener method not found on CMT Logger");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to wire CMT console logging: {ex.Message}");
        }
    }

    private void OnAddNewProgressItem(object? sender, ProgressItemEventArgs e)
    {
        // "Processing Entity: <name>" is the first AddNewProgressItem fired by
        // ImportCrmEntityActions.BeginEntityImport() — it fires AFTER
        // ImportDataToCrm() has called ImportCommonMethods.ClearCrossReferanceList()
        // (which clears LookupKeys), so this is the correct point to pre-seed the
        // cache. Hooking on "Schema Validation Complete" fires too early — CMT wipes
        // LookupKeys via ClearCrossReferanceList() after that event returns.
        string message = e.progressItem?.ItemText ?? string.Empty;
        if (message.StartsWith("Processing Entity:", StringComparison.OrdinalIgnoreCase)
            && Interlocked.CompareExchange(ref _lookupKeysPrepopulated, 1, 0) == 0)
        {
            PrePopulateLookupKeysFromPackage();
        }

        OnUpdateProgressItem(sender, e);
    }

    private void OnUpdateProgressItem(object? sender, ProgressItemEventArgs e)
    {
        if (e.progressItem is null)
            return;

        string message = e.progressItem.ItemText ?? string.Empty;

        switch (e.progressItem.ItemStatus)
        {
            case ProgressItemStatus.Complete:
                _logger.LogInformation("{Message} - Complete", message);
                break;
            case ProgressItemStatus.Failed:
                Interlocked.Increment(ref _failedStageCount);
                _logger.LogError("{Message} - Stage Failed", message);
                break;
            case ProgressItemStatus.Warning:
                _logger.LogWarning("{Message}", message);
                break;
            default:
                _logger.LogInformation("{Message}", message);
                break;
        }
    }

    /// <summary>
    /// Registers the extracted data package directory for instance-level
    /// assembly probing, so that any DLLs inside the package can be resolved.
    /// </summary>
    private void RegisterExtractedDirectoryForProbing(string directory)
    {
        foreach (string dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                LegacyAssemblyRuntime.PatchDispatcherReferencesOnDisk(dll);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Patching dispatcher references failed for {Dll}.", dll); }
        }
    }

    private Assembly? OnResolveAssembly(object? sender, ResolveEventArgs args)
    {
        string key = (args.Name ?? "<unknown>").Split(',')[0];
        return InstanceResolve(key);
    }

    private Assembly? OnResolveAssemblyLoadContext(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        string key = assemblyName.Name ?? "<unknown>";
        return InstanceResolve(key);
    }

    private Assembly? InstanceResolve(string key)
    {
        if (_assemblyMap.TryGetValue(key, out Assembly? cached))
            return cached;

        // Probe extracted directory (if any DLLs match the name).
        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDir, key + ".dll");

        if (File.Exists(candidate))
        {
            try
            {
                Assembly loaded = Assembly.LoadFrom(candidate);
                _assemblyMap[key] = loaded;
                return loaded;
            }
            // Intentional: best-effort assembly load; falls through to mark as unresolved.
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to load assembly candidate {Candidate}.", candidate); }
        }

        _unresolvedAssemblies.Add(key);
        return null;
    }

    /// <summary>
    /// Pre-seeds <c>ImportCommonMethods.LookupKeys</c> with every record
    /// in the loaded package so that <c>FindEntity()</c> can short-circuit the
    /// cache lookup for internal package references without a server round-trip.
    ///
    /// <para>
    /// CMT's <c>UpsertMultiple</c> handler only sets <c>record.newId</c> when
    /// <c>RecordCreated == true</c> (line 884 of <c>ImportCrmEntityActions.cs</c>).
    /// For records that already exist in the target (<c>RecordCreated=false</c>),
    /// <c>newId</c> stays null, so every child-entity lookup for that parent falls
    /// through to <c>LookupCustomerOrLookupFieldInCRM</c> — one server call per
    /// unique lookup reference.
    /// </para>
    ///
    /// <para>
    /// Because CMT always preserves source GUIDs (the <c>UpsertRequest</c> target
    /// entity's <c>Id</c> is set to the source record GUID), the target GUID is
    /// always equal to the source GUID. Pre-seeding the cache with
    /// <c>"entityName:sourceGuid" → sourceGuid</c> is therefore always correct,
    /// regardless of whether the record is new or pre-existing in the target.
    /// </para>
    ///
    /// <para>
    /// This does not affect external lookups (entities not present in the package)
    /// — those still resolve via the normal server call path.
    /// </para>
    ///
    /// <para>
    /// Accesses CMT internals via reflection because
    /// <c>Microsoft.Xrm.Tooling.Dmt.DataMigCommon</c> is a net462 legacy assembly
    /// that is patched and loaded at runtime — it is not directly referenceable
    /// from the net10 host at compile time.
    /// </para>
    /// </summary>
    private void PrePopulateLookupKeysFromPackage()
    {
        try
        {
            // Navigate to the RUNTIME-loaded DataMigCommon assembly via the
            // handler's type. AppDomain.GetAssemblies() may contain both the
            // compile-time net462 copy and the runtime-loaded copy; they carry
            // separate static state, so we must find the one CMT actually uses.
            Type? importCommonType = null;
            if (_handlerRuntimeType != null)
            {
                // Walk ImportProcessor's referenced assemblies to find DataMigCommon.
                foreach (var asmRef in _handlerRuntimeType.Assembly.GetReferencedAssemblies())
                {
                    if (asmRef.Name?.Contains("DataMigCommon", StringComparison.OrdinalIgnoreCase) != true)
                        continue;

                    // Get the already-loaded instance with the same name.
                    var loaded = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, asmRef.Name, StringComparison.OrdinalIgnoreCase));
                    if (loaded == null) continue;

                    importCommonType = loaded.GetType(
                        "Microsoft.Xrm.Tooling.Dmt.DataMigCommon.DataInteraction.ImportCommonMethods");
                    if (importCommonType != null) break;
                }
            }

            // Fallback: scan all loaded assemblies (less precise but still useful).
            if (importCommonType == null)
            {
                importCommonType = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name?.Contains("DataMigCommon", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(a => a.GetType("Microsoft.Xrm.Tooling.Dmt.DataMigCommon.DataInteraction.ImportCommonMethods"))
                    .FirstOrDefault(t => t != null);
            }

            if (importCommonType == null)
            {
                _logger.LogInformation("LookupKeys pre-population skipped — ImportCommonMethods type not found in loaded assemblies");
                return;
            }

            _logger.LogInformation("LookupKeys pre-population: using type from assembly {Assembly}",
                importCommonType.Assembly.FullName);

            // Retrieve the static LookupKeys ConcurrentDictionary<string, Guid>.
            var lookupKeysField = importCommonType.GetField(
                "LookupKeys", BindingFlags.Public | BindingFlags.Static);
            object? lookupKeys = lookupKeysField?.GetValue(null);
            if (lookupKeys == null)
            {
                _logger.LogInformation("LookupKeys pre-population skipped — LookupKeys field not found or null");
                return;
            }

            // Get TryAdd(string, Guid) on the ConcurrentDictionary.
            MethodInfo? tryAdd = lookupKeys.GetType().GetMethod(
                "TryAdd", [typeof(string), typeof(Guid)]);
            if (tryAdd == null)
            {
                _logger.LogInformation("LookupKeys pre-population skipped — TryAdd method not found on LookupKeys");
                return;
            }

            // Retrieve the static dataEntities property.
            var dataEntitiesProp = importCommonType.GetProperty(
                "dataEntities", BindingFlags.Public | BindingFlags.Static);
            object? dataEntities = dataEntitiesProp?.GetValue(null);
            if (dataEntities == null)
            {
                _logger.LogInformation("LookupKeys pre-population skipped — dataEntities not loaded yet");
                return;
            }

            // entities.entity → entitiesEntity[]
            var entityArrayProp = dataEntities.GetType().GetProperty("entity");
            if (entityArrayProp?.GetValue(dataEntities) is not System.Collections.IEnumerable entityArray)
            {
                _logger.LogDebug("LookupKeys pre-population skipped — entity array not found");
                return;
            }

            int count = 0;
            int entityCount = 0;
            foreach (object? entity in entityArray)
            {
                if (entity == null) continue;
                entityCount++;

                string? entityName = entity.GetType()
                    .GetProperty("name")?.GetValue(entity) as string;
                if (string.IsNullOrEmpty(entityName)) continue;

                // entitiesEntity.records → entitiesEntityRecord[]
                var recordsProp = entity.GetType().GetProperty("records");
                if (recordsProp?.GetValue(entity) is not System.Collections.IEnumerable records) continue;

                foreach (object? record in records)
                {
                    if (record == null) continue;
                    string? id = record.GetType()
                        .GetProperty("id")?.GetValue(record) as string;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!Guid.TryParse(id, out Guid guid)) continue;

                    string key = string.Concat(entityName, ":", id);
                    tryAdd.Invoke(lookupKeys, [key, guid]);
                    count++;
                }
            }

            _logger.LogInformation(
                "Pre-populated LookupKeys cache with {Count} records across {EntityCount} entities — "
                + "eliminates server lookup calls for internal package references",
                count, entityCount);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "LookupKeys pre-population failed — import will proceed with standard CMT lookup behavior");
        }
    }
}
