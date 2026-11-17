using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>View → Other Windows → Runic, and Tools → Runic → Runic.</summary>
internal sealed class ShowToolWindowCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.ShowToolWindow;

    public override Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
        => package.ShowToolWindowAsync(typeof(RunicToolWindow), 0, create: true, cancellationToken);
}
