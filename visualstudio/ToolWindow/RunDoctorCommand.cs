using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// Run dotnet runic doctor (Tools → Runic, the tool window's toolbar, the Error List's context menu): shows the Runic tool
/// window and runs <c>dotnet runic doctor --format json --fail-on never</c> for the Runic SDK project of the active
/// document, or for every one, into the Doctor section and the Error List (VS Code: <c>runic.doctor.run</c>).
/// </summary>
internal sealed class RunDoctorCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.RunDoctor;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        await package.ShowToolWindowAsync(typeof(RunicToolWindow), 0, create: true, cancellationToken);
        await RunicSdkState.Instance.RunDoctorAsync(null, cancellationToken);
    }
}
