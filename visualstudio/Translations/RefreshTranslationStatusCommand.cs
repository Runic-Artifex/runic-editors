using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Refresh Translation Status (the Runic tool window's toolbar and the Translations section): finds the solution's
/// runic.json projects again and reads each one's <c>runic-translations status</c> (VS Code: <c>runic.translations.refreshStatusView</c>).
/// </summary>
internal sealed class RefreshTranslationStatusCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.RefreshTranslationStatus;

    public override Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
        => TranslationStatusState.Instance.RefreshAsync(cancellationToken);
}
