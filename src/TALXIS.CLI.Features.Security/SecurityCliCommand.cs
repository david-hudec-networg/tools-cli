using DotMake.CommandLine;

namespace TALXIS.CLI.Features.Security;

[CliCommand(
    Name = "security",
    Description = "Discover and manage tenant-wide resources and role assignments.",
    Children = new[] { typeof(Role.RoleCliCommand), typeof(ServicePrincipal.ServicePrincipalCliCommand), typeof(User.UserCliCommand), typeof(Group.GroupCliCommand) },
    ShortFormAutoGenerate = CliNameAutoGenerate.None
)]
public class SecurityCliCommand
{
    public void Run(CliContext context)
    {
        context.ShowHelp();
    }
}
