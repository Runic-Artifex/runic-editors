using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Output;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Tools → Runic → Override Library Message… (also the MF2 editor's context menu), as VS Code's
/// <c>runic.translations.overrideLibraryMessage</c>: choose an installed library catalog (<c>runic/libraryCatalogs</c> from
/// the language server), a message and a locale; <c>runic-translations overrides add</c> writes
/// <c>overrides/&lt;catalog&gt;/&lt;locale&gt;.rmf2</c> seeded with the library's text (listing the catalog in runic.json
/// "overrides" when needed), and the file opens at the message.
/// </summary>
internal sealed class OverrideLibraryMessageCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.OverrideLibraryMessage;

    public const string ServerNotRunning = "The Runic language server is not running. Open an MF2 document of the project to start it, then run Override Library Message… again.";

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        string? project = await TranslationsWorkspace.ChooseProjectAsync(cancellationToken);
        if (project == null) return;
        var client = await TranslationsWorkspace.LanguageClientAsync(cancellationToken);
        if (!client.IsRunning) throw new InvalidOperationException(ServerNotRunning);
        var answer = await client.RequestAsync("runic/libraryCatalogs", new { uri = new Uri(project).AbsoluteUri }, cancellationToken);
        await TaskScheduler.Default;
        var result = LibraryCatalogs.Parse(answer.ToString(Newtonsoft.Json.Formatting.None));
        var pane = await RunicOutput.TranslationsAsync(cancellationToken);
        foreach (string warning in result.Warnings) TranslationsWorkspace.WriteLine(pane, warning);
        var catalogs = LibraryCatalogs.CatalogChoices(result);
        if (catalogs.Count == 0) throw new InvalidOperationException(LibraryCatalogs.NoCatalog);

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var catalog = ChoiceDialog.Pick("Library catalog to override", catalogs)?.Value;
        if (catalog == null) return;
        var message = ChoiceDialog.Pick($"Message of {catalog.Catalog} to override", LibraryCatalogs.MessageChoices(catalog))?.Value;
        if (message == null) return;
        string? locale = ChoiceDialog.Pick($"Locale to override {message.Path} in", LibraryCatalogs.LocaleChoices(catalog, message))?.Value;
        if (locale == "")
            locale = ChoiceDialog.Ask("Another locale", "BCP 47 locale tag the application adds, such as fr or de-CH",
                value => LibraryCatalogs.CanonicalLocale(value) != null ? null : "Use a BCP 47 locale tag.");
        if (string.IsNullOrEmpty(locale)) return;
        string tag = LibraryCatalogs.CanonicalLocale(locale) ?? throw new InvalidOperationException($"\"{locale}\" is not a BCP 47 locale tag.");
        string path = LibraryCatalogs.OverrideSourcePath(project, catalog.Catalog, tag);

        if (!LibraryCatalogs.IsOverriddenIn(message, tag))
        {
            // The tool writes the override source and possibly runic.json; unsaved edits there would be overwritten underneath the editor.
            var open = TranslationsWorkspace.OpenDocuments();
            var unsaved = LibraryCatalogs.Unsaved(open.Select(entry => entry.Document), new[] { path, project }, ignoreCase: true);
            if (unsaved.Count > 0)
            {
                string names = string.Join(", ", unsaved.Select(document => Path.GetFileName(document.Path)));
                if (!await TranslationsWorkspace.ConfirmAsync($"{names} has unsaved changes that runic-translations overrides add would overwrite. Save and continue?", cancellationToken)) return;
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                foreach (var entry in open.Where(entry => unsaved.Contains(entry.Document)))
                {
                    entry.Source.Save();
                    if (!entry.Source.Saved) throw new InvalidOperationException($"Could not save {entry.Document.Path}.");
                }
            }
            await TranslationsWorkspace.RunToolAsync(project, TranslationsTool.OverridesAddArguments(project, catalog.Catalog, tag, message.Key), cancellationToken);
            TranslationStatusState.Instance.Invalidate();
        }
        await TaskScheduler.Default;
        int line = File.Exists(path) ? LibraryCatalogs.MessageLine(File.ReadAllText(path), message.Path) : 0;
        await TranslationsWorkspace.OpenAsync(path, line, cancellationToken);
    }
}
