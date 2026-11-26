using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// Debug the Window with the active launch profile (design/ide-run-debug E4, B3, B4): the command-line rule (the
/// project's <c>RunArguments</c>, then the profile's <c>commandLineArgs</c>, as one unsplit string), reading the active
/// profile, the variables' order, and scenario M's <c>/mock</c> (<c>Fixtures/mock-app</c>).
/// </summary>
public class LaunchProfilesTests
{
    private const string Project = "/a/Acme.Mock.csproj";
    private const string AppHost = @"C:\a\bin\Debug\net10.0\Acme.Mock.exe";
    private static readonly string ProjectFolder = Path.GetDirectoryName(Project)!;

    private static LaunchProfile Profile(string? arguments, string? workingDirectory = null, params (string Key, string Value)[] variables)
        => new("Test", "Project", arguments, variables.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)).ToList(), workingDirectory);

    private static string FixtureFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "visualstudio", "Core.Tests", "Fixtures", "mock-app");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Fixtures/mock-app not found");
    }

    /// <summary>A copy of <c>Fixtures/mock-app</c> in a temporary folder; returns its project path.</summary>
    private static string CopyFixture(string directory)
    {
        string source = FixtureFolder();
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(directory, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return Path.Combine(directory, "Acme.Mock.csproj");
    }

    // ---- The command-line rule ----------------------------------------------------------------------------------

    [Test]
    public void Project_arguments_come_first_then_the_profiles_as_one_unsplit_string()
    {
        // (RunArguments, the profile's commandLineArgs) → the command line bstrArg gets, and the arguments the app sees.
        var cases = new (string? Run, string? Profile, string Line, string[] Argv)[]
        {
            ("/mock", null, "/mock", new[] { "/mock" }),                                   // project only (StartArguments)
            ("", "/mock", "/mock", new[] { "/mock" }),                                     // profile only
            (null, "/mock", "/mock", new[] { "/mock" }),
            ("/mock", "/verbose", "/mock /verbose", new[] { "/mock", "/verbose" }),        // both, the project's first (D084.2)
            ("  /mock  ", "  /verbose ", "/mock /verbose", new[] { "/mock", "/verbose" }),
            ("/mock", "   ", "/mock", new[] { "/mock" }),
            ("", "", "", Array.Empty<string>()),
            // Quoting (B3): the text is passed as written, so quotes and backslashes survive to the app.
            (@"/config:""C:\Program Files\Acme\dev.json""", null, @"/config:""C:\Program Files\Acme\dev.json""", new[] { @"/config:C:\Program Files\Acme\dev.json" }),
            (null, @"""C:\data\new"" /mock", @"""C:\data\new"" /mock", new[] { @"C:\data\new", "/mock" }),
            (@"--x ""a b""", @"/config:""C:\Program Files\x""", @"--x ""a b"" /config:""C:\Program Files\x""", new[] { "--x", "a b", @"/config:C:\Program Files\x" }),
        };
        foreach (var (run, profile, line, argv) in cases)
        {
            var program = WindowDebug.Program(Project, new RunProperties(AppHost, run, "", null), profile == null ? null : Profile(profile));
            string label = $"RunArguments [{run}], commandLineArgs [{profile}]";
            Check.Equal(AppHost, program.Program, label);
            Check.Equal(line, program.CommandLineArguments, label);
            Check.Same(argv, program.Arguments, label);
        }
    }

    [Test]
    public void Without_an_application_host_dotnet_exec_and_the_dll_are_left_out_and_the_rest_kept_as_written()
    {
        // M9: RunCommand dotnet, RunArguments exec "<dll>" $(StartArguments).
        var shared = WindowDebug.Program(Project, new RunProperties("dotnet", @"exec ""C:\Program Files\a\Acme.Mock.dll"" /mock /config:""C:\x y\z.json""", "", @"C:\Program Files\a\Acme.Mock.dll"), Profile("/verbose"));
        Check.Equal(@"C:\Program Files\a\Acme.Mock.dll", shared.Program);
        Check.Equal(@"/mock /config:""C:\x y\z.json"" /verbose", shared.CommandLineArguments);
        Check.Same(new[] { "/mock", @"/config:C:\x y\z.json", "/verbose" }, shared.Arguments);

        var bare = WindowDebug.Program(Project, new RunProperties(@"C:\Program Files\dotnet\dotnet.exe", "exec /a/bin/App.dll", "", "/a/bin/App.dll"));
        Check.Equal("", bare.CommandLineArguments);
        var profileOnly = WindowDebug.Program(Project, new RunProperties("dotnet", "exec /a/bin/App.dll", "", "/a/bin/App.dll"), Profile("/mock"));
        Check.Equal("/mock", profileOnly.CommandLineArguments);
    }

    [Test]
    public void The_profiles_working_directory_is_resolved_against_the_project_folder()
    {
        var properties = new RunProperties(AppHost, "", "/a/bin/run", null);
        Check.Equal("/a/bin/run", WindowDebug.Program(Project, properties).WorkingDirectory, "RunWorkingDirectory without a profile");
        Check.Equal("/a/bin/run", WindowDebug.Program(Project, properties, Profile(null)).WorkingDirectory, "a profile without workingDirectory");
        Check.Equal(Path.GetFullPath(Path.Combine(ProjectFolder, "data")), WindowDebug.Program(Project, properties, Profile(null, "data")).WorkingDirectory);
        Check.Equal(Path.GetFullPath(Path.Combine(ProjectFolder, "..", "shared")), WindowDebug.Program(Project, properties, Profile(null, "../shared")).WorkingDirectory);
        string absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "acme-data"));
        Check.Equal(absolute, WindowDebug.Program(Project, properties, Profile(null, absolute)).WorkingDirectory, "an absolute directory stays");
        Check.Equal(ProjectFolder, WindowDebug.Program(Project, new RunProperties(AppHost, "", "", null), Profile("/mock")).WorkingDirectory, "else the project folder, as before");
        Check.Equal("Test", WindowDebug.Program(Project, properties, Profile(null)).Profile);
        Check.Equal(null, WindowDebug.Program(Project, properties).Profile);
    }

    [Test]
    public void The_splitter_follows_dotnets_process_start_rules()
    {
        var cases = new (string Line, string[] Argv)[]
        {
            ("/mock", new[] { "/mock" }),
            (@"/config:""C:\Program Files\x""", new[] { @"/config:C:\Program Files\x" }),
            (@"""C:\data\new""", new[] { @"C:\data\new" }),
            (@"C:\data\new\", new[] { @"C:\data\new\" }),
            (@"""a\""b"" c", new[] { @"a""b", "c" }),
            (@"""a\\"" b", new[] { @"a\", "b" }),
            (@"""say """"hi"""""" x", new[] { @"say ""hi""", "x" }),
            ("\t a \t b  ", new[] { "a", "b" }),
            (@"""""", new[] { "" }),
            ("", Array.Empty<string>()),
        };
        foreach (var (line, argv) in cases) Check.Same(argv, CommandLine.Split(line), line);
        Check.Same(new[] { 0, 5 }, CommandLine.Tokens(@"exec ""C:\a b\x.dll""").Select(token => token.Start));

        // Against .NET itself (Unix: the same rules as Windows' CommandLineToArgvW): printf prints each argument it gets.
        if (OperatingSystem.IsWindows()) return;
        foreach (var (line, argv) in cases.Where(item => item.Argv.Length > 0 && item.Argv.All(arg => arg.Length > 0)))
        {
            var start = new ProcessStartInfo("printf", @"""%s\n"" " + line) { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Check.Same(argv, output.Split('\n', StringSplitOptions.None).SkipLast(1), "ProcessStartInfo.Arguments " + line);
        }
    }

    // ---- Reading the active profile -------------------------------------------------------------------------------

    [Test]
    public void Launch_settings_keep_the_files_order_and_accept_comments_trailing_commas_and_numbers()
    {
        var profiles = LaunchProfiles.Parse(File.ReadAllText(Path.Combine(FixtureFolder(), "Properties", "launchSettings.json")));
        Check.Same(new[] { "Acme.Mock", "Mock data", "Installed" }, profiles.Select(profile => profile.Name));
        var mock = profiles[1];
        Check.True(mock.IsProject, "Project");
        Check.Equal(@"/mock /config:""C:\Program Files\Acme\dev.json""", mock.CommandLineArgs);
        Check.Equal("data", mock.WorkingDirectory);
        Check.Same(new[] { "DOTNET_ENVIRONMENT=Staging", @"ACME_DATA=%ACME_ROOT%\mock", "ACME_PORT=5000" }, mock.EnvironmentVariables.Select(pair => pair.Key + "=" + pair.Value));
        Check.True(!profiles[2].IsProject, "Executable");

        Check.Equal(0, LaunchProfiles.Parse("{}").Count);
        Check.Equal(0, LaunchProfiles.Parse("""{ "profiles": { "x": 1 } }""").Count);
        Check.Equal("a//b", LaunchProfiles.Parse("""{ "profiles": { "p": { "commandName": "project", "commandLineArgs": "a//b" /* c */ } } }""")[0].CommandLineArgs);
        Check.True(LaunchProfiles.Parse("""{ "profiles": { "p": { "commandName": "project" } } }""")[0].IsProject, "commandName compares without case");
        Check.Equal("""{ "a": [1, 2  ] }""", LaunchProfiles.Relax("""{ "a": [1, 2, ] }"""));
        Check.Equal("""{ "a": "x,]" }""", LaunchProfiles.Relax("""{ "a": "x,]" }"""));
    }

    [Test]
    public void The_active_profile_is_the_named_one_else_the_first_and_only_project_profiles_apply()
    {
        var profiles = LaunchProfiles.Parse(File.ReadAllText(Path.Combine(FixtureFolder(), "Properties", "launchSettings.json")));
        Check.Equal("Mock data", LaunchProfiles.Choose(profiles, "Mock data").Profile?.Name);
        Check.Equal("Mock data", LaunchProfiles.Choose(profiles, " mock DATA ").Profile?.Name, "a name in other case");
        Check.Equal("Acme.Mock", LaunchProfiles.Choose(profiles, null).Profile?.Name, "no ActiveDebugProfile: the first, as Visual Studio");
        Check.Equal("Acme.Mock", LaunchProfiles.Choose(profiles, "Removed").Profile?.Name, "a profile that no longer exists: the first");
        var executable = LaunchProfiles.Choose(profiles, "Installed");
        Check.Equal(null, executable.Profile);
        Check.True(executable.Note!.Contains("Installed") && executable.Note.Contains("Executable"), executable.Note);
        var none = LaunchProfiles.Choose(Array.Empty<LaunchProfile>(), "x");
        Check.True(none.Profile == null && none.Note == null, "no profiles: as before");
    }

    [Test]
    public void The_profile_is_read_from_properties_and_the_user_file()
    {
        string directory = Directory.CreateTempSubdirectory("runic-launch-").FullName;
        try
        {
            string project = CopyFixture(directory);
            Check.Equal(Path.Combine(directory, "Properties", "launchSettings.json"), LaunchProfiles.SettingsPath(project));
            Check.Equal(null, LaunchProfiles.ActiveNameFromUserFile(project), "no user file");
            File.WriteAllText(project + ".user", """
                <?xml version="1.0" encoding="utf-8"?>
                <Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <ActiveDebugProfile>Mock data</ActiveDebugProfile>
                  </PropertyGroup>
                </Project>
                """);
            string? active = LaunchProfiles.ActiveNameFromUserFile(project);
            Check.Equal("Mock data", active);
            Check.Equal("Mock data", LaunchProfiles.Read(project, active).Profile?.Name);
            Check.Equal("Acme.Mock", LaunchProfiles.Read(project, null).Profile?.Name);

            File.WriteAllText(project + ".user", "<Project><PropertyGroup><ActiveDebugProfile>");
            Check.Equal(null, LaunchProfiles.ActiveNameFromUserFile(project), "a broken user file names no profile");

            File.WriteAllText(LaunchProfiles.SettingsPath(project), "{ \"profiles\": ");
            string error = Throws(() => LaunchProfiles.Read(project, null));
            Check.True(error.Contains("launchSettings.json") && error.Contains("not valid JSON"), error);

            File.Delete(LaunchProfiles.SettingsPath(project));
            var missing = LaunchProfiles.Read(project, "Mock data");
            Check.True(missing.Profile == null && missing.Note == null, "no launchSettings.json: as before");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void Profile_values_expand_environment_variables()
    {
        var environment = new Dictionary<string, string> { ["ACME_ROOT"] = @"C:\acme", ["Path"] = @"C:\bin" };
        var expanded = Profile("/root:%acme_root% %MISSING%", @"%ACME_ROOT%\data", ("ACME_DATA", @"%ACME_ROOT%\mock"), ("P", "%PATH%;x")).Expand(environment);
        Check.Equal(@"/root:C:\acme %MISSING%", expanded.CommandLineArgs);
        Check.Equal(@"C:\acme\data", expanded.WorkingDirectory);
        Check.Same(new[] { @"ACME_DATA=C:\acme\mock", @"P=C:\bin;x" }, expanded.EnvironmentVariables.Select(pair => pair.Key + "=" + pair.Value));
        Check.Equal("100%", LaunchProfiles.ExpandVariables("100%", environment));
    }

    // ---- Variables ------------------------------------------------------------------------------------------------

    [Test]
    public void Profile_variables_sit_between_the_development_default_and_the_sessions_runic_variables()
    {
        var visualStudio = new Dictionary<string, string> { ["PATH"] = @"C:\bin", ["DOTNET_ENVIRONMENT"] = "Production", ["ACME_KEEP"] = "vs" };
        var session = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["RUNIC_APPLICATION_DEVELOPMENT_ASSETS"] = "/a/assets.json",
            [WindowDebug.WebView2ArgumentsVariable] = "--session",
        };
        var profile = Profile(null, null,
            ("DOTNET_ENVIRONMENT", "Staging"), ("ACME_PORT", "5000"), ("ACME_KEEP", "profile"),
            ("RUNIC_APPLICATION_DEVELOPMENT_ASSETS", "/elsewhere"), (WindowDebug.WebView2ArgumentsVariable, "--profile"));
        var plan = WindowDebug.Plan(visualStudio, session, () => 41000, profile.EnvironmentVariables);
        Check.Equal("Staging", plan.Environment["DOTNET_ENVIRONMENT"], "the profile's over the session's default");
        Check.Equal("5000", plan.Environment["ACME_PORT"]);
        Check.Equal("profile", plan.Environment["ACME_KEEP"], "the profile's over Visual Studio's");
        Check.Equal("/a/assets.json", plan.Environment["RUNIC_APPLICATION_DEVELOPMENT_ASSETS"], "the session's Runic variables last");
        Check.Equal("--session --remote-debugging-port=41000", plan.Environment[WindowDebug.WebView2ArgumentsVariable]);
        Check.Equal(@"C:\bin", plan.Environment["PATH"]);
        Check.Same(new[] { "ACME_KEEP", "ACME_PORT", "DOTNET_ENVIRONMENT", "RUNIC_APPLICATION_DEVELOPMENT_ASSETS", WindowDebug.WebView2ArgumentsVariable }, plan.Added.Keys);

        // A profile's WebView2 arguments get the port when the session has none.
        var own = WindowDebug.Plan(new Dictionary<string, string>(), null, () => 41001, Profile(null, null, (WindowDebug.WebView2ArgumentsVariable, "--profile")).EnvironmentVariables);
        Check.Equal("--profile --remote-debugging-port=41001", own.Environment[WindowDebug.WebView2ArgumentsVariable]);
        Check.Equal("Development", own.Environment["DOTNET_ENVIRONMENT"]);

        // The template's CS-WebUI profile sets the CS-WebUI port; without one in the session it decides the page engine.
        var webUi = WindowDebug.Plan(new Dictionary<string, string>(), null, () => throw new InvalidOperationException("no free port needed"),
            Profile(null, null, (WindowDebug.WebUiPortVariable, "9225")).EnvironmentVariables);
        Check.Equal(PageHost.CsWebUi, webUi.Host);
        Check.Equal(9225, webUi.Port);
    }

    // ---- Scenario M: /mock ------------------------------------------------------------------------------------------

    /// <summary>The stand-in app of REPRODUCTION.md scenario M: a Debug build exits 2 without <c>/mock</c>.</summary>
    private static int MockApp(IReadOnlyList<string> args, string configuration)
        => configuration == "Debug" && !args.Contains("/mock") ? 2 : 0;

    [Test]
    public void Mock_reaches_the_window_through_start_arguments_and_through_a_profile()
    {
        string directory = Directory.CreateTempSubdirectory("runic-mock-").FullName;
        try
        {
            string project = CopyFixture(directory);
            // dotnet msbuild -getProperty for the fixture in Debug (M8a/M9a): StartArguments became RunArguments.
            var debug = WindowDebug.ParseRunProperties("""{ "Properties": { "RunCommand": "C:\\a\\bin\\Debug\\net10.0\\Acme.Mock.exe", "RunArguments": "/mock", "RunWorkingDirectory": "", "TargetPath": "C:\\a\\bin\\Debug\\net10.0\\Acme.Mock.dll" } }""");
            var noStart = new RunProperties(debug.RunCommand, "", "", debug.TargetPath);

            // StartArguments with the template's first profile (no commandLineArgs).
            var template = WindowDebug.Program(project, debug, LaunchProfiles.Read(project, null).Profile);
            Check.Equal("/mock", template.CommandLineArguments);
            Check.Equal(0, MockApp(template.Arguments, "Debug"), "StartArguments: mock mode");

            // The profile, without StartArguments: before E4 Debug the Window ignored it and the app exited 2.
            Check.Equal(2, MockApp(WindowDebug.Program(project, noStart).Arguments, "Debug"), "without the profile, as before E4");
            var viaProfile = WindowDebug.Program(project, noStart, LaunchProfiles.Read(project, "Mock data").Profile);
            Check.Equal(@"/mock /config:""C:\Program Files\Acme\dev.json""", viaProfile.CommandLineArguments, "passed on unsplit");
            Check.Same(new[] { "/mock", @"/config:C:\Program Files\Acme\dev.json" }, viaProfile.Arguments);
            Check.Equal(0, MockApp(viaProfile.Arguments, "Debug"), "the profile: mock mode");
            Check.Equal(Path.GetFullPath(Path.Combine(directory, "data")), viaProfile.WorkingDirectory);

            // Both: the project's /mock first, then the profile's.
            var both = WindowDebug.Program(project, debug, LaunchProfiles.Read(project, "Mock data").Profile);
            Check.Same(new[] { "/mock", "/mock", @"/config:C:\Program Files\Acme\dev.json" }, both.Arguments);
            Check.Equal(0, MockApp(both.Arguments, "Debug"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) { return error.Message; }
        throw new InvalidOperationException("expected an exception");
    }
}
