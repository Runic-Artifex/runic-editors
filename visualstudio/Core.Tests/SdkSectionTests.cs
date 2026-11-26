using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// The ViewModels and Doctor sections against the fixtures VS Code's <c>vscode/test/sdk.test.ts</c> uses (copied to
/// <c>Core.Tests/Fixtures</c>) and the <c>fixtures/sdk-app</c> project of the VS Code host test.
/// </summary>
public class SdkSectionTests
{
    private static readonly string Repository = FindRepository();
    private static readonly string Fixtures = Path.Combine(Repository, "visualstudio", "Core.Tests", "Fixtures");

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

    // ---- Development source map -------------------------------------------------------------------------------------

    [Test]
    public void A_source_map_with_the_current_or_the_0_20_format_ID_is_accepted_and_others_are_rejected()
    {
        string text = Fixture("source-map.json");
        Check.Equal("runic.views.bridge-sources/1", SourceMap.Parse(text).Format);
        Check.Equal("runic-bridge-sources/1", SourceMap.Parse(text.Replace("runic.views.bridge-sources/1", "runic-bridge-sources/1")).Format);
        foreach (string format in new[] { "runic.views.bridge-sources/2", "runic-bridge-sources/2" })
        {
            var error = Throws<FormatException>(() => SourceMap.Parse(text.Replace("runic.views.bridge-sources/1", format)));
            Check.True(error.Message.StartsWith("Unsupported development source map format"), error.Message);
        }
        Check.True(Throws<FormatException>(() => SourceMap.Parse("{\"format\":\"runic.views.bridge-sources/1\"}")).Message.Contains("no viewModels or members"), "missing arrays");
        Throws<FormatException>(() => SourceMap.Parse("{\"format\":"));
    }

    [Test]
    public void The_source_map_tree_lists_ViewModels_with_their_members_in_client_order_and_their_CSharp_locations()
    {
        var tree = SourceMapTree.Build(SourceMap.Parse(Fixture("source-map.json")));
        Check.Same(new[] { "About|", "Counter|route counter", "Demo.Orphan|" }, tree.Select(node => node.Label + "|" + node.Description));
        var counter = tree[1];
        Check.Equal("/src/Demo/CounterViewModel.cs:6:29", counter.Location?.ToString());
        Check.Same(new[] { "count|state", "canIncrement|command state", "increment|command" }, counter.Children.Select(child => child.Label + "|" + child.Description));
        var increment = counter.Children[2];
        Check.Equal("/src/Demo/CounterViewModel.cs:18:18", increment.Location?.ToString());
        Check.True(increment.Tooltip.Contains("via Demo.CounterViewModel.IncrementCommand"), increment.Tooltip);
        Check.True(counter.Children[1].Tooltip.Contains("no source location"), counter.Children[1].Tooltip);
    }

    [Test]
    public void The_newest_source_map_under_obj_configuration_framework_is_used()
    {
        string root = Temporary("sources");
        try
        {
            string project = Path.Combine(root, "App.csproj");
            Check.Equal(null, SourceMap.Newest(project));
            string debug = Path.Combine(root, "obj", "Debug", "net10.0", "runic-bridge");
            string release = Path.Combine(root, "obj", "Release", "net10.0", "runic-bridge");
            Directory.CreateDirectory(debug); Directory.CreateDirectory(release);
            File.WriteAllText(Path.Combine(debug, "sources.json"), Fixture("source-map.json"));
            File.WriteAllText(Path.Combine(release, "sources.json"), Fixture("source-map.json"));
            File.SetLastWriteTimeUtc(Path.Combine(release, "sources.json"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check.Equal(Path.Combine(debug, "sources.json"), SourceMap.Newest(project));
            File.SetLastWriteTimeUtc(Path.Combine(release, "sources.json"), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check.Equal(Path.Combine(release, "sources.json"), SourceMap.Newest(project));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void The_ViewModels_section_shows_the_tree_opens_members_and_reports_an_unreadable_map()
    {
        string root = Temporary("viewmodels");
        try
        {
            var state = new SdkProjectState(Path.Combine(root, "App.csproj"));
            state.LoadSourceMap();
            Check.Equal(0, SdkSections.ViewModels(new[] { state }).Count, "no map yet: the section shows its welcome text");

            string directory = Path.Combine(root, "obj", "Debug", "net10.0", "runic-bridge");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "sources.json"), Fixture("source-map.json"));
            state.LoadSourceMap();
            var rows = SdkSections.ViewModels(new[] { state });
            Check.Same(new[] { "About", "Counter", "Demo.Orphan" }, rows.Select(row => row.Label));
            var counter = rows[1];
            Check.Equal(RowIcon.ViewModel, counter.Icon);
            Check.Equal(RowAction.OpenLocation, counter.Action);
            Check.Same(new[] { RowIcon.Property, RowIcon.Boolean, RowIcon.Method }, counter.Children.Select(row => row.Icon));
            Check.Same(new[] { RowAction.OpenLocation, RowAction.None, RowAction.OpenLocation }, counter.Children.Select(row => row.Action));
            Check.Equal(RowAction.None, rows[0].Action, "About has no location");

            var second = new SdkProjectState(Path.Combine(root, "Other", "Other.csproj"));
            second.LoadSourceMap();
            var grouped = SdkSections.ViewModels(new[] { state, second });
            Check.Same(new[] { "App.csproj — 3 ViewModels", "Other.csproj — not built" }, grouped.Select(row => row.ToString()));
            Check.Equal(3, grouped[0].Children.Count);

            File.WriteAllText(Path.Combine(directory, "sources.json"), "{\"format\":\"runic.views.bridge-sources/9\",\"viewModels\":[],\"members\":[]}");
            state.LoadSourceMap();
            var error = SdkSections.ViewModels(new[] { state }).Single();
            Check.True(error.Label.StartsWith("The development source map could not be read: Unsupported development source map format"), error.Label);
            Check.Equal(RowIcon.Warning, error.Icon);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- Doctor -----------------------------------------------------------------------------------------------------

    [Test]
    public void Doctor_reads_a_successful_envelope_a_failed_envelopes_fault_data_and_reports_a_fault_without_a_report()
    {
        Check.Same(new[] { "runic", "doctor", "--project", "/app/App.csproj", "--format", "json", "--fail-on", "never" }, Doctor.Arguments("/app/App.csproj"));
        var success = Doctor.Parse(Fixture("doctor-success.json"));
        Check.Same(new[] { "views-window", "lock-file", "dotnet-sdk" }, Doctor.OrderedChecks(success).Select(check => check.Id));
        Check.Equal("1 failed, 1 warning, 1 passed", Doctor.Summary(success));
        Check.Equal(false, success.Healthy);
        var failed = Doctor.Parse(Fixture("doctor-fault-with-report.json"));
        Check.Equal(false, failed.Healthy);
        Check.Equal("views-window", failed.Checks.Single().Id);
        Check.Equal("RAPPCLI1003: No project file was found.", Throws<InvalidOperationException>(() => Doctor.Parse(Fixture("doctor-fault.json"))).Message);
        Check.True(Throws<InvalidOperationException>(() => Doctor.Parse("Could not execute because the specified command or file was not found.")).Message
            .StartsWith("dotnet runic doctor did not write a JSON envelope"), "plain text output");
    }

    [Test]
    public void Failing_and_warning_checks_become_Error_List_entries_on_the_project_with_the_doctor_help_link()
    {
        var entries = Doctor.ErrorListEntries("/app/App.csproj", Doctor.Parse(Fixture("doctor-success.json")), "0.21.0");
        Check.Same(new[] { "lock-file", "views-window" }, entries.Select(entry => entry.Code));
        Check.Same(new[] { RunicErrorSeverity.Warning, RunicErrorSeverity.Error }, entries.Select(entry => entry.Severity));
        Check.Equal("views-window: Not a Window project. Fix: Set RunicApplicationFrontendWindowProject.", entries[1].Text);
        Check.True(entries.All(entry => entry.File == "/app/App.csproj" && entry.Line == 0 && entry.Column == 0), "on the project file");
        Check.Equal("https://github.com/Runic-Artifex/runic-sdk/blob/v0.21.0/tools/dotnet-runic/README.md#doctor-json-output", entries[0].HelpUri);
        Check.True(Doctor.ErrorListEntries("/app/App.csproj", Doctor.Parse(Fixture("doctor-success.json")), null)[0].HelpUri.Contains("/blob/main/"), "unknown version");
    }

    [Test]
    public void The_Doctor_section_shows_progress_errors_and_checks_failures_first()
    {
        var state = new SdkProjectState("/app/App.csproj");
        Check.Equal(0, SdkSections.Doctor(new[] { state }).Count, "never run: the section shows its welcome text");
        state.DoctorRunning = true;
        Check.Equal(RowIcon.Running, SdkSections.Doctor(new[] { state }).Single().Icon);
        state.DoctorRunning = false; state.DoctorError = "RAPPCLI1003: No project file was found.";
        Check.Equal("doctor failed: RAPPCLI1003: No project file was found.", SdkSections.Doctor(new[] { state }).Single().Label);
        state.DoctorError = null; state.Doctor = Doctor.Parse(Fixture("doctor-success.json"));
        var rows = SdkSections.Doctor(new[] { state });
        Check.Same(new[] { "Not healthy — 1 failed, 1 warning, 1 passed", "views-window — Not a Window project.", "lock-file — No lock file.", "dotnet-sdk — SDK 10.0.403." }, rows.Select(row => row.ToString()));
        Check.Same(new[] { RowIcon.NotHealthy, RowIcon.Error, RowIcon.Warning, RowIcon.Pass }, rows.Select(row => row.Icon));
        Check.Equal("Set RunicApplicationFrontendWindowProject.", rows[1].Children.Single().Label);
        Check.True(rows[1].Expanded && !rows[3].Expanded, "failing checks open, passing ones closed");

        var other = new SdkProjectState("/other/Other.csproj");
        var grouped = SdkSections.Doctor(new[] { state, other });
        Check.Same(new[] { "App.csproj — 1 failed, 1 warning, 1 passed", "Other.csproj" }, grouped.Select(row => row.ToString()));
        Check.Equal(RowAction.RunDoctor, grouped[1].Children.Single().Action);
    }

    [Test]
    public void Run_Doctor_checks_the_project_of_the_active_document_or_every_project()
    {
        var projects = new[] { new SdkProjectState(Path.Combine(Path.GetTempPath(), "a", "A.csproj")), new SdkProjectState(Path.Combine(Path.GetTempPath(), "b", "B.csproj")) };
        Check.Equal(1, SdkSections.DoctorTargets(projects.Take(1).ToList(), null).Count);
        Check.Same(new[] { projects[1] }, SdkSections.DoctorTargets(projects, Path.Combine(Path.GetTempPath(), "b", "Views", "Counter.cs")));
        Check.Equal(2, SdkSections.DoctorTargets(projects, Path.Combine(Path.GetTempPath(), "c.txt")).Count);
        Check.Equal(2, SdkSections.DoctorTargets(projects, null).Count);
    }

    [Test]
    public async Task Doctor_runs_the_project_tool_through_dotnet_and_keeps_the_envelope_of_a_failing_exit()
    {
        string root = Temporary("doctor");
        try
        {
            string project = Path.Combine(root, "My App", "App.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(Path.Combine(root, "doctor.json"), Fixture("doctor-fault-with-report.json"));
            string arguments = Path.Combine(root, "arguments.txt");
            string dotnet = FakeDotnet(root, Path.Combine(root, "doctor.json"), arguments, exitCode: 1);
            var report = Doctor.Parse(await Doctor.RunAsync(dotnet, project, CancellationToken.None));
            Check.Equal("views-window", report.Checks.Single().Id);
            Check.Same(Doctor.Arguments(project), File.ReadAllLines(arguments).Select(line => line.Trim()).Where(line => line.Length > 0));

            string silent = FakeDotnet(root, null, arguments, exitCode: 3);
            var error = await ThrowsAsync<InvalidOperationException>(() => Doctor.RunAsync(silent, project, CancellationToken.None));
            Check.True(error.Message.Contains("runic doctor failed: tool not restored"), error.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A stand-in <c>dotnet</c> like the VS Code host test's: prints a file, records its arguments one per line.</summary>
    private static string FakeDotnet(string root, string? output, string arguments, int exitCode)
    {
        string name = output == null ? "silent-dotnet" : "fake-dotnet";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string script = Path.Combine(root, name + ".cmd");
            File.WriteAllText(script, "@echo off\r\n(for %%a in (%*) do @echo %%~a) > \"" + arguments + "\"\r\n"
                + (output != null ? "type \"" + output + "\"\r\n" : "echo tool not restored 1>&2\r\n") + $"exit /b {exitCode}\r\n");
            return script;
        }
        string shell = Path.Combine(root, name + ".sh");
        File.WriteAllText(shell, "#!/bin/sh\nprintf '%s\\n' \"$@\" > '" + arguments + "'\n"
            + (output != null ? "cat '" + output + "'\n" : "echo 'tool not restored' >&2\n") + $"exit {exitCode}\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shell;
    }

    // ---- Projects and help links ------------------------------------------------------------------------------------

    [Test]
    public void The_sdk_app_fixture_is_a_Runic_SDK_project_and_a_Translations_only_project_is_not()
    {
        string app = Path.Combine(Repository, "fixtures", "sdk-app", "App.csproj");
        Check.Same(new[] { app }, RunicSdkProjects.Find(new[] { app, app, Path.Combine(Repository, "fixtures", "rmf2", "runic.json"), "Miscellaneous Files" }));
        // fixtures/sdk-library's client library (the VS Code host test's dependency) is a Runic SDK project too.
        string library = Path.Combine(Repository, "fixtures", "sdk-library", "SharedUi", "SharedUi.csproj");
        Check.Same(new[] { app, library }, RunicSdkProjects.Scan(Path.Combine(Repository, "fixtures")));
        Check.True(RunicSdkProjects.IsSdkProject("<Project><ItemGroup><ProjectReference Include=\"../Runic.Desktop.Host/Runic.Desktop.Host.csproj\" /></ItemGroup></Project>"), "Runic.Desktop reference");
        Check.True(!RunicSdkProjects.IsSdkProject("<Project><ItemGroup><PackageReference Include=\"Runic.Translations.Runtime\" Version=\"0.13.0\" /></ItemGroup></Project>"), "Translations only");
    }

    [Test]
    public void The_tool_version_comes_from_the_nearest_manifest_and_help_links_use_its_tag()
    {
        string root = Temporary("manifest");
        try
        {
            string project = Path.Combine(root, "src", "App", "App.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            Directory.CreateDirectory(Path.Combine(root, ".config"));
            File.WriteAllText(Path.Combine(root, ".config", "dotnet-tools.json"), "{\"version\":1,\"isRoot\":true,\"tools\":{\"dotnet-runic\":{\"version\":\"0.21.0\",\"commands\":[\"runic\"]}}}");
            Check.Equal("0.21.0", RunicSdkProjects.ProjectToolVersion(project));
            Directory.CreateDirectory(Path.Combine(root, "src", ".config"));
            File.WriteAllText(Path.Combine(root, "src", ".config", "dotnet-tools.json"), "{\"isRoot\":true,\"tools\":{}}");
            Check.Equal(null, RunicSdkProjects.ProjectToolVersion(project), "a root manifest without the tool stops the search");
        }
        finally { Directory.Delete(root, recursive: true); }
        foreach (string code in new[] { "RUNICBRIDGE013", "RAPPDEV1007", "RAPPCLI1009", "RAPPPKG1001", "RCLI8101", "RUNICP008" }) Check.True(RunicHelpLinks.IsRunicCode(code), code);
        foreach (string code in new[] { "CS1002", "RTR0090", "RUNICBRIDGE", "MSB3073", "RUNICPA001" }) Check.True(!RunicHelpLinks.IsRunicCode(code), code);
        Check.Equal("https://github.com/Runic-Artifex/runic-sdk/blob/v0.20.0/docs/diagnostics.md#runicbridge013", RunicHelpLinks.Diagnostic("RUNICBRIDGE013", "0.20.0"));
        Check.Equal("https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#rappdev1007", RunicHelpLinks.Diagnostic("RAPPDEV1007"));
        Check.True(RunicHelpLinks.Diagnostic("RAPPDEV1007", "../../evil").Contains("/blob/main/"), "an unsafe version is not a tag");
    }

    [Test]
    public void The_JSON_reader_handles_escapes_numbers_and_rejects_trailing_text()
    {
        var value = Json.Object(Json.Parse("{\"a\":\"x\\u00e9\\n\\\"\",\"b\":[1,-2.5e1,true,null],\"c\":{}}"))!;
        Check.Equal("x\u00e9\n\"", Json.String(value, "a"));
        Check.Same(new object?[] { 1d, -25d, true, null }, Json.Array(value["b"])!);
        Throws<FormatException>(() => Json.Parse("{} x"));
        Throws<FormatException>(() => Json.Parse("[1,]"));
        Check.Equal("\"a b\"", CommandLine.Quote("a b"));
        Check.Equal("\"C:\\My App\\\\\"", CommandLine.Quote("C:\\My App\\"));
        Check.Equal("plain", CommandLine.Quote("plain"));
        Check.Equal("\"\"", CommandLine.Quote(""));
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
