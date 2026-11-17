using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Output;

/// <summary>Stub. VS-2: the Runic Translations Output pane; server stderr and LSP trace go there.</summary>
internal sealed class ShowTranslationsOutputCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.ShowTranslationsOutput;
}
