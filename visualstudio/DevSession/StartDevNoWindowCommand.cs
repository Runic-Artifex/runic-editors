using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// Start dotnet runic dev --no-window (VS Code: <c>runic.dev.startNoWindow</c>): the frontend only; F5 starts the
/// Window with the session file's environment (owner <c>ide</c>).
/// </summary>
internal sealed class StartDevNoWindowCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.StartDevNoWindow;

    public override Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
        => StartDevCommand.StartAsync(package, noWindow: true, cancellationToken);
}
