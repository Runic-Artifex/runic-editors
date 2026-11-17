using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    public void The_follower_reads_a_session_file_replaced_atomically_and_a_real_process_by_its_start_time()
    {
        string root = Path.Combine(Path.GetTempPath(), $"runic-vs-dev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "obj", "runic"));
        using var sleeper = Sleeper();
        try
        {
            string project = Path.Combine(root, "App.csproj");
            File.WriteAllText(project, "<Project />");
            var follower = new DevSessionFollower(project);
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
            Replace(sleeper.Id, started, owner: "dev");
            Check.Equal("ready", follower.Refresh().Label);
            sleeper.Kill();
            sleeper.WaitForExit();
            var stopped = follower.Refresh();
            Check.Equal(DevState.Stopped, stopped.State);
            Check.True(stopped.Exited, "a dead dev exited");
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
        Check.Same(new[] { "CS1002", "RUNICBRIDGE1003", "RAPPDEV1007" }, problems.Entries.Select(entry => entry.Code));
        Check.Equal(3, changes.Count(changed => changed));
        // With the Window, "Window exited" and "Reloading" start a cycle; without it, only "Evaluating" does.
        Check.True(problems.Add("[dev] Window exited: process 41377"), "window exited clears");
        Check.Equal(0, problems.Entries.Count);
        Check.True(!problems.Add("[dev] Reloading the Runic Views Window application."), "nothing to clear");

        var noWindow = new DevProblems("/app/App.csproj", noWindow: true);
        foreach (string line in lines) noWindow.Add(line);
        Check.True(!noWindow.Add("[dev] Window exited: process 41377"), "--no-window keeps entries");
        Check.Equal(3, noWindow.Entries.Count);
        Check.True(noWindow.Add("[dev] Evaluating Views Window project..."), "evaluating clears");
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
    public void A_dev_command_acts_on_the_only_project_the_active_documents_or_the_startup_project()
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
