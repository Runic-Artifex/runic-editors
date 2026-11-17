using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// Refresh ViewModels (the tool window's toolbar): finds the solution's Runic SDK projects again and reloads each one's
/// newest development source map into the ViewModels section (VS Code: <c>runic.viewModels.refresh</c>).
/// </summary>
internal sealed class RefreshViewModelsCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.RefreshViewModels;

    public override Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
        => RunicSdkState.Instance.RescanAsync(cancellationToken);
}
