using DotMake.CommandLine;
using Microsoft.Extensions.Logging;
using TALXIS.CLI.Core;
using TALXIS.CLI.Logging;

namespace TALXIS.CLI.Features.Security.ServicePrincipal;

/// <summary>
/// Lists Entra applications available for tenant-wide role assignment.
/// Usage: <c>txc security service-principal list [--filter &lt;name&gt;]</c>
/// </summary>
[CliReadOnly]
[CliCommand(
    Name = "list",
    Description = "List Entra applications that can be targeted by txc security service-principal role commands."
)]
public class ServicePrincipalListCliCommand : ProfiledCliCommand
{
    protected override ILogger Logger { get; } = TxcLoggerFactory.CreateLogger(nameof(ServicePrincipalListCliCommand));

    [CliOption(Name = "--filter", Description = "Show only Entra applications whose display name starts with this value.", Required = false)]
    public string? Filter { get; set; }

    protected override async Task<int> ExecuteAsync()
    {
        var rows = await SecurityServicePrincipalCommandSupport.ListServicePrincipalsAsync(Profile, Filter, CancellationToken.None).ConfigureAwait(false);
        OutputFormatter.WriteList(rows, SecurityServicePrincipalCommandSupport.WriteServicePrincipalTable);
        return ExitSuccess;
    }
}
