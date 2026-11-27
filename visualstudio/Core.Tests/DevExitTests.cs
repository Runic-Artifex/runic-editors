using System;
using System.Collections.Generic;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// F5's early <c>dev</c> exit and the restore it offers (the 0.24 VM run, part 2, finding 1), and the Development rule
/// that decides whether Debug the Window can wait for the page (finding 3).
/// </summary>
public class DevExitTests
{
    /// <summary>What <c>dotnet runic dev</c> printed on the VM's fresh clone (German dotnet), then the service's own line.</summary>
    private static readonly string[] FreshClone =
    {
        "Der Befehl \"dotnet runic\" konnte nicht ausgeführt werden.",
        "Führen Sie \"dotnet tool restore\" aus, um den Befehl \"dotnet-runic\" verfügbar zu machen.",
        "dotnet runic dev exited with code 1 (Mock.csproj).",
    };

    [Test]
    public void An_early_exit_names_its_code_and_last_lines_and_offers_the_restore_only_when_the_tools_are_missing()
    {
        var exit = new DevExit(1, FreshClone, problems: 0);
        Check.True(exit.ToolsMissing, "the German message names dotnet tool restore");
        string offer = DevExits.Message(exit, offerRestore: true);
        Check.True(offer.StartsWith("dotnet runic dev exited with code 1 before its session was ready: Der Befehl"), offer);
        Check.True(offer.Contains("| Führen Sie \"dotnet tool restore\" aus"), offer);
        Check.True(!offer.Contains("(Mock.csproj)"), "the service's own exit line is not repeated: " + offer);
        Check.True(offer.Contains("Restore them now (dotnet tool restore)?"), offer);
        // The VM's message pointed at an Error List that stayed empty.
        Check.True(!offer.Contains("Error List") && offer.Contains("Runic SDK Output pane"), offer);
        Check.True(DevExits.Message(exit, offerRestore: false).Contains("run dotnet tool restore in the project's folder"), "without the offer it says what to run");

        var build = new DevExit(1, new[] { "Program.cs(3,1): error CS1002: ; expected", "Build FAILED." }, problems: 1);
        string failed = DevExits.Message(build, offerRestore: true);
        Check.True(!build.ToolsMissing && !failed.Contains("Restore"), failed);
        Check.True(failed.Contains("See the Error List"), "rows from dev's output: the Error List has them: " + failed);
        Check.Equal("dotnet runic dev exited with code 3 before its session was ready. Its full output is in the Runic SDK Output pane.", DevExits.Message(new DevExit(3, Array.Empty<string>(), 0), true));
        Check.Same(new[] { "tool", "restore" }, DevExits.ToolRestoreArguments);
    }

    [Test]
    public void The_last_lines_keep_twenty_non_empty_lines()
    {
        var lines = new DevExits.LastLines();
        for (int i = 0; i < 30; i++) { lines.Add($"line {i}  "); lines.Add("  "); }
        var kept = lines.Snapshot();
        Check.Equal(DevExits.KeptLines, kept.Count);
        Check.Equal("line 10", kept[0]);
        Check.Equal("line 29", kept[^1]);
        Check.True(DevExits.Message(new DevExit(1, kept, 0), false).Contains(": line 27 | line 28 | line 29."), "the last three");
    }

    [Test]
    public void A_Window_is_in_Development_by_the_SDKs_rule()
    {
        HostEnvironment Of(params (string Key, string Value)[] pairs) => HostEnvironment.Of(pairs.ToDictionary(pair => pair.Key, pair => pair.Value));
        Check.True(Of(("DOTNET_ENVIRONMENT", "development")).IsDevelopment, "without case");
        Check.True(!Of(("DOTNET_ENVIRONMENT", "Staging"), ("ASPNETCORE_ENVIRONMENT", "Development")).IsDevelopment, "DOTNET_ENVIRONMENT first");
        var aspNet = Of(("DOTNET_ENVIRONMENT", ""), ("ASPNETCORE_ENVIRONMENT", "Development"));
        Check.True(aspNet.IsDevelopment && aspNet.Variable == "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT when the first is empty");
        var none = Of();
        Check.True(!none.IsDevelopment && none.Value == null, "neither: Production");

        // The fixture's "Mock data" profile with Staging (the 0.24 VM run): the plan's environment says so.
        var plan = WindowDebug.Plan(new Dictionary<string, string>(), null, () => 63777, new[] { new KeyValuePair<string, string>("DOTNET_ENVIRONMENT", "Staging") });
        Check.True(!plan.HostEnvironment.IsDevelopment, "Staging");
        string message = plan.HostEnvironment.NotDevelopmentMessage("Mock.csproj", plan.EndpointUrl, "Mock data");
        Check.True(message.StartsWith("Mock.csproj runs with DOTNET_ENVIRONMENT=Staging from the launch profile Mock data, not in Development, so its page's debugging endpoint http://127.0.0.1:63777/ does not open"), message);
        Check.True(message.Contains("specs/desktop/development-host-profile.md") && message.Contains("Set DOTNET_ENVIRONMENT to Development in the launch profile Mock data"), message);
        Check.True(none.NotDevelopmentMessage("Mock.csproj", "http://127.0.0.1:1/", null).Contains("DOTNET_ENVIRONMENT unset (Production), not in Development"), "unset");
        Check.True(WindowDebug.Plan(new Dictionary<string, string>(), null, () => 1).HostEnvironment.IsDevelopment, "dev's default");
    }
}
