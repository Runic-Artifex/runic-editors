using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Tools → Runic → Export XLIFF…: <c>runic-translations xliff export</c> of the project (one XLIFF 2.1 document per
/// translated locale) into a folder the user picks, through the local tool, as VS Code's <c>runic.translations.exportXliff</c>.
/// The tool's output goes to the Runic Translations Output pane.
/// </summary>
internal sealed class ExportXliffCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.ExportXliff;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        string? project = await TranslationsWorkspace.ChooseProjectAsync(cancellationToken);
        if (project == null) return;
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? target = FilePickers.Folder("Export XLIFF here", Path.GetDirectoryName(project));
        if (target == null) return;
        string stdout = await TranslationsWorkspace.RunToolAsync(project, TranslationsTool.XliffExportArguments(project, target), cancellationToken);
        await TranslationsWorkspace.InformAsync(TranslationsTool.ExportedMessage(stdout, target), cancellationToken);
    }
}
