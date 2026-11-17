using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Output;

/// <summary>
/// Tools → Runic → Show Translations Output: the "Runic Translations" Output pane, with the language server's standard
/// error, start failures and, when Tools → Options → Runic DevKit → Trace is on, its messages.
/// </summary>
internal sealed class ShowTranslationsOutputCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.ShowTranslationsOutput;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
        => await RunicOutput.ShowAsync(await RunicOutput.TranslationsAsync(cancellationToken), cancellationToken);
}
