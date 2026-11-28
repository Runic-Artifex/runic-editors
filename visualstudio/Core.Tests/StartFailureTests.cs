using System;
using System.IO;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>The start-failure InfoBar's reason and help, as VS Code's notification.</summary>
public class StartFailureTests
{
    private static ServerStartAttempt Stderr(params string[] lines) => new() { Stderr = lines, Directory = "C:\\src\\app" };

    [Test]
    public void The_reason_comes_from_the_dotnet_tool_output()
    {
        Check.Equal(ServerStartProblem.RestoreNeeded, ServerStartFailure.Classify(Stderr("Run \"dotnet tool restore\" to make the \"runic-translations\" command available.")));
        Check.Equal(ServerStartProblem.RestoreNeeded, ServerStartFailure.Classify(Stderr("Tool 'dotnet-runic-translations' (version '0.13.0') is not installed.")));
        Check.Equal(ServerStartProblem.RuntimeMissing, ServerStartFailure.Classify(Stderr("You must install or update .NET to run this application.", "The framework 'Microsoft.NETCore.App', version '10.0.0' (x64) was not found.")));
        Check.Equal(ServerStartProblem.WrongToolVersion, ServerStartFailure.Classify(Stderr("error RCLI1002: unknown command 'lsp'.")));
        Check.Equal(ServerStartProblem.Other, ServerStartFailure.Classify(Stderr("Unhandled exception. System.IO.IOException: disk full")));
        Check.Equal(ServerStartProblem.DotnetMissing, ServerStartFailure.Classify(new ServerStartAttempt { ExecutableMissing = true, Error = "The system cannot find the file specified" }));
        Check.Equal(ServerStartProblem.AssemblyMissing, ServerStartFailure.Classify(new ServerStartAttempt { Known = ServerStartProblem.AssemblyMissing, Stderr = new[] { "dotnet tool restore" } }), "a known problem wins");
        Check.Equal(ServerStartProblem.ToolMissing, ServerStartFailure.Classify(new ServerStartAttempt { Error = "No project-local runic-translations tool manifest found. Restore the local tool before opening MF2 resources." }));
    }

    [Test]
    public void The_message_says_what_to_do()
    {
        const string prefix = "Runic Translations could not start its language server. ";
        string restore = ServerStartFailure.Message(Stderr("Run \"dotnet tool restore\" to make the \"runic-translations\" command available."));
        Check.Equal(prefix + "The local runic-translations tool is not restored. Run dotnet tool restore in C:\\src\\app, then restart the language server.", restore);
        Check.True(ServerStartFailure.Message(new ServerStartAttempt { Known = ServerStartProblem.ToolMissing }).Contains("Install dotnet-runic-translations as a local tool"), "tool missing");
        Check.True(ServerStartFailure.Message(new ServerStartAttempt { ExecutableMissing = true, Dotnet = "/opt/none/dotnet" })
            .Contains("The dotnet executable '/opt/none/dotnet' was not found. Install .NET, or set .NET executable in Tools > Options > Runic DevKit."), "dotnet missing");
        Check.True(ServerStartFailure.Message(Stderr("unknown command 'lsp'.")).Contains("has no language server (lsp)"), "wrong version");
        Check.Equal(prefix + "RUNIC_TRANSLATIONS_SERVER must identify an existing absolute tool DLL; x.dll is not one. Fix the setting, then restart the language server.",
            ServerStartFailure.Message(new ServerStartAttempt { Known = ServerStartProblem.AssemblyMissing, Error = "RUNIC_TRANSLATIONS_SERVER must identify an existing absolute tool DLL; x.dll is not one." }));
        Check.Equal(prefix + "Unhandled exception.", ServerStartFailure.Message(new ServerStartAttempt { Error = "\r\nUnhandled exception.\r\n   at Main()" }));
        Check.Equal(prefix + "See the Runic Translations Output pane.", ServerStartFailure.Message(new ServerStartAttempt()));
    }

    [Test]
    public void The_actions_match_VS_Code_and_the_notice_shows_once_per_solution()
    {
        Check.Equal("Show output", ServerStartFailure.ShowOutput);
        Check.Equal("How to install the tool", ServerStartFailure.HowToInstall);
        // The section VS Code's "How to install the tool" opens (its README); it has the same tool steps.
        string readme = File.ReadAllText(Path.Combine(Paths.Extension, "..", "vscode", "README.md"));
        Check.True(ServerStartFailure.InstallHelpUrl.EndsWith("/vscode/README.md#install-the-tool", StringComparison.Ordinal) && readme.Replace("\r\n", "\n").Contains("\n## Install the tool\n"), "the install section exists");

        var notice = new OneTimeNotice();
        Check.True(notice.Claim("C:\\src\\App.sln"), "first");
        Check.True(!notice.Claim("c:\\SRC\\app.sln"), "same solution, other case");
        Check.True(notice.Claim("C:\\src\\Other.sln"), "another solution");
    }

    [Test]
    public void The_two_output_panes_have_the_VS_Code_channel_roles()
    {
        Check.Equal("Runic SDK", RunicOutputPanes.SdkName);
        Check.Equal("Runic Translations", RunicOutputPanes.TranslationsName);
        string client = File.ReadAllText(Path.Combine(Paths.Extension, "LanguageClient.cs"));
        Check.True(!client.Contains("Trace.WriteLine"), "stderr no longer goes only to the diagnostic trace");
        Check.True(client.Contains("pane.OutputStringThreadSafe(args.Data"), "stderr goes to the Translations pane");
        Check.True(client.Contains("ShowNotificationOnInitializeFailed => false"), "one failure notice, the extension's");
    }

    [Test]
    public void The_Runic_SDK_pane_shows_dev_output_without_colour_codes()
    {
        // Vite's colour codes showed raw in the pane (0.24.0 checklist P4).
        Check.Equal("[vite] 01:23:14 [vite] page reload src/App.tsx",
            DevOutput.Plain("[vite] \x1B[2m01:23:14\x1B[22m \x1B[36m\x1B[1m[vite]\x1B[22m\x1B[39m \x1B[32mpage reload \x1B[39m\x1B[2msrc/App.tsx\x1B[22m"));
        Check.Equal("see docs", DevOutput.Plain("see \x1B]8;;https://example.invalid/\u0007docs\x1B]8;;\x1B\\"), "OSC 8 hyperlinks");
        string pane = File.ReadAllText(Path.Combine(Paths.Extension, "Output", "SdkPaneOutput.cs"));
        Check.True(pane.Contains("line = DevOutput.Plain(line);"), "the pane writes plain lines");
    }
}
