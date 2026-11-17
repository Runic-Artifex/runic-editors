using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Output;

/// <summary>Tools → Runic → Show Runic SDK Output, also on the Runic tool window's toolbar: the "Runic SDK" Output pane.</summary>
internal sealed class ShowSdkOutputCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.ShowSdkOutput;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
        => await RunicOutput.ShowAsync(await RunicOutput.SdkAsync(cancellationToken), cancellationToken);
}
