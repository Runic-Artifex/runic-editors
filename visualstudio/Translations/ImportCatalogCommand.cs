using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>Stub. VS-6: imports a .resx or i18next catalog (dry run, then confirm).</summary>
internal sealed class ImportCatalogCommand : NotAvailableYetHandler
{
    public override int Id => RunicCommandIds.ImportCatalog;
}
