using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TALXIS.CLI.Features.Data.DataModelConverter.Model;
using TALXIS.CLI.Logging;

namespace TALXIS.CLI.Features.Data.DataModelConverter.AppScope;

/// <summary>
/// Narrows tables to the columns something in the app actually mentions.
///
/// This searches for known column names rather than parsing each artefact type: the set of
/// columns is already known by the time this runs, so the question is only which of those
/// names appear anywhere. That makes the scan indifferent to artefact schemas, and it errs
/// toward keeping a column rather than dropping one — a name shared by two tables keeps the
/// column on both.
/// </summary>
public static class AttributeReferenceFilter
{
    private static readonly ILogger _logger = TxcLoggerFactory.CreateLogger(nameof(AttributeReferenceFilter));

    /// <summary>Identifier-shaped tokens; column logical names are always of this shape.</summary>
    private static readonly Regex TokenPattern = new(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Folders whose contents reference columns. Entity declarations are deliberately not
    /// among them: an entity declares its own columns, so scanning one would report every
    /// column as referenced by itself.
    /// </summary>
    private static readonly string[] ReferencingFolders =
        ["FormXml", "SavedQueries", "Workflows", "Visualizations", "AppModuleSiteMaps", "AppModules"];

    private static readonly string[] CodeExtensions = [".cs", ".ts", ".js"];

    public static void Apply(List<Table> tables, List<Relationship> relationships, ResolvedAppScope scope)
    {
        var referenced = CollectReferencedTokens(scope.SearchRoots, scope.ScanCode);

        // Computed before anything is dropped. The translators read
        // Relationship.LeftSideRow/RighSideRow without a null check, so removing a row an
        // edge points at turns a narrower diagram into a crash on the sql and edmx targets.
        var loadBearing = new HashSet<TableRow>();
        foreach (var relationship in relationships)
        {
            if (relationship.LeftSideRow != null) loadBearing.Add(relationship.LeftSideRow);
            if (relationship.RighSideRow != null) loadBearing.Add(relationship.RighSideRow);
        }

        foreach (var table in tables.Where(t => t.Type == TableType.InSolution))
        {
            var dropped = table.Rows
                .Where(r => r.RowType != RowType.Primarykey
                            && !loadBearing.Contains(r)
                            && !referenced.Contains(r.Name))
                .ToList();

            foreach (var row in dropped)
            {
                table.Rows.Remove(row);
                scope.DroppedColumns.Add($"{table.LogicalName}.{row.Name}");
            }
        }

        _logger.LogInformation(
            "Kept columns referenced by forms, views, workflows and sitemaps{Code}; dropped {Dropped}. "
            + "A dropped column is one no reference was found for, which is not the same as one that is unused.",
            scope.ScanCode ? ", and by .cs/.ts sources" : " (plug-in and script sources were not scanned)",
            scope.DroppedColumns.Count);
    }

    private static HashSet<string> CollectReferencedTokens(IEnumerable<string> searchRoots, bool scanCode)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in searchRoots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct())
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!ShouldScan(file, scanCode)) continue;

                try
                {
                    foreach (Match match in TokenPattern.Matches(File.ReadAllText(file)))
                    {
                        tokens.Add(match.Value);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not read {File} while looking for column references.", file);
                }
            }
        }

        return tokens;
    }

    private static bool ShouldScan(string file, bool scanCode)
    {
        if (scanCode && CodeExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Entity.xml declares columns rather than referencing them.
        if (string.Equals(Path.GetFileName(file), "Entity.xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(file) ?? string.Empty;
        return ReferencingFolders.Any(folder =>
            directory.Contains(Path.DirectorySeparatorChar + folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || directory.EndsWith(Path.DirectorySeparatorChar + folder, StringComparison.OrdinalIgnoreCase));
    }
}
