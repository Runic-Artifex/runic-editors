using System;
using System.IO;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// The Translations commands and section against the fixtures of VS Code's <c>vscode/test/unit.test.ts</c> (copied to
/// <c>Core.Tests/Fixtures</c>: <c>translations-status.json</c>, <c>library-catalogs.json</c>) and the
/// <c>fixtures/rmf2</c> project of both extensions' host tests.
/// </summary>
public class TranslationsTests
{
    private static readonly string Repository = FindRepository();
    private static readonly string Fixtures = Path.Combine(Repository, "visualstudio", "Core.Tests", "Fixtures");
    private static readonly string Rmf2 = Path.Combine(Repository, "fixtures", "rmf2");

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "visualstudio", "Commands.vsct"))) return directory.FullName;
        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(Fixtures, name));

    private static string Temporary(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"runic-vs-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- The local tool and its arguments ----------------------------------------------------------------------------

    [Test]
    public void Commands_run_the_local_tool_of_the_nearest_manifest_and_keep_argument_boundaries()
    {
        string project = Path.Combine(Rmf2, "runic.json");
        string output = Path.Combine(Rmf2, "out dir");
        // fixtures/rmf2 has no tool manifest; the repository's .config/dotnet-tools.json declares runic-translations.
        var launch = TranslationsTool.Find(Rmf2, "dotnet", null, TranslationsTool.XliffExportArguments(project, output));
        Check.Equal(Repository, launch.Directory);
        Check.Same(new[] { "tool", "run", "runic-translations", "--", "xliff", "export", "--project", project, "--output", output }, launch.Arguments);
        Check.True(launch.CommandLineArguments.EndsWith($"--output \"{output}\"", StringComparison.Ordinal), launch.CommandLineArguments);
        Check.Same(new[] { "xliff", "import", "--project", project, "--source", "de file.xliff", "--dry-run" }, TranslationsTool.XliffImportDryRunArguments(project, "de file.xliff"));
        Check.Same(new[] { "status", "--project", "/w/runic.json", "--format", "json" }, TranslationsTool.StatusArguments("/w/runic.json"));
        Check.Same(new[] { "import", "--from", "resx", "/w/Strings resx/Strings.resx", "--project", "/w/runic.json", "--dry-run" },
            TranslationsTool.ImportArguments("/w/runic.json", CatalogFormat.Resx, "/w/Strings resx/Strings.resx", dryRun: true));
        Check.Same(new[] { "import", "--from", "i18next", "/w/locales", "--project", "/w/runic.json", "--overwrite" },
            TranslationsTool.ImportArguments("/w/runic.json", CatalogFormat.I18next, "/w/locales", dryRun: false, overwrite: true));
    }

    [Test]
    public void A_configured_server_assembly_runs_directly_and_a_root_manifest_without_the_tool_stops_the_search()
    {
        string root = Temporary("tool");
        try
        {
            string dll = Path.Combine(root, "server with spaces.dll");
            File.WriteAllText(dll, "fixture");
            var launch = TranslationsTool.Find(root, "dotnet", RunicSettings.ServerAssembly(dll, null, null), TranslationsTool.StatusArguments("runic.json"));
            Check.Same(new[] { dll, "status", "--project", "runic.json", "--format", "json" }, launch.Arguments);
            Check.Equal(root, launch.Directory);

            string missing = Path.Combine(root, "missing.dll");
            var error = Throws(() => TranslationsTool.Find(root, "dotnet", RunicSettings.ServerAssembly(null, missing, null), Array.Empty<string>()));
            Check.True(error.Contains(RunicSettings.ServerAssemblyVariable) && error.Contains(missing), error);

            Directory.CreateDirectory(Path.Combine(root, ".config"));
            File.WriteAllText(Path.Combine(root, ".config", "dotnet-tools.json"), "{\"isRoot\": true, \"tools\": {\"dotnet-runic\": {\"commands\": [\"runic\"]}}}");
            string nested = Path.Combine(root, "app");
            Directory.CreateDirectory(nested);
            Check.Equal(TranslationsTool.ToolMissing, Throws(() => TranslationsTool.Find(nested, "dotnet", null, Array.Empty<string>())));
            File.WriteAllText(Path.Combine(nested, "runic.json"), "{}");
            Directory.CreateDirectory(Path.Combine(nested, ".config"));
            File.WriteAllText(Path.Combine(nested, ".config", "dotnet-tools.json"), "{\"tools\": {\"dotnet-runic-translations\": {\"commands\": [\"runic-translations\"]}}}");
            Check.Equal(nested, TranslationsTool.Find(nested, "dotnet", null, Array.Empty<string>()).Directory);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void Commands_pick_the_project_of_the_active_file_the_nearest_runic_json_within_the_solution()
    {
        string root = Temporary("nearest");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "apps", "shop", "translations"));
            Directory.CreateDirectory(Path.Combine(root, "apps", "admin"));
            Check.Equal(null, TranslationsTool.ProjectManifest(root));
            File.WriteAllText(Path.Combine(root, "runic.json"), "{}");
            File.WriteAllText(Path.Combine(root, "apps", "shop", "translations", "runic.json"), "{}");
            Check.Equal(Path.Combine(root, "apps", "shop", "translations", "runic.json"), TranslationsTool.NearestProjectManifest(Path.Combine(root, "apps", "shop", "translations", "de.rmf2"), root));
            Check.Equal(Path.Combine(root, "runic.json"), TranslationsTool.NearestProjectManifest(Path.Combine(root, "apps", "admin", "en.rmf2"), root));
            Check.Equal(null, TranslationsTool.NearestProjectManifest(Path.Combine(root, "apps", "admin", "en.rmf2"), Path.Combine(root, "apps")));
            Check.Equal(null, TranslationsTool.NearestProjectManifest(Path.Combine(Path.GetTempPath(), "elsewhere.rmf2"), root));
            Check.Equal(Path.Combine(root, "runic.json"), TranslationsTool.ProjectManifest(root));
            // The fixture's feature directory belongs to the project above it.
            Check.Equal(Path.Combine(Rmf2, "runic.json"), TranslationsTool.NearestProjectManifest(Path.Combine(Rmf2, "account", "de.rmf2"), Repository));
            Check.Equal("apps/shop/translations", TranslationStatus.ProjectLabel(Path.Combine(root, "apps", "shop", "translations", "runic.json"), root));
            Check.Equal("translations", TranslationStatus.ProjectLabel(Path.Combine(root, "apps", "shop", "translations", "runic.json"), null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void Command_messages_come_from_the_tool_output()
    {
        const string export = "wrote out/de.xliff\nwrote out/fr.xliff\n";
        Check.Equal("Exported 2 XLIFF document(s) to C:/out.", TranslationsTool.ExportedMessage(export, "C:/out"));
        const string dryRun = "unit payment: updated\r\ndry run: 3 updated, 1 unchanged\r\n2 existing messages were kept; --overwrite replaces them\r\n";
        Check.Equal("dry run: 3 updated, 1 unchanged Nothing was written; run runic-translations xliff import or the Translations Editor to apply it.", TranslationsTool.XliffDryRunMessage(dryRun));
        Check.True(TranslationsTool.KeptExisting(dryRun), "kept");
        Check.True(!TranslationsTool.KeptExisting("dry run: 3 new"), "nothing kept");
        Check.Equal("Dry run finished.", TranslationsTool.DryRunSummary(""));
        Check.Equal("Imported .resx: wrote 1 file(s). See the Runic Translations output for each message.", TranslationsTool.ImportedMessage(CatalogFormat.Resx, "wrote de.rmf2\n"));
        var failed = new TranslationsToolResult(1, "", "error: something\nRTR0001: The catalog does not compile.\n");
        Check.Equal("runic-translations xliff export failed (RTR0001: The catalog does not compile.). See the Runic Translations output.",
            TranslationsTool.FailureMessage(TranslationsTool.XliffExportArguments("runic.json", "out"), failed));
        Check.Equal("runic-translations status --project failed (exit code 3). See the Runic Translations output.",
            TranslationsTool.FailureMessage(TranslationsTool.StatusArguments("runic.json"), new TranslationsToolResult(3, " ", "")));
    }

    // ---- Translation status -------------------------------------------------------------------------------------------

    [Test]
    public void Status_reads_coverage_thresholds_and_completeness_policy_as_the_command_line_reports_them()
    {
        var result = TranslationStatus.Parse(Fixture("translations-status.json"));
        var swiss = TranslationStatus.LocaleStatus(result, "DE-ch")!;
        Check.Equal("Coverage de-CH: 87.5% (30 translated, 5 from de, 5 missing, 1 stale), below its minimum 90%; missing translations are warnings", TranslationStatus.CoverageLine(swiss));
        Check.Equal("Coverage en: 100% (40 translated, 0 missing)", TranslationStatus.CoverageLine(TranslationStatus.LocaleStatus(result, "en")!));
        Check.Equal(null, TranslationStatus.LocaleStatus(result, "fr"));
        Check.Equal("Translation Status · app", TranslationStatus.Title(result));

        string markdown = TranslationStatus.Markdown(result);
        Check.True(markdown.Contains("| Locale | Translated | Fallback | Missing | Stale | Coverage | Missing translations |"), markdown);
        Check.True(markdown.Contains("| en (base) | 40 | 0 | 0 | – | 100% | – |"), markdown);
        Check.True(markdown.Contains("| **de-CH → de** ⚠ | 30 | 5 | 5 | 1 | **87.5% (min 90%)** below minimum | warning |"), markdown);
        Check.True(markdown.Contains("review file app.review.json"), markdown);

        Check.Equal("The catalog does not compile.", Throws(() => TranslationStatus.Parse("{\"success\": false, \"payloadType\": null, \"payload\": null, \"fault\": {\"message\": \"The catalog does not compile.\"}}")));
        Check.Equal("runic-translations status returned no status report.", Throws(() => TranslationStatus.Parse("Unhandled exception.")));
        Check.Equal("runic-translations status returned an invalid status report.", Throws(() => TranslationStatus.Parse("{\"payloadType\": \"runic.translations.status/1\", \"payload\": {}}")));
        Check.Equal("87.25%", TranslationStatus.Percent(87.25));
        Check.Equal("33.33%", TranslationStatus.Percent(100.0 / 3));
    }

    [Test]
    public void The_section_lists_each_locale_with_its_coverage_and_one_row_per_project_when_there_are_several()
    {
        string manifest = Path.Combine(Rmf2, "runic.json");
        Check.Equal(0, TranslationStatus.Rows(Array.Empty<TranslationsProjectState>(), Repository).Count);
        var state = new TranslationsProjectState(manifest);
        // Not read yet: the welcome text and Show Translation Status.
        Check.Equal(0, TranslationStatus.Rows(new[] { state }, Repository).Count);
        state.Running = true;
        Check.Same(new[] { "Reading translation status…" }, TranslationStatus.Rows(new[] { state }, Repository).Select(row => row.Label));
        state.Running = false; state.Loaded = true; state.Error = TranslationsTool.ToolMissing.Split('.')[0];
        var unavailable = TranslationStatus.Rows(new[] { state }, Repository).Single();
        Check.Equal(RowIcon.Warning, unavailable.Icon);
        Check.Equal("Status unavailable: No project-local runic-translations tool was found", unavailable.Label);

        state.Error = null; state.Result = TranslationStatus.Parse(Fixture("translations-status.json"));
        var rows = TranslationStatus.Rows(new[] { state }, Repository);
        Check.Same(new[] { "en — 100% (base)", "de-CH — 87.5%" }, rows.Select(row => row.ToString()));
        Check.Same(new[] { RowIcon.Pass, RowIcon.Warning }, rows.Select(row => row.Icon));
        Check.True(rows.All(row => row.Project == manifest), "rows carry the project");
        Check.Equal(TranslationStatus.CoverageLine(state.Result.Projects[0].Locales[1]), rows[1].Tooltip);

        var other = new TranslationsProjectState(Path.Combine(Repository, "apps", "admin", "runic.json")) { Loaded = true };
        var projects = TranslationStatus.Rows(new[] { state, other }, Repository);
        Check.Same(new[] { "fixtures/rmf2 — app", "apps/admin" }, projects.Select(row => row.ToString()));
        Check.Same(new[] { "en", "de-CH" }, projects[0].Children.Select(row => row.Label));
        Check.Same(new[] { TranslationStatus.Unavailable }, projects[1].Children.Select(row => row.Label));
    }

    // ---- Library catalogs ---------------------------------------------------------------------------------------------

    [Test]
    public void Override_Library_Message_offers_installed_catalogs_messages_and_locales_and_runs_overrides_add()
    {
        var result = LibraryCatalogs.Parse(Fixture("library-catalogs.json"));
        var catalogs = LibraryCatalogs.CatalogChoices(result);
        Check.Same(new[] { "acme.table", "acme.chart" }, catalogs.Select(choice => choice.Label));
        Check.Equal("Acme.Table 1.0.0", catalogs[0].Description);
        Check.Equal("Overridden in de-CH", catalogs[0].Detail);
        Check.True(catalogs[1].Detail!.Contains("runic.json gets an \"overrides\" entry"), catalogs[1].Detail!);

        var messages = LibraryCatalogs.MessageChoices(catalogs[0].Value);
        Check.Same(new[] { "header.sort — Sort by {$column}", "rows — .input {$count :integer}" }, messages.Select(choice => choice.ToString()));
        Check.Equal("Overridden in de-CH; library text changed since the override in de-CH; Inputs: $column: string", messages[0].Detail);
        Check.Equal(null, messages[1].Detail);

        var locales = LibraryCatalogs.LocaleChoices(catalogs[0].Value, messages[0].Value);
        Check.Same(new[] { "library base locale", "added by the application, falls back to de", "a locale the library lacks" }, locales.Select(choice => choice.Description));
        Check.Equal("1/2 messages in its own text; this message is overridden here", locales[1].Detail);
        Check.Equal(LibraryCatalogs.AnotherLocale, locales[2].Label);
        Check.Equal("", locales[2].Value);

        string manifest = Path.Combine("/work", "shop", "translations", "runic.json");
        Check.Same(new[] { "overrides", "add", "acme.table", "--locale", "fr", "--key", "header_sort", "--project", Path.Combine("/work", "shop", "translations") },
            TranslationsTool.OverridesAddArguments(manifest, "acme.table", "fr", "header_sort"));
        Check.Equal(Path.Combine("/work", "shop", "translations", "overrides", "acme.table", "fr.rmf2"), LibraryCatalogs.OverrideSourcePath(manifest, "acme.table", "fr"));
        Check.Equal(5, LibraryCatalogs.MessageLine("# header {\n#   sort = x\n# }\nrows = Rows\nheader {\n  sort = Trier\n}\n", "header.sort"));
        Check.Equal(0, LibraryCatalogs.MessageLine("rows = Rows\n", "missing"));
        Check.Equal(10, LibraryCatalogs.MessageLine(File.ReadAllText(Path.Combine(Rmf2, "en.rmf2")), "plain"));
    }

    [Test]
    public void Typed_override_locales_are_canonicalized_and_compared_case_insensitively()
    {
        Check.Equal("de-CH", LibraryCatalogs.CanonicalLocale("de_ch"));
        Check.Equal("de-CH", LibraryCatalogs.CanonicalLocale(" DE-ch "));
        Check.Equal("zh-Hant-TW", LibraryCatalogs.CanonicalLocale("zh_hant_tw"));
        Check.Equal("fr", LibraryCatalogs.CanonicalLocale("fr"));
        Check.Equal("es-419", LibraryCatalogs.CanonicalLocale("ES-419"));
        Check.Equal("de-DE-u-co-phonebk", LibraryCatalogs.CanonicalLocale("de-de-U-CO-phonebk"));
        Check.Equal("sl-rozaj-biske", LibraryCatalogs.CanonicalLocale("sl-Rozaj-biske"));
        Check.Equal(null, LibraryCatalogs.CanonicalLocale("not a locale"));
        Check.Equal(null, LibraryCatalogs.CanonicalLocale("de--CH"));
        Check.Equal(null, LibraryCatalogs.CanonicalLocale(""));
        Check.Equal(null, LibraryCatalogs.CanonicalLocale("d"));
        Check.Equal(null, LibraryCatalogs.CanonicalLocale("de-u"));
        var message = LibraryCatalogs.Parse(Fixture("library-catalogs.json")).Catalogs.Single(catalog => catalog.Catalog == "acme.table").Messages![0];
        Check.True(LibraryCatalogs.IsOverriddenIn(message, "de-ch"), "de-ch");
        Check.True(LibraryCatalogs.IsOverriddenIn(message, "DE-CH"), "DE-CH");
        Check.True(!LibraryCatalogs.IsOverriddenIn(message, "de"), "de");
    }

    [Test]
    public void Overrides_add_stops_for_unsaved_edits_to_the_files_it_writes()
    {
        string source = Path.Combine("/work", "overrides", "acme.table", "de-CH.rmf2");
        string manifest = Path.Combine("/work", "runic.json");
        var documents = new[] { new OpenDocument(source, true), new OpenDocument(manifest, false), new OpenDocument(Path.Combine("/work", "other.rmf2"), true) };
        Check.Same(new[] { documents[0] }, LibraryCatalogs.Unsaved(documents, new[] { source, manifest }, ignoreCase: false));
        Check.Equal(0, LibraryCatalogs.Unsaved(documents, new[] { source.ToUpperInvariant(), manifest }, ignoreCase: false).Count);
        Check.Same(new[] { documents[0] }, LibraryCatalogs.Unsaved(documents, new[] { source.ToUpperInvariant(), manifest }, ignoreCase: true));
    }

    [Test]
    public void Library_documents_open_as_read_only_copies_named_after_their_source()
    {
        const string project = "file:///work/my%20shop/translations/runic.json";
        Check.Equal(project, LibraryCatalogs.DocumentProject("project=" + Uri.EscapeDataString(project)));
        Check.Equal(project, LibraryCatalogs.DocumentProject(Uri.EscapeDataString("project=" + project)));
        Check.Equal(null, LibraryCatalogs.DocumentProject("other=1"));

        string uri = "runic-library:/acme.table/translations/de%20CH.rmf2?project=" + Uri.EscapeDataString(project);
        string root = Path.Combine(Path.GetTempPath(), "RunicDevKit", "library");
        string path = LibraryCatalogs.DocumentPath(root, uri)!;
        Check.True(path.StartsWith(root, StringComparison.Ordinal), path);
        Check.True(path.EndsWith(Path.Combine("acme.table", "translations", "de CH.rmf2"), StringComparison.Ordinal), path);
        // Another application project gets another directory; the same project the same one.
        string other = LibraryCatalogs.DocumentPath(root, uri.Replace("my%2520shop", "admin"))!;
        Check.True(other != path && other.EndsWith("de CH.rmf2", StringComparison.Ordinal), other);
        Check.Equal(path, LibraryCatalogs.DocumentPath(root, uri));
        Check.True(LibraryCatalogs.IsLibraryUri(uri), "library URI");
        Check.True(!LibraryCatalogs.IsLibraryUri("file:///work/de.rmf2"), "file URI");
        Check.Equal(null, LibraryCatalogs.DocumentPath(root, "runic-library:/acme.table/../../escape.rmf2?project=x"));
        Check.Equal(null, LibraryCatalogs.DocumentPath(root, "runic-library:/acme.table/notes.txt?project=x"));
        Check.Equal(null, LibraryCatalogs.DocumentPath(root, "runic-library:/only.rmf2"));
        Check.Equal(null, LibraryCatalogs.DocumentPath(root, "file:///work/de.rmf2"));
    }

    private static string Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) { return error.Message; }
        throw new InvalidOperationException("Expected an InvalidOperationException.");
    }
}
