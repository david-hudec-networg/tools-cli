using TALXIS.CLI.Core.Contracts.PowerPlatform;
using TALXIS.CLI.Core.DependencyInjection;
using TALXIS.CLI.Features.Security;
using TALXIS.CLI.Platform.PowerPlatform.Control;

namespace TALXIS.CLI.Features.Security.Role;

internal static class SecurityRoleCommandSupport
{
    public static async Task<IReadOnlyList<PowerPlatformRoleDefinition>> ListRolesAsync(
        string? profile,
        string? filter,
        CancellationToken ct)
    {
        var context = await SecurityPrincipalCommandSupport.ResolveContextAsync(profile, ct).ConfigureAwait(false);
        var resolver = TxcServices.Get<SecurityRoleResolver>();
        return await resolver.ListTenantRolesAsync(context.Connection, context.Credential, filter, ct).ConfigureAwait(false);
    }

    public static async Task<PowerPlatformRoleDefinition> GetRoleAsync(
        string? profile,
        string role,
        CancellationToken ct)
    {
        var context = await SecurityPrincipalCommandSupport.ResolveContextAsync(profile, ct).ConfigureAwait(false);
        var resolver = TxcServices.Get<SecurityRoleResolver>();
        return await resolver.GetTenantRoleAsync(context.Connection, context.Credential, role, ct).ConfigureAwait(false);
    }
}
