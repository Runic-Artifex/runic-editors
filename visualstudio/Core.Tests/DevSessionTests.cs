using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// The dev session file, its liveness rule, <c>dotnet runic dev</c>'s output and the pre-F5 guard, against the session
/// VS Code's <c>vscode/test/sdk.test.ts</c> uses (<c>Fixtures/dev-session-*.json</c>) and the output forms of runic-sdk's
/// IDE contract (<c>Fixtures/dev-output.txt</c>).
/// </summary>
public class DevSessionTests
{
    private static readonly string Fixtures = FindFixtures();

    private static string FindFixtures()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "visualstudio", "Core.Tests", "Fixtures");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("visualstudio/Core.Tests/Fixtures not found above " + AppContext.BaseDirectory);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(Fixtures, name));

    private static readonly DateTimeOffset DevStarted = DateTimeOffset.Parse("2026-11-14T18:53:15.9010000+00:00");

    /// <summary>An in-memory session file whose processes the test controls.</summary>
    private sealed class MemorySource : IDevSessionSource
    {
        public Dictionary<string, string> Files { get; } = new();
        public Dictionary<int, DevProcessInfo> Processes { get; } = new();
        public string? Read(string path) => Files.TryGetValue(path, out var text) ? text : null;
        public DevProcessInfo? FindProcess(int processId) => Processes.TryGetValue(processId, out var info) ? info : null;
    }

    /// <summary>An in-memory dev-stopped stamp: file times the test sets, touches it counts, and a read-only directory.</summary>
    private sealed class MemoryStamp : IDevStoppedStamp
    {
        public Dictionary<string, DateTime> Times { get; } = new();
        public List<string> Touches { get; } = new();
        public bool ReadOnly { get; set; }
        public DateTime? LastWriteTimeUtc(string path) => Times.TryGetValue(path, out var time) ? time : null;
        public void Touch(string path)
        {
            if (ReadOnly) throw new UnauthorizedAccessException("Access to the path is denied.");
            Touches.Add(path);
            Times[path] = DateTime.UtcNow;
        }
    }

    private sealed class Lines : IRunicOutput
    {
        public List<string> All { get; } = new();
        public void WriteLine(string line) => All.Add(line);
    }

    private static string WithState(string text, string state) => text.Replace("\"state\": \"ready\"", $"\"state\": \"{state}\"");

    // ---- Session file -----------------------------------------------------------------------------------------------

    [Test]
    public void The_session_file_parses_only_for_its_schema_and_ignores_added_properties()
    {
        var session = DevSessions.Parse(Fixture("dev-session-ready.json"))!;
        Check.Equal(DevSessionState.Ready, session.State);
        Check.Equal(41250, session.ProcessId);
        Check.Equal(DevStarted, session.ProcessStartedAt);
        Check.Equal("/app/App.csproj", session.Project);
        Check.Equal("vite", session.Frontend!.DevelopmentServer);
        Check.Equal("http://127.0.0.1:5173/", session.ServerUrl);
        Check.Equal("/app/Frontend/src/generated", session.Frontend.GeneratedDirectory);
        Check.Same(new[] { "index.html http://127.0.0.1:5173/index.html" }, session.Pages.Select(page => page.Document + " " + page.Url));
        Check.Equal("dev", session.Window!.Owner);
        Check.Equal(41377, session.Window.ProcessId);
        Check.Equal(1, session.Window.Starts);
        Check.Equal("Development", session.Window.Environment["DOTNET_ENVIRONMENT"]);
        Check.Same(new[] { "webview2 cdp http://127.0.0.1:9222/" }, session.DebugEndpoints.Select(endpoint => $"{endpoint.Engine} {endpoint.Protocol} {endpoint.Url}"));

        var noWindow = DevSessions.Parse(Fixture("dev-session-no-window.json"))!;
        Check.True(noWindow.Window!.OwnedByIde, "owner ide");
        Check.Equal(null, noWindow.ProcessStartedAt);
        Check.Equal(null, noWindow.Window.ProcessId);
        Check.Equal(null, noWindow.ServerUrl);

        string text = Fixture("dev-session-ready.json");
        Check.Equal(null, DevSessions.Parse(text.Replace("dev-session/1", "dev-session/2")));
        Check.Equal(null, DevSessions.Parse(WithState(text, "paused")));
        Check.Equal(null, DevSessions.Parse("{"));
        Check.Equal(null, DevSessions.Parse("[]"));
        Check.Equal(null, DevSessions.Parse(text.Replace("\"processId\": 41250,", "")));
        Check.Equal(Path.Combine(Path.GetDirectoryName("/app/App.csproj")!, "obj", "runic", "dev-session.json"), DevSessions.PathFor("/app/App.csproj"));
        Check.Equal("ready", DevSessions.Parse(text.Replace(DevSessions.Schema, DevSessions.RaxSchema))?.State.ToString().ToLowerInvariant());
    }

    [Test]
    public void After_the_rename_the_session_file_is_under_obj_rax_when_the_manifest_lists_RunicArtifex_Tool()
    {
        using var root = new TempDirectory();
        root.Write("App/App.csproj", "<Project />");
        string project = root.Full("App/App.csproj");
        Check.Equal(root.Full("App/obj/runic/dev-session.json"), DevSessions.PathFor(project, null));
        root.Write("dotnet-tools.json", """{ "isRoot": true, "tools": { "runicartifex.tool": { "version": "0.25.0", "commands": [ "rax" ] } } }""");
        Check.Equal(root.Full("App/obj/rax/dev-session.json"), DevSessions.PathFor(project));
        Check.Same(new[] { "rax", "dev", "--no-window", "--project", project }, DevOutput.Arguments(project, noWindow: true));
        Check.Equal("rax", Doctor.Arguments(project)[0]);
        root.Write(".config/dotnet-tools.json", """{ "tools": { "dotnet-runic": { "version": "0.24.0", "commands": [ "dotnet-runic" ] } } }""");
        Check.Equal(root.Full("App/obj/runic/dev-session.json"), DevSessions.PathFor(project));
        Check.Same(new[] { "runic", "dev", "--project", project }, DevOutput.Arguments(project, noWindow: false));
    }

    [Test]
    public void Dotnet_runic_dev_command_lines_are_recognized_as_the_DLL_the_apphost_or_the_tool_name()
    {
        Check.True(DevSessions.IsDevCommandLine(new[] { "/usr/share/dotnet/dotnet", "exec", "--runtimeconfig", "x.json", "/home/u/.nuget/packages/dotnet-runic/0.20.0/tools/net10.0/any/dotnet-runic.dll", "dev", "--no-window" }), "dll");
        Check.True(DevSessions.IsDevCommandLine(new[] { "/home/u/.dotnet/tools/dotnet-runic", "dev" }), "tool");
        Check.True(DevSessions.IsDevCommandLine(new[] { @"C:\Users\u\.nuget\packages\dotnet-runic\0.20.0\tools\net10.0\any\dotnet-runic.exe", "dev" }), "apphost");
        Check.True(!DevSessions.IsDevCommandLine(new[] { "/usr/share/dotnet/dotnet", "exec", "/x/dotnet-runic.dll", "doctor" }), "doctor");
        Check.True(!DevSessions.IsDevCommandLine(new[] { "node", "vite", "dev" }), "vite");
        Check.True(!DevSessions.IsDevCommandLine(new[] { "/app/bin/Debug/net10.0/App" }), "app");
        // After the rename: RunicArtifex.Tool's DLL under dotnet, the rax shim, and under NativeAOT rax itself (R5 A7: no dotnet parent).
        Check.True(DevSessions.IsDevCommandLine(new[] { "/usr/share/dotnet/dotnet", "exec", "/home/u/.nuget/packages/runicartifex.tool/0.25.0/tools/net10.0/any/RunicArtifex.Tool.dll", "dev" }), "RunicArtifex.Tool.dll");
        Check.True(DevSessions.IsDevCommandLine(new[] { "/home/u/.nuget/packages/runicartifex.tool/0.25.0/tools/net10.0/linux-x64/rax", "dev", "--no-window" }), "native rax");
        Check.True(DevSessions.IsDevCommandLine(new[] { @"C:\Users\u\.dotnet\tools\rax.exe", "dev" }), "rax.exe");
        Check.True(!DevSessions.IsDevCommandLine(new[] { "/usr/bin/rax", "doctor" }), "rax doctor");
        Check.True(!DevSessions.IsDevCommandLine(new[] { "/usr/bin/raxx", "dev" }), "lookalike");
    }

    [Test]
    public void Liveness_is_the_process_ID_plus_its_start_time_and_falls_back_to_the_command_line_or_image()
    {
        var source = new MemorySource();
        var session = DevSessions.Parse(Fixture("dev-session-ready.json"))!;
        Check.True(!DevSessions.IsRunning(session, source), "no process");
        source.Processes[41250] = new DevProcessInfo(DevStarted.AddMilliseconds(400), "dotnet");
        Check.True(DevSessions.IsRunning(session, source), "start time within a second");
        source.Processes[41250] = new DevProcessInfo(DevStarted.AddMinutes(5), "dotnet");
        Check.True(!DevSessions.IsRunning(session, source), "a reused process ID");
        source.Processes[41250] = new DevProcessInfo(null, null);
        Check.True(DevSessions.IsRunning(session, source), "a process that cannot be inspected counts as running");

        // Before 0.20 (no processStartedAt): the command line where readable, else the image name.
        var old = DevSessions.Parse(Fixture("dev-session-no-window.json"))!;
        source.Processes[41250] = new DevProcessInfo(DateTimeOffset.UtcNow, "dotnet", new[] { "dotnet", "exec", "/x/dotnet-runic.dll", "dev", "--no-window" });
        Check.True(DevSessions.IsRunning(old, source), "dev command line");
        source.Processes[41250] = new DevProcessInfo(DateTimeOffset.UtcNow, "node", new[] { "node", "vite" });
        Check.True(!DevSessions.IsRunning(old, source), "another command line");
        source.Processes[41250] = new DevProcessInfo(DateTimeOffset.UtcNow, "dotnet-runic");
        Check.True(DevSessions.IsRunning(old, source), "image dotnet-runic");
        source.Processes[41250] = new DevProcessInfo(DateTimeOffset.UtcNow, "rax");
        Check.True(DevSessions.IsRunning(old, source), "the rax image (NativeAOT, no dotnet parent)");
        source.Processes[41250] = new DevProcessInfo(DateTimeOffset.UtcNow, "RunicArtifex.Tool");
        Check.True(DevSessions.IsRunning(old, source), "the RunicArtifex.Tool apphost");
        source.Processes[41250] = new DevProcessInfo(DateTimeOffset.UtcNow, "notepad");
        Check.True(!DevSessions.IsRunning(old, source), "image notepad");
    }

    [Test]
    public void The_follower_reports_changes_once_keeps_the_last_snapshot_for_foreign_files_and_reads_a_killed_dev_as_stopped()
    {
        var source = new MemorySource();
        var follower = new DevSessionFollower("/app/App.csproj", source);
        var seen = new List<string>();
        follower.Changed += (_, snapshot) => seen.Add(snapshot.Label);
        Check.Equal(DevState.None, follower.Refresh().State);
        string ready = Fixture("dev-session-ready.json");
        source.Processes[41250] = new DevProcessInfo(DevStarted, "dotnet");
        source.Files[follower.Path] = WithState(ready, "starting");
        Check.Equal(DevState.Starting, follower.Refresh().State);
        source.Files[follower.Path] = ready;
        Check.Equal(DevState.Ready, follower.Refresh().State);
        Check.True(follower.Snapshot.IsActive, "ready is active");
        follower.Refresh();
        // dev was killed: the file keeps "ready", but its process is gone.
        source.Processes.Remove(41250);
        var killed = follower.Refresh();
        Check.Equal(DevState.Stopped, killed.State);
        Check.True(killed.Exited, "exited");
        Check.Equal("stopped (process 41250 exited)", killed.Label);
        follower.Refresh();
        // An unreadable file keeps the last snapshot; a stopped file needs no process; a deleted one is none.
        source.Files[follower.Path] = "{\"schema\":";
        Check.Equal(DevState.Stopped, follower.Refresh().State);
        source.Files[follower.Path] = WithState(ready, "stopped");
        Check.True(!follower.Refresh().Exited, "a file that says stopped");
        source.Files.Remove(follower.Path);
        Check.Equal(DevState.None, follower.Refresh().State);
        Check.Same(new[] { "starting", "ready", "stopped (process 41250 exited)", "stopped", "not started" }, seen);
    }

    [Test]
    public void The_stopped_stamp_is_needed_only_for_a_starting_or_ready_session_whose_dev_exited_and_not_stamped_since()
    {
        var session = DevSessions.Parse(Fixture("dev-session-ready.json"))!;
        var stopped = DevSessions.Parse(WithState(Fixture("dev-session-ready.json"), "stopped"))!;
        var written = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var exited = new DevSnapshot(DevState.Stopped, session, exited: true);
        Check.True(DevSessions.NeedsStoppedStamp(exited, null, written), "no stamp yet");
        Check.True(DevSessions.NeedsStoppedStamp(exited, written.AddHours(-1), written), "the last clean stop's stamp");
        Check.True(DevSessions.NeedsStoppedStamp(exited, written, null), "session time unknown");
        Check.True(!DevSessions.NeedsStoppedStamp(exited, written.AddSeconds(1), written), "stamped since the session was written");
        Check.True(!DevSessions.NeedsStoppedStamp(exited, written, written), "stamped as the session was written");
        Check.True(!DevSessions.NeedsStoppedStamp(new DevSnapshot(DevState.Ready, session, exited: false), null, written), "a live dev");
        Check.True(!DevSessions.NeedsStoppedStamp(new DevSnapshot(DevState.Stopped, stopped, exited: false), null, written), "a file that says stopped");
        Check.True(!DevSessions.NeedsStoppedStamp(DevSnapshot.None, null, null), "no session");
        Check.Equal(Path.Combine(Path.GetDirectoryName(DevSessions.PathFor("/app/App.csproj"))!, "dev-stopped"), DevSessions.StoppedStampPathFor(DevSessions.PathFor("/app/App.csproj")));
    }

    [Test]
    public void The_follower_touches_dev_stopped_once_per_dead_dev_and_never_for_a_live_or_stopped_one()
    {
        var source = new MemorySource();
        var stamp = new MemoryStamp();
        var lines = new Lines();
        var follower = new DevSessionFollower("/app/App.csproj", source, stamp: stamp, output: lines);
        string ready = Fixture("dev-session-ready.json");
        source.Processes[41250] = new DevProcessInfo(DevStarted, "dotnet");
        source.Files[follower.Path] = WithState(ready, "starting");
        follower.Refresh();
        source.Files[follower.Path] = ready;
        follower.Refresh();
        // A live dev, and one whose start time cannot be read, are never stamped.
        source.Processes[41250] = new DevProcessInfo(null, null);
        follower.Refresh();
        Check.Equal(0, stamp.Touches.Count);
        // Killed: the stamp beside this session file, once, however often the follower polls.
        source.Processes.Remove(41250);
        Check.True(follower.Refresh().Exited, "exited");
        follower.Refresh();
        follower.Refresh();
        Check.Same(new[] { follower.StoppedStampPath }, stamp.Touches);
        Check.Equal(Path.Combine(Path.GetDirectoryName(follower.Path)!, "dev-stopped"), follower.StoppedStampPath);
        Check.True(lines.All.Single().Contains("process 41250") && lines.All.Single().Contains("dev-stopped"), string.Join("\n", lines.All));
        // A file that says stopped: dev touched the stamp itself.
        source.Files[follower.Path] = WithState(ready, "stopped");
        follower.Refresh();
        Check.Equal(1, stamp.Touches.Count);
        // A new dev with another process ID that is killed is stamped again.
        source.Files[follower.Path] = ready.Replace("\"processId\": 41250,", "\"processId\": 41300,");
        follower.Refresh();
        Check.Equal(2, stamp.Touches.Count);

        // A stamp already newer than the session file (touched before a restart, or by another IDE) is left alone.
        var again = new MemoryStamp();
        var restarted = new DevSessionFollower("/app/App.csproj", source, stamp: again);
        again.Times[restarted.Path] = DateTime.UtcNow.AddMinutes(-5);
        again.Times[restarted.StoppedStampPath] = DateTime.UtcNow.AddMinutes(-1);
        Check.True(restarted.Refresh().Exited, "exited");
        Check.Equal(0, again.Touches.Count);

        // A read-only obj/runic is logged, not thrown, and not retried on each poll.
        var denied = new MemoryStamp { ReadOnly = true };
        var deniedLines = new Lines();
        var readOnly = new DevSessionFollower("/app/App.csproj", source, stamp: denied, output: deniedLines);
        Check.True(readOnly.Refresh().Exited, "exited");
        readOnly.Refresh();
        Check.Equal(1, deniedLines.All.Count);
        Check.True(deniedLines.All[0].Contains("could not be touched"), deniedLines.All[0]);

        // Without a stamp (another source), the follower only reads.
        Check.True(new DevSessionFollower("/app/App.csproj", source).Refresh().Exited, "exited");
    }

    [Test]
    public void The_follower_reads_a_session_file_replaced_atomically_and_a_real_process_by_its_start_time()
    {
        string root = Path.Combine(Path.GetTempPath(), $"runic-vs-dev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "obj", "runic"));
        using var sleeper = Sleeper();
        try
        {
            string project = Path.Combine(root, "App.csproj");
            File.WriteAllText(project, "<Project />");
            var lines = new Lines();
            var follower = new DevSessionFollower(project, output: lines);
            void Replace(int processId, DateTimeOffset started, string owner = "ide")
            {
                string text = Fixture("dev-session-no-window.json")
                    .Replace("\"processId\": 41250,", $"\"processId\": {processId}, \"processStartedAt\": \"{started.UtcDateTime:o}\",")
                    .Replace("\"owner\": \"ide\"", $"\"owner\": \"{owner}\"");
                string staged = follower.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(staged, text);
                if (File.Exists(follower.Path)) File.Delete(follower.Path);
                File.Move(staged, follower.Path);
            }
            var started = new DateTimeOffset(sleeper.StartTime.ToUniversalTime(), TimeSpan.Zero);
            Replace(sleeper.Id, started);
            var ready = follower.Refresh();
            Check.Equal(DevState.Ready, ready.State);
            Check.Equal("ready (Window in the IDE)", ready.Label);
            Check.True(ready.IsReadyForIde, "ready for the IDE");
            // The same process ID with another start time is a reused ID.
            Replace(sleeper.Id, started.AddHours(-1));
            Check.True(follower.Refresh().Exited, "reused ID");
            // A reused ID means that dev is gone: dev-stopped is touched. Take it away, and follow anew (once per
            // process ID) to see a killed dev's own touch.
            Check.True(File.Exists(follower.StoppedStampPath), "the reused ID's dev-stopped");
            File.Delete(follower.StoppedStampPath);
            Replace(sleeper.Id, started, owner: "dev");
            follower = new DevSessionFollower(project, output: lines);
            Check.Equal("ready", follower.Refresh().Label);
            Check.True(!File.Exists(follower.StoppedStampPath), "no stamp while dev runs");
            sleeper.Kill();
            sleeper.WaitForExit();
            var before = DateTime.UtcNow.AddSeconds(-2);
            var stopped = follower.Refresh();
            Check.Equal(DevState.Stopped, stopped.State);
            Check.True(stopped.Exited, "a dead dev exited");
            Check.True(File.Exists(follower.StoppedStampPath), "the killed dev's dev-stopped");
            Check.Equal(0L, new FileInfo(follower.StoppedStampPath).Length);
            Check.True(File.GetLastWriteTimeUtc(follower.StoppedStampPath) >= before, "touched now");
            Check.True(File.GetLastWriteTimeUtc(follower.StoppedStampPath) >= File.GetLastWriteTimeUtc(follower.Path), "newer than the session file");

            // A missing obj/runic is logged, not thrown.
            string other = Path.Combine(root, "Other", "Other.csproj");
            var missing = new DevSessionFollower(other, new MemorySource { Files = { [DevSessions.PathFor(other)] = File.ReadAllText(follower.Path) } }, stamp: FileSystemDevSessionSource.Instance, output: lines);
            Check.True(missing.Refresh().Exited, "exited");
            Check.True(lines.All.Last().Contains("could not be touched"), lines.All.Last());
        }
        finally
        {
            try { if (!sleeper.HasExited) sleeper.Kill(); } catch (InvalidOperationException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process Sleeper() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command Start-Sleep 60") { UseShellExecute = false, CreateNoWindow = true })!
        : Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;

    // ---- dotnet runic dev's output ----------------------------------------------------------------------------------

    [Test]
    public void Output_lines_become_Error_List_entries_as_the_runic_dev_problem_matcher_reads_them()
    {
        string project = Path.Combine(Path.GetTempPath(), "app", "App.csproj");
        string directory = Path.GetDirectoryName(project)!;
        var fault = DevOutput.Parse("/app/App.csproj : error RAPPDEV1007: The vite development server exited unexpectedly with code 1.", directory, "0.21.0")!;
        Check.Equal(RunicErrorSeverity.Error, fault.Severity);
        Check.Equal("RAPPDEV1007", fault.Code);
        Check.Equal("RAPPDEV1007: The vite development server exited unexpectedly with code 1.", fault.Text);
        Check.Equal("/app/App.csproj", fault.File);
        Check.Equal(0, fault.Line);
        Check.Equal("https://github.com/Runic-Artifex/runic-sdk/blob/v0.21.0/docs/diagnostics.md#rappdev1007", fault.HelpUri);

        // MSBuild's node prefix, a relative file, a range and the project suffix.
        var warning = DevOutput.Parse("3>CounterViewModel.cs(10,9,10,20): warning RUNICBRIDGE1003: The state Count has no setter. [/app/App.csproj]", directory)!;
        Check.Equal(RunicErrorSeverity.Warning, warning.Severity);
        Check.Equal(Path.Combine(directory, "CounterViewModel.cs"), warning.File);
        Check.Equal(9, warning.Line);
        Check.Equal(8, warning.Column);
        Check.Equal("RUNICBRIDGE1003: The state Count has no setter.", warning.Text);
        Check.Equal("https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge1003", warning.HelpUri);

        // A C# error has no Runic catalog link; colour codes are ignored; human text is no entry.
        var csharp = DevOutput.Parse("\u001b[31m/app/CounterViewModel.cs(18,5): error CS1002: ; expected [/app/App.csproj]\u001b[0m", directory)!;
        Check.Equal("CS1002: ; expected", csharp.Text);
        Check.Equal("", csharp.HelpUri);
        Check.Equal(null, DevOutput.Parse("[dev] Ready", directory));
        Check.Equal(null, DevOutput.Parse("  App -> /app/bin/Debug/net10.0/App.dll", directory));
        Check.True(DevOutput.IsReady("[dev] Ready"), "ready");
        Check.True(!DevOutput.IsReady("[dev] Ready soon"), "not ready");
        Check.Same(new[] { "runic", "dev", "--no-window", "--project", project }, DevOutput.Arguments(project, noWindow: true));
        Check.Same(new[] { "runic", "dev", "--project", project }, DevOutput.Arguments(project, noWindow: false));
    }

    [Test]
    public void A_new_build_cycle_replaces_the_entries_of_the_previous_one_and_duplicates_count_once()
    {
        var lines = Fixture("dev-output.txt").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var problems = new DevProblems("/app/App.csproj", noWindow: false);
        var changes = lines.Select(problems.Add).ToList();
        // "[dev] Ready" follows a build that succeeded, so it drops the errors read before it and keeps the warnings.
        Check.Same(new[] { "RUNICBRIDGE1003", "RAPPDEV1007" }, problems.Entries.Select(entry => entry.Code));
        Check.Equal(4, changes.Count(changed => changed));
        // With the Window, "Window exited" and "Reloading" start a cycle; without it, only "Evaluating" does.
        Check.True(problems.Add("[dev] Window exited: process 41377"), "window exited clears");
        Check.Equal(0, problems.Entries.Count);
        Check.True(!problems.Add("[dev] Reloading the Runic Views Window application."), "nothing to clear");

        var noWindow = new DevProblems("/app/App.csproj", noWindow: true);
        foreach (string line in lines) noWindow.Add(line);
        Check.True(!noWindow.Add("[dev] Window exited: process 41377"), "--no-window keeps entries");
        Check.Equal(2, noWindow.Entries.Count);
        Check.True(noWindow.Add("[dev] Evaluating Views Window project..."), "evaluating clears");
    }

    [Test]
    public void A_fixed_build_error_leaves_the_error_list_when_dev_is_ready_again()
    {
        // Row 7 of the manual checklist (0.22.0.1000): dotnet watch restarts after the fix without "Window exited".
        var problems = new DevProblems("/app/App.csproj", noWindow: false);
        Check.True(problems.Add("[dev] Window exited: process 3928") == false, "nothing to clear yet");
        Check.True(problems.Add("[host] /app/WorkspaceViewModel.cs(77,26): error CS1002: ; expected [/app/App.csproj]"), "the error");
        Check.True(!problems.Add("[host] dotnet watch ❌ Exited with error code 1"), "no entry");
        Check.True(!problems.Add("[host] dotnet watch ⌚ Waiting for changes"), "no entry");
        Check.True(!problems.Add("[dev] Window started: process 12516"), "no change");
        Check.True(problems.Add("[dev] Ready"), "ready clears the error");
        Check.Equal(0, problems.Entries.Count);
        Check.True(!problems.Add("[dev] Ready"), "nothing left to clear");
        // The same error again is a new entry after the clear.
        Check.True(problems.Add("[host] /app/WorkspaceViewModel.cs(77,26): error CS1002: ; expected [/app/App.csproj]"), "again");
        Check.Equal(1, problems.Entries.Count);
    }

    [Test]
    public void A_frontend_error_stays_until_dev_says_the_frontend_errors_are_cleared()
    {
        // runic-editors#52: dev's [vite] lines of runic-sdk 0.23 (runic-sdk#1212, recorded output around a .NET build).
        var lines = Fixture("dev-output-vite.txt").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var problems = new DevProblems("/tmp/t1160/App/App.csproj", noWindow: false, toolVersion: "0.23.0");
        foreach (string line in lines.Take(5)) problems.Add(line);
        var frontend = problems.Entries.Single();
        Check.Equal("RUNICP008", frontend.Code);
        Check.Equal("/tmp/t1160/App/Frontend/src/App.tsx", frontend.File, "the [vite] label is not part of the file");
        Check.Equal(28, frontend.Line);
        Check.Equal(49, frontend.Column);
        Check.Equal("RUNICP008: [vite:oxc] [PARSE_ERROR] Expected corresponding JSX closing tag for 'p'.", frontend.Text);
        Check.Equal("https://github.com/Runic-Artifex/runic-sdk/blob/v0.23.0/docs/diagnostics.md#runicp008", frontend.HelpUri);
        // A .NET build error comes and goes at [dev] Ready; the frontend error stays.
        Check.True(problems.Add(lines[5]), "build error");
        Check.True(problems.Add(lines[6]), "ready drops the build error");
        Check.Same(new[] { "RUNICP008" }, problems.Entries.Select(entry => entry.Code));
        Check.True(!problems.Add(lines[7]), "Vite's own lines are no rows");
        Check.True(problems.Add(lines[8]), "the clear line drops the frontend error");
        Check.Equal(0, problems.Entries.Count);
        Check.True(!problems.Add(lines[8]), "nothing left to clear");
        Check.True(DevOutput.IsFrontendErrorsCleared("\x1B[2m[vite] [runic] Frontend errors cleared\x1B[0m\r"), "colour codes and CR");
        Check.True(!DevOutput.IsFrontendErrorsCleared("[host] [runic] Frontend errors cleared"), "only dev's [vite] label");
    }

    [Test]
    public void The_clear_line_drops_only_the_frontend_errors_and_the_same_error_comes_back()
    {
        var problems = new DevProblems("/app/App.csproj", noWindow: true);
        Check.True(problems.Add("[vite] C:\\app\\Frontend\\src\\App.tsx(3,7): error RUNICP008: [vite:import-analysis] Failed to resolve import \"./Missing\""), "Windows path");
        Check.True(problems.Add("/app/CounterViewModel.cs(18,5): error CS1002: ; expected [/app/App.csproj]"), "build error");
        Check.True(problems.Add("3>CounterViewModel.cs(10,9,10,20): warning RUNICBRIDGE1003: The state Count has no setter. [/app/App.csproj]"), "warning");
        Check.True(problems.Add("[vite] [runic] Frontend errors cleared"), "cleared");
        Check.Same(new[] { "CS1002", "RUNICBRIDGE1003" }, problems.Entries.Select(entry => entry.Code), "C# rows stay");
        Check.True(problems.Add("[vite] C:\\app\\Frontend\\src\\App.tsx(3,7): error RUNICP008: [vite:import-analysis] Failed to resolve import \"./Missing\""), "the same error is a new row after the clear");
        Check.True(problems.Add("[dev] Evaluating Views Window project..."), "a new cycle drops everything");
        Check.Equal(0, problems.Entries.Count);
    }

    [Test]
    public void The_label_dev_puts_before_a_process_line_is_not_part_of_the_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "app");
        string source = Path.Combine(directory, "WorkspaceViewModel.cs");
        var entry = DevOutput.Parse($"[host] {source}(77,26): error CS1002: ; expected [{Path.Combine(directory, "App.csproj")}]", directory)!;
        Check.Equal(source, entry.File);
        Check.Equal(76, entry.Line);
        Check.Equal(25, entry.Column);
        Check.Equal("CS1002: ; expected", entry.Text);
        Check.Equal(Path.Combine(directory, "Counter.cs"), DevOutput.Parse("\u001b[2m[app]\u001b[22m Counter.cs(3,1): warning CS0168: unused", directory)!.File);
        // Vite's errors have no MSBuild form; they stay in the Runic SDK pane (README "dev errors").
        Check.Equal(null, DevOutput.Parse("[vite] 21:16:18 [vite] (client) Internal server error: Transform failed with 1 error:", directory));
        Check.Equal(null, DevOutput.Parse("[vite]   File: C:/app/Frontend/src/App.tsx:7581:23", directory));
    }

    [Test]
    public void A_started_dev_streams_its_lines_and_stops()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        string root = Path.Combine(Path.GetTempPath(), $"runic-vs-devprocess-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string project = Path.Combine(root, "App.csproj");
            var lines = new List<string>();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // A stand-in for dotnet runic dev: Ready on standard output, a fault on standard error, then it waits.
            using var dev = DevProcess.Start("/bin/sh", new[] { "-c", "echo '[dev] Ready'; echo 'App.csproj : error RAPPDEV1007: failed.' >&2; exec sleep 60" }, project, noWindow: true, line =>
            {
                lock (lines) lines.Add(line);
                if (DevOutput.IsReady(line)) ready.TrySetResult(true);
            });
            Check.True(dev.ProcessId > 0, "process ID");
            Check.True(ready.Task.Wait(TimeSpan.FromSeconds(20)), "Ready line");
            Check.True(!dev.HasExited, "still runs");
            dev.StopAsync(TimeSpan.FromSeconds(10)).Wait();
            Check.True(dev.HasExited, "stopped");
            lock (lines) Check.True(lines.Contains("App.csproj : error RAPPDEV1007: failed."), "standard error");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A stand-in for <c>dotnet runic dev</c> on <c>/bin/sh</c> (none on Windows): Ready, then <paramref name="script"/>.</summary>
    private static DevProcess FakeDev(string root, string script, bool stopOnInput, List<string> lines, TaskCompletionSource<bool> ready)
        => DevProcess.Start("/bin/sh", new[] { "-c", "echo '[dev] Ready'; " + script }, Path.Combine(root, "App.csproj"), noWindow: true, line =>
        {
            lock (lines) lines.Add(line);
            if (DevOutput.IsReady(line)) ready.TrySetResult(true);
        }, stopOnInput);

    private static async Task WithRoot(Func<string, Task> test)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        string root = Path.Combine(Path.GetTempPath(), $"runic-vs-devstop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { await test(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public Task A_0_22_dev_stops_on_a_stop_line_on_its_input() => WithRoot(async root =>
    {
        var lines = new List<string>();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // As dev 0.22 with RUNIC_DEV_STOP_ON_INPUT=1 (runic-sdk#1092): a stop line ends it with 130, as Ctrl+C does.
        using var dev = FakeDev(root, "echo \"variable=$RUNIC_DEV_STOP_ON_INPUT\"; read line; echo \"input=$line\"; exit 130", stopOnInput: true, lines, ready);
        Check.True(dev.StopOnInput, "stop on input");
        Check.True(ready.Task.Wait(TimeSpan.FromSeconds(20)), "Ready line");
        Check.Equal(DevStop.Input, await dev.StopAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        Check.Equal(130, await dev.Exited);
        lock (lines)
        {
            Check.True(lines.Contains("variable=1"), "RUNIC_DEV_STOP_ON_INPUT=1 in dev's environment");
            Check.True(lines.Contains("input=stop"), "the stop line");
        }
        Check.Equal(DevStop.AlreadyExited, await dev.StopAsync(TimeSpan.FromSeconds(1)));
    });

    [Test]
    public Task Cancelling_a_stop_ends_the_wait_and_not_the_stop() => WithRoot(async root =>
    {
        var lines = new List<string>();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // runic-editors#44: Visual Studio closing while Stop waits for a dev that ignores its input.
        using var dev = FakeDev(root, "trap '' INT; exec sleep 60", stopOnInput: true, lines, ready);
        Check.True(ready.Task.Wait(TimeSpan.FromSeconds(20)), "Ready line");
        using var closing = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;
        try { await dev.StopAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), closing.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check.True(cancelled, "the wait is cancelled");
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"returned promptly ({watch.Elapsed})");
        Check.Equal(DevStop.Forced, await dev.StopAsync(TimeSpan.Zero, TimeSpan.FromSeconds(10)));
    });

    [Test]
    public Task A_0_22_dev_stops_at_the_end_of_its_input() => WithRoot(async root =>
    {
        var lines = new List<string>();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Lines other than stop are ignored by dev; the closed pipe still ends it.
        using var dev = FakeDev(root, "cat > /dev/null; echo ended; exit 130", stopOnInput: true, lines, ready);
        Check.True(ready.Task.Wait(TimeSpan.FromSeconds(20)), "Ready line");
        Check.Equal(DevStop.Input, await dev.StopAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        lock (lines) Check.True(lines.Contains("ended"), "end of input");
    });

    [Test]
    public Task A_dev_that_ignores_its_input_is_stopped_by_force_after_the_wait() => WithRoot(async root =>
    {
        var lines = new List<string>();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // An older dev, or one that hangs while stopping: the bounded wait ends and the forced stop follows.
        using var dev = FakeDev(root, "exec sleep 60", stopOnInput: true, lines, ready);
        Check.True(ready.Task.Wait(TimeSpan.FromSeconds(20)), "Ready line");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Check.Equal(DevStop.Forced, await dev.StopAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10)));
        Check.True(watch.Elapsed >= TimeSpan.FromMilliseconds(450), "waited for the input stop first");
        Check.True(dev.HasExited, "stopped");
    });

    [Test]
    public Task Without_stop_on_input_dev_gets_no_variable_and_is_stopped_by_force() => WithRoot(async root =>
    {
        var lines = new List<string>();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? inherited = Environment.GetEnvironmentVariable(DevProcess.StopOnInputVariable);
        Environment.SetEnvironmentVariable(DevProcess.StopOnInputVariable, "1");
        try
        {
            // dotnet-runic 0.21: the variable is not passed on even when Visual Studio's own environment has it.
            using var dev = FakeDev(root, "echo \"variable=${RUNIC_DEV_STOP_ON_INPUT:-unset}\"; exec sleep 60", stopOnInput: false, lines, ready);
            Check.True(!dev.StopOnInput, "no stop on input");
            Check.True(ready.Task.Wait(TimeSpan.FromSeconds(20)), "Ready line");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Check.Equal(DevStop.Forced, await dev.StopAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(9), "no wait for an input stop");
            lock (lines) Check.True(lines.Contains("variable=unset"), "no variable");
        }
        finally { Environment.SetEnvironmentVariable(DevProcess.StopOnInputVariable, inherited); }
    });

    [Test]
    public Task A_dev_that_exits_while_a_child_holds_its_output_has_exited() => WithRoot(async root =>
    {
        var lines = new List<string>();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The 0.22 checklist, row 6: dev stops on its input and exits, but its frontend server lives on with dev's
        // output pipe. dev has exited all the same, so Start works again.
        using var dev = FakeDev(root, "sleep 60 & echo \"child=$!\"; read line; exit 130", stopOnInput: true, lines, ready);
        Check.True(ready.Task.Wait(TimeSpan.FromSeconds(20)), "Ready line");
        int child = 0;
        for (int attempt = 0; attempt < 100 && child == 0; attempt++)
        {
            lock (lines) child = lines.Where(line => line.StartsWith("child=", StringComparison.Ordinal)).Select(line => int.Parse(line.Substring(6))).FirstOrDefault();
            if (child == 0) await Task.Delay(100);
        }
        Check.True(child > 0, "the child's process ID");
        try
        {
            var watch = Stopwatch.StartNew();
            Check.Equal(DevStop.Input, await dev.StopAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
            Check.Equal(130, await dev.Exited);
            Check.True(watch.Elapsed < DevProcess.OutputDrainTimeout + TimeSpan.FromSeconds(3), $"exited when the process did, not when the pipe closed ({watch.Elapsed})");
            Check.True(dev.HasExited, "has exited");
        }
        finally { try { Process.GetProcessById(child).Kill(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { } }
    });

    [Test, NotInParallel("pings")]
    public async Task A_forced_stop_on_Windows_ends_processes_whose_parent_has_exited()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        string root = Path.Combine(Path.GetTempPath(), $"runic-vs-devtree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var before = new HashSet<int>(Process.GetProcessesByName("PING").Select(process => process.Id));
        try
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // A stand-in for dev on Windows: an inner cmd starts ping and exits at once, so ping's parent is gone and
            // taskkill /T on dev cannot find it (Vite under bun under dev, row 6); ping also holds dev's output pipe.
            // It starts them only once Start has returned (a line on its input), as dev starts its children only after
            // the dotnet host has loaded: a child started before the job and whose parent exited before Start swept
            // for children cannot be found (runic-editors#58, where this test raced the job).
            using var dev = DevProcess.Start("cmd.exe", new[] { "/d", "/c", "echo [dev] Ready& set /p go=& start /b cmd /d /c start /b ping -n 121 127.0.0.1& ping -n 60 127.0.0.1 >nul" },
                Path.Combine(root, "App.csproj"), noWindow: true, line => { if (DevOutput.IsReady(line)) ready.TrySetResult(true); });
            dev.WriteInputLine("go");
            Check.True(ready.Task.Wait(TimeSpan.FromSeconds(20)), "Ready line");
            List<int> Orphans() => Process.GetProcessesByName("PING").Select(process => process.Id).Where(id => !before.Contains(id)).ToList();
            for (int attempt = 0; attempt < 100 && Orphans().Count < 2; attempt++) await Task.Delay(100);
            Check.True(Orphans().Count >= 1, "the orphaned ping runs");
            Check.Equal(DevStop.Forced, await dev.StopAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
            Check.True(dev.HasExited, "dev has exited");
            for (int attempt = 0; attempt < 50 && Orphans().Count > 0; attempt++) await Task.Delay(100);
            Check.Equal(0, Orphans().Count, "no ping left running");
        }
        finally
        {
            foreach (var process in Process.GetProcessesByName("PING").Where(process => !before.Contains(process.Id)))
                try { process.Kill(); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Test, NotInParallel("pings")]
    public async Task A_forced_stop_on_Windows_ends_children_dev_started_before_its_job()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // runic-editors#58: Process.Start returns a running process, so a child it starts at once can come before the job
        // is assigned. Start puts those in the job as well; ending the job first used to cut taskkill's parent links.
        string root = Path.Combine(Path.GetTempPath(), $"runic-vs-devjob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var before = new HashSet<int>(Process.GetProcessesByName("PING").Select(process => process.Id));
        List<int> Pings() => Process.GetProcessesByName("PING").Select(process => process.Id).Where(id => !before.Contains(id)).ToList();
        try
        {
            using var dev = DevProcess.Start("cmd.exe", new[] { "/d", "/c", "start /b ping -n 121 127.0.0.1 >nul& echo [dev] Ready& ping -n 60 127.0.0.1 >nul" },
                Path.Combine(root, "App.csproj"), noWindow: true, _ => { });
            for (int attempt = 0; attempt < 100 && Pings().Count < 2; attempt++) await Task.Delay(100);
            Check.Equal(2, Pings().Count, "both pings run");
            Check.Equal(DevStop.Forced, await dev.StopAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
            for (int attempt = 0; attempt < 50 && Pings().Count > 0; attempt++) await Task.Delay(100);
            Check.Equal(0, Pings().Count, "no ping left running");
        }
        finally
        {
            foreach (var process in Process.GetProcessesByName("PING").Where(process => !before.Contains(process.Id)))
                try { process.Kill(); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Start_never_does_nothing_without_saying_why()
    {
        Check.Equal(DevStartStep.Start, DevStartGate.Decide(ownedRunning: false, stopping: false));
        Check.Equal(DevStartStep.Start, DevStartGate.Decide(ownedRunning: false, stopping: true), "a stop that has finished");
        Check.Equal(DevStartStep.WaitForStop, DevStartGate.Decide(ownedRunning: true, stopping: true));
        Check.Equal(DevStartStep.AlreadyRunning, DevStartGate.Decide(ownedRunning: true, stopping: false));
        Check.True(DevStartGate.StopWait >= DevProcess.DefaultInputStopTimeout + DevProcess.OutputDrainTimeout, "the wait covers the input stop");
        string still = DevStartGate.StillStopping("App.csproj", 9416);
        Check.True(still.Contains("process 9416") && still.Contains("App.csproj") && still.Contains("Stop dotnet runic dev"), still);
        string running = DevStartGate.AlreadyRunning("App.csproj", 9416, "starting");
        Check.True(running.Contains("process 9416") && running.Contains("starting"), running);
        Check.True(DevStartGate.Waiting("App.csproj", 9416).Contains("process 9416"), "waiting line");
        Check.True(DevStartGate.AlreadyActive("App.csproj", null, "ready").Contains("already runs for App.csproj (ready)"), "already active");
        Check.True(DevStartGate.EndedLeftovers(2).StartsWith("Ended 2 processes", StringComparison.Ordinal), "leftovers");
    }

    [Test]
    public void Starting_a_missing_program_says_which()
    {
        try
        {
            DevProcess.Start(Path.Combine(Path.GetTempPath(), "no-such-dotnet-" + Guid.NewGuid().ToString("N")), Array.Empty<string>(), "/app/App.csproj", false, _ => { });
            Check.True(false, "no exception");
        }
        catch (InvalidOperationException error) { Check.True(error.Message.StartsWith("Could not start "), error.Message); }
    }

    // ---- Pre-F5 guard and targets -----------------------------------------------------------------------------------

    [Test]
    public void The_pre_F5_guard_asks_unless_dev_no_window_runs_for_the_IDE()
    {
        var source = new MemorySource();
        source.Processes[41250] = new DevProcessInfo(null);
        DevSnapshot Of(string fixture, string state = "ready") => DevSessions.Snapshot(WithState(Fixture(fixture), state), source)!;

        Check.Equal(DevGuardDecision.AskToStart, DevGuard.Decide(DevSnapshot.None));
        Check.Equal(DevGuardDecision.Launch, DevGuard.Decide(Of("dev-session-no-window.json")));
        Check.Equal(DevGuardDecision.LaunchWhileStarting, DevGuard.Decide(Of("dev-session-no-window.json", "starting")));
        Check.Equal(DevGuardDecision.AskToRestart, DevGuard.Decide(Of("dev-session-ready.json")));
        Check.Equal(DevGuardDecision.AskToStart, DevGuard.Decide(Of("dev-session-no-window.json", "stopped")));
        source.Processes.Clear();
        Check.Equal(DevGuardDecision.AskToStart, DevGuard.Decide(Of("dev-session-no-window.json")));

        string question = DevGuard.Question("/app/App.csproj", DevGuardDecision.AskToStart, startedHere: false);
        Check.True(question.StartsWith("dotnet runic dev --no-window does not run for App.csproj."), question);
        Check.True(question.Contains("No: continue without it."), question);
        Check.True(DevGuard.Question("/app/App.csproj", DevGuardDecision.AskToRestart, startedHere: false).Contains("Ctrl+C"), "outside VS");
        Check.True(DevGuard.Question("/app/App.csproj", DevGuardDecision.AskToRestart, startedHere: true).Contains("Restart it with --no-window"), "ours");
    }

    [Test]
    public void The_pre_F5_guard_checks_F5_and_Ctrl_F5_of_a_Runic_startup_project_from_design_mode()
    {
        // Visual Studio's guidVSStd97 cmdidStart (Debug.Start, F5) and cmdidStartNoDebug (Debug.StartWithoutDebugging, Ctrl+F5).
        var std97 = new Guid("{5EFC7975-14BC-11CF-9B2B-00AA00573819}");
        Check.Equal<bool?>(true, DevGuard.LaunchKind(std97, 295), "F5");
        Check.Equal<bool?>(false, DevGuard.LaunchKind(std97, 368), "Ctrl+F5");
        Check.Equal<bool?>(null, DevGuard.LaunchKind(std97, 296), "another Debug command");
        Check.Equal<bool?>(null, DevGuard.LaunchKind(Guid.NewGuid(), 295), "another command set");

        string app = Path.Combine("C:", "src", "App", "App.csproj"), tests = Path.Combine("C:", "src", "Tests", "Tests.csproj");
        var sdk = new[] { app };
        Check.Equal(app, DevGuard.Target(new[] { app }, sdk, debuggerInDesignMode: true));
        Check.Equal(app, DevGuard.Target(new[] { tests, app.ToUpperInvariant() }, sdk, debuggerInDesignMode: true), "the first Runic startup project, any case");
        Check.Equal<string?>(null, DevGuard.Target(new[] { app }, sdk, debuggerInDesignMode: false), "F5 continues a session");
        Check.Equal<string?>(null, DevGuard.Target(new[] { tests }, sdk, debuggerInDesignMode: true), "not a Runic startup project");
        Check.Equal<string?>(null, DevGuard.Target(Array.Empty<string>(), sdk, debuggerInDesignMode: true), "no startup project");
    }

    [Test]
    public void The_pre_F5_guard_finds_the_startup_project_of_a_project_opened_without_a_solution()
    {
        // The single-project template has no .sln: Visual Studio opens App.csproj alone, Solution.FullName is empty and
        // the startup project's UniqueName is relative (0.24.0 checklist P6: .NET Framework's Path.GetDirectoryName("")
        // threw "The path is not of a legal form", and F5 asked nothing).
        string root = Path.Combine(Path.GetTempPath(), "runic-guard"), app = Path.Combine(root, "App", "App.csproj");
        var loaded = new[] { new LoadedProject("App.csproj", app) };
        foreach (string? solution in new[] { "", null, "   " })
        {
            var startup = StartupProjectFiles.Resolve(new[] { "App.csproj" }, solution, loaded);
            Check.Same(new[] { app }, startup, $"project-only workspace, Solution.FullName '{solution}'");
            Check.Equal(app, DevGuard.Target(startup, new[] { app }, debuggerInDesignMode: true), "the guard asks about it");
        }
        Check.Same(new[] { app }, StartupProjectFiles.Resolve(new[] { "App\\App.csproj".Replace('\\', Path.DirectorySeparatorChar) }, "", new[] { new LoadedProject("other", app) }),
            "no matching UniqueName: the only loaded project of that file name");
        Check.Same(new[] { app }, StartupProjectFiles.Resolve(new[] { app }, "", Array.Empty<LoadedProject>()), "a rooted name");

        // With a solution, as before: the UniqueName relative to the solution's folder.
        string solutionFile = Path.Combine(root, "App.slnx");
        Check.Same(new[] { app }, StartupProjectFiles.Resolve(new[] { Path.Combine("App", "App.csproj") }, solutionFile, Array.Empty<LoadedProject>()), "relative to the .sln");
        Check.Same(new[] { app }, StartupProjectFiles.Resolve(new[] { Path.Combine("App", "App.csproj") }, solutionFile, new[] { new LoadedProject(Path.Combine("App", "App.csproj"), app) }), "by UniqueName");
        Check.Equal(0, StartupProjectFiles.Resolve(new[] { "" }, "", loaded).Count, "an empty name");
    }

    [Test]
    public void A_failed_F5_check_says_so_and_lets_the_user_choose()
    {
        string message = DevGuard.CheckFailed("The path is not of a legal form.");
        Check.True(message.StartsWith("The F5 check for dotnet runic dev failed: The path is not of a legal form."), message);
        Check.True(message.Contains("OK: start without dotnet runic dev --no-window") && message.Contains("Cancel: do not start."), message);
    }

    [Test]
    public void A_dev_command_acts_on_the_only_Window_project_the_active_documents_or_the_startup_project()
    {
        string root = Path.GetTempPath();
        string app = Path.Combine(root, "app", "App.csproj"), tool = Path.Combine(root, "tool", "Tool.csproj");
        Check.Equal(app, DevTargets.Pick(new[] { app }, null, null));
        Check.Equal(tool, DevTargets.Pick(new[] { app, tool }, Path.Combine(root, "tool", "Views", "Main.cs"), new[] { app }));
        Check.Equal(app, DevTargets.Pick(new[] { app, tool }, Path.Combine(root, "README.md"), new[] { app }));
        try { DevTargets.Pick(new[] { app, tool }, null, null); Check.True(false, "no exception"); }
        catch (InvalidOperationException error) { Check.Equal(DevTargets.Ambiguous, error.Message); }
        try { DevTargets.Pick(Array.Empty<string>(), null, null); Check.True(false, "no exception"); }
        catch (InvalidOperationException error) { Check.Equal(SdkSections.NoSdkProject, error.Message); }
    }
}
