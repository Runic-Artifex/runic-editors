using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Tools → Runic → Import XLIFF (Dry Run)…: <c>runic-translations xliff import --dry-run</c> of an XLIFF 2.1 file the user
/// picks. It reports each unit's outcome in the Runic Translations Output pane and writes nothing, as VS Code's
/// <c>runic.translations.importXliffDryRun</c>.
/// </summary>
internal sealed class ImportXliffDryRunCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.ImportXliffDryRun;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        string? project = await TranslationsWorkspace.ChooseProjectAsync(cancellationToken);
        if (project == null) return;
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? source = FilePickers.File("Check import", Path.GetDirectoryName(project), "XLIFF 2.1 (*.xliff;*.xlf)|*.xliff;*.xlf");
        if (source == null) return;
        string stdout = await TranslationsWorkspace.RunToolAsync(project, TranslationsTool.XliffImportDryRunArguments(project, source), cancellationToken);
        await TranslationsWorkspace.ShowOutputAsync(cancellationToken);
        await TranslationsWorkspace.InformAsync(TranslationsTool.XliffDryRunMessage(stdout), cancellationToken);
    }
}
