using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Tools → Runic → Import .resx or i18next Catalog…: migration through the local tool's importer (the Translations
/// Editor's too), as VS Code's <c>runic.translations.importCatalog</c>. A dry run first, whose per-message outcome goes to
/// the Runic Translations Output pane, then the import after confirmation, optionally overwriting existing text.
/// </summary>
internal sealed class ImportCatalogCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.ImportCatalog;

    private const string Import = "Import";
    private const string Overwrite = "Import and overwrite existing";

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        string? project = await TranslationsWorkspace.ChooseProjectAsync(cancellationToken);
        if (project == null) return;
        string? root = await TranslationsWorkspace.SolutionDirectoryAsync(cancellationToken) ?? Path.GetDirectoryName(project);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        // Windows has no picker for a file or a folder at once, so an i18next directory is its own choice.
        var format = ChoiceDialog.Pick("Import from", new[]
        {
            new Choice<(CatalogFormat From, bool Folder)>(".resx", "Strings.resx with its Strings.<locale>.resx satellites", null, (CatalogFormat.Resx, false)),
            new Choice<(CatalogFormat From, bool Folder)>("i18next", "<lng>.json", null, (CatalogFormat.I18next, false)),
            new Choice<(CatalogFormat From, bool Folder)>("i18next directory", "a directory of <lng>.json or <lng>/<namespace>.json files", null, (CatalogFormat.I18next, true)),
        });
        if (format == null) return;
        var (from, folder) = format.Value;
        string? source = folder ? FilePickers.Folder("Check import", root)
            : FilePickers.File("Check import", root, from == CatalogFormat.Resx ? ".resx (*.resx)|*.resx" : "i18next JSON (*.json)|*.json");
        if (source == null) return;

        string dryRun = await TranslationsWorkspace.RunToolAsync(project, TranslationsTool.ImportArguments(project, from, source, dryRun: true), cancellationToken);
        await TranslationsWorkspace.ShowOutputAsync(cancellationToken);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string question = TranslationsTool.ImportDryRunQuestion(dryRun);
        bool overwrite;
        if (TranslationsTool.KeptExisting(dryRun))
        {
            var choice = ChoiceDialog.Pick("Import", new[]
            {
                new Choice<bool>(Import, "keep the existing text", null, false),
                new Choice<bool>(Overwrite, "replace the existing text (--overwrite)", null, true),
            }, question);
            if (choice == null) return;
            overwrite = choice.Value;
        }
        else
        {
            if (!await TranslationsWorkspace.ConfirmAsync(question + "\n\nImport now?", cancellationToken)) return;
            overwrite = false;
        }
        string result = await TranslationsWorkspace.RunToolAsync(project, TranslationsTool.ImportArguments(project, from, source, dryRun: false, overwrite), cancellationToken);
        TranslationStatusState.Instance.Invalidate();
        await TranslationsWorkspace.InformAsync(TranslationsTool.ImportedMessage(from, result), cancellationToken);
    }
}
