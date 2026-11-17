using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// Stop dotnet runic dev (VS Code: <c>runic.dev.stop</c>): ends the <c>dev</c> this extension started, with its Window
/// and frontend server. A <c>dev</c> started in a terminal has to be stopped there with Ctrl+C: Windows has no clean
/// Ctrl+C for another console (PARITY §7.3; VS Code on Windows says the same).
/// </summary>
internal sealed class StopDevCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.StopDev;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        var service = DevSessionService.Instance;
        await service.StopAsync(await service.PickAsync(null, cancellationToken), cancellationToken);
    }
}
