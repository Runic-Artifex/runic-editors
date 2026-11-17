using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Settings;

/// <summary>Tools → Runic → Options...: opens Tools → Options → Runic DevKit (<see cref="RunicOptionsPage"/>).</summary>
internal sealed class OpenOptionsCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.OpenOptions;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        package.ShowOptionPage(typeof(RunicOptionsPage));
    }
}
