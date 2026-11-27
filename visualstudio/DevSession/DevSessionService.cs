using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Output;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// The <c>dotnet runic dev</c> session of each Runic SDK project of the solution (VS Code: <c>sdk.ts</c>, the dev parts
/// of <c>ProjectState</c>). It follows each project's session file (<see cref="DevSessionFollower"/>) after a file
/// watcher event and every 2 seconds, because a killed <c>dev</c> leaves <c>"ready"</c> behind (the follower then
/// touches <c>obj/runic/dev-stopped</c>, so the next F5 builds); starts <c>dev</c> and
/// <c>dev --no-window</c> as child processes whose output goes to the "Runic SDK" Output pane and whose diagnostics go
/// to the Error List (<see cref="DevErrorList"/>); and stops the ones it started. A <c>dev</c> started elsewhere (a
/// terminal) is followed but cannot be stopped from here: Windows has no clean Ctrl+C for another console (PARITY §7.3).
/// </summary>
internal sealed class DevSessionService
{
    public static DevSessionService Instance { get; } = new();

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private Timer? poll;
    private DevErrorList? errorList;
    private bool initialized;

    private DevSessionService() { }

    /// <summary>The follower's lines (a touched <c>dev-stopped</c>) for the Runic SDK pane, wherever <see cref="SdkOutput"/> points.</summary>
    private sealed class SdkLines : IRunicOutput
    {
        public static SdkLines Instance { get; } = new();
        public void WriteLine(string line) => SdkOutput.WriteLine(line);
    }

    private sealed class Entry
    {
        public Entry(DevSessionFollower follower) { Follower = follower; }
        public DevSessionFollower Follower { get; }
        public FileSystemWatcher? Watcher { get; set; }
        /// <summary>The <c>dev</c> this extension started, until it exits.</summary>
        public DevProcess? Run { get; set; }
        /// <summary>Whether Stop has asked <see cref="Run"/> to stop and it has not exited yet.</summary>
        public bool Stopping { get; set; }
        public DevProblems? Problems { get; set; }
        public int ProblemsVersion;
        /// <summary>How the last <c>dev</c> this extension started ended, and which run that was.</summary>
        public DevExit? LastExit { get; set; }
        public DevProcess? LastExitRun { get; set; }
        /// <summary>How many F5 or Debug the Window waits report this project's <c>dev</c> exit themselves.</summary>
        public int Waiting;
    }

    /// <summary>
    /// A <c>dev</c> this extension started, that exited with tools missing while nothing waited for it (Start dotnet runic
    /// dev): the project and its exit. The package offers the restore (VS Code: the same offer from the Dev Session view).
    /// </summary>
    public event Action<string, DevExit>? ExitedWithoutWaiter;

    /// <summary>Raised on any thread when a project's dev snapshot or its owned process changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The project's snapshot; <see cref="DevSnapshot.None"/> for a project the service does not follow.</summary>
    public DevSnapshot Snapshot(string project)
    {
        lock (gate) return entries.TryGetValue(project, out var entry) ? entry.Follower.Snapshot : DevSnapshot.None;
    }

    /// <summary>Whether this extension started the project's running <c>dev</c>.</summary>
    public bool StartedHere(string project)
    {
        lock (gate) return entries.TryGetValue(project, out var entry) && entry.Run is { HasExited: false };
    }

    public string SessionPath(string project) => DevSessions.PathFor(project);

    /// <summary>Which dev commands are enabled (the toolbar and Tools → Runic).</summary>
    public DevCommandState CommandState()
    {
        lock (gate) return new DevCommandState(entries.Values.Select(entry => (entry.Follower.Snapshot, entry.Run is { HasExited: false })).ToList());
    }

    /// <summary>Starts following the solution's projects and the pre-F5 guard; stops owned <c>dev</c> processes when Visual Studio closes.</summary>
    public async Task InitializeAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        if (initialized) return;
        initialized = true;
        RunicSdkState.Instance.Changed += (_, _) => Sync();
        package.DisposalToken.Register(StopAllOwned);
        // A dev started from Tools > Runic or the Dev Session view that exits for missing tools: the same offer as F5's.
        ExitedWithoutWaiter += (project, exit) => _ = package.JoinableTaskFactory.RunAsync(() => ToolRestore.ReportAsync(package, project, exit, package.DisposalToken));
        await DevLaunchGuard.AdviseAsync(package, cancellationToken);
        await TaskScheduler.Default;
        try { await RunicSdkState.Instance.EnsureScannedAsync(cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { SdkOutput.WriteLine("Could not find the Runic SDK projects: " + error.Message); }
        Sync();
    }

    /// <summary>Follows exactly the Window projects <see cref="RunicSdkState"/> found; a library is no dev target.</summary>
    private void Sync()
    {
        var projects = RunicSdkState.Instance.DevTargets;
        var added = new List<Entry>();
        lock (gate)
        {
            foreach (string project in entries.Keys.Where(project => !projects.Contains(project, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                // A project that left the solution keeps its owned dev running only as long as Visual Studio does.
                entries[project].Watcher?.Dispose();
                entries.Remove(project);
            }
            foreach (string project in projects.Where(project => !entries.ContainsKey(project)))
            {
                var entry = new Entry(new DevSessionFollower(project, output: SdkLines.Instance));
                entry.Follower.Changed += (_, snapshot) => OnSnapshot(entry, snapshot);
                entry.Watcher = Watch(project);
                entries[project] = entry;
                added.Add(entry);
            }
            if (entries.Count > 0) poll ??= new Timer(_ => RefreshAll(), null, PollInterval, PollInterval);
            else { poll?.Dispose(); poll = null; }
        }
        foreach (var entry in added) entry.Follower.Refresh();
    }

    /// <summary>As VS Code's <c>**/obj/runic/dev-session.json</c> watcher.</summary>
    private FileSystemWatcher? Watch(string project)
    {
        string? directory = Path.GetDirectoryName(project);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
        try
        {
            var watcher = new FileSystemWatcher(directory!, "dev-session.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
            FileSystemEventHandler changed = (_, _) => Refresh(project);
            watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += changed;
            watcher.Renamed += (_, _) => Refresh(project);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception error) when (error is ArgumentException or IOException)
        {
            SdkOutput.WriteLine("Cannot watch " + directory + " for the dev session file: " + error.Message);
            return null;
        }
    }

    private void RefreshAll()
    {
        List<Entry> all;
        lock (gate) all = entries.Values.ToList();
        foreach (var entry in all) entry.Follower.Refresh();
    }

    /// <summary>Rereads the project's session file now; returns its snapshot.</summary>
    public DevSnapshot Refresh(string project)
    {
        Entry? entry;
        lock (gate) entries.TryGetValue(project, out entry);
        return entry?.Follower.Refresh() ?? DevSnapshot.None;
    }

    private void OnSnapshot(Entry entry, DevSnapshot snapshot)
    {
        SdkOutput.WriteLine($"{Path.GetFileName(entry.Follower.Project)}: dotnet runic dev {snapshot.Label}{(snapshot.Session != null ? $" (process {snapshot.Session.ProcessId})" : "")}");
        string status;
        lock (gate) status = DevSessionRows.StatusText(entries.Values.Select(item => item.Follower.Snapshot).ToList());
        _ = RunicSdkState.Background.RunAsync(() => ShowStatusAsync(status));
        Raise();
    }

    /// <summary>VS Code's "Runic dev" status bar item; Visual Studio's status text is shared, so it shows on each change.</summary>
    private static async Task ShowStatusAsync(string text)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
        {
            bar.IsFrozen(out int frozen);
            if (frozen == 0) bar.SetText(text);
        }
    }

    private void Raise()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        RunicSdkState.Instance.NotifyChanged();
        _ = RunicSdkState.Background.RunAsync(UpdateCommandsAsync);
    }

    /// <summary>The toolbar's start and stop buttons follow the state at once, not at the next idle query.</summary>
    private static async Task UpdateCommandsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsUIShell)) is IVsUIShell shell) shell.UpdateCommandUI(0);
    }

    // ---- Picking a project ------------------------------------------------------------------------------------------

    /// <summary>The project a command acts on: <paramref name="project"/>, else <see cref="DevTargets.Pick"/> of the Window projects.</summary>
    public async Task<string> PickAsync(string? project, CancellationToken cancellationToken)
    {
        await RunicSdkState.Instance.EnsureScannedAsync(cancellationToken);
        Sync();
        if (project != null) return project;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        string? active = null;
        var startup = new List<string>();
        try { active = dte?.ActiveDocument?.FullName; } catch (ArgumentException) { }
        try { startup.AddRange(StartupProjects(dte)); } catch (Exception error) when (error is ArgumentException or System.Runtime.InteropServices.COMException) { }
        var state = RunicSdkState.Instance;
        return DevTargets.Pick(state.DevTargets, active, startup, state.Libraries);
    }

    /// <summary>The startup projects' files.</summary>
    internal static IEnumerable<string> StartupProjects(EnvDTE.DTE? dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (dte?.Solution?.SolutionBuild?.StartupProjects is not object[] names) yield break;
        string? directory = Path.GetDirectoryName(dte.Solution.FullName);
        foreach (string name in names.OfType<string>())
            yield return Path.IsPathRooted(name) || string.IsNullOrEmpty(directory) ? name : Path.GetFullPath(Path.Combine(directory, name));
    }

    // ---- Start and stop ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts <c>dotnet runic dev</c> (or <c>--no-window</c>) for the project, as VS Code's <c>startDev</c>: nothing
    /// when it already runs in that mode; with the other mode, a <c>dev</c> this extension started restarts after a
    /// confirmation (<paramref name="confirmRestart"/>), and one started elsewhere has to be stopped in its terminal.
    /// </summary>
    /// <returns>The <c>dev</c> it started, or null when it started none.</returns>
    public async Task<DevProcess?> StartAsync(string project, bool noWindow, Func<string, Task<bool>> confirmRestart, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;
        var snapshot = Refresh(project);
        if (snapshot.IsActive)
        {
            bool ideOwned = snapshot.Session?.Window?.OwnedByIde == true;
            string name = Path.GetFileName(project);
            if (ideOwned == noWindow)
            {
                SdkOutput.WriteLine(DevStartGate.AlreadyActive(name, snapshot.Session?.ProcessId, snapshot.Label));
                return null;
            }
            if (!StartedHere(project))
                throw new InvalidOperationException($"dotnet runic dev (process {snapshot.Session?.ProcessId}) already runs for {name}{(ideOwned ? " without the Window" : " with its Window")} and was started outside Visual Studio; stop it with Ctrl+C in its terminal first.");
            if (!await confirmRestart($"dotnet runic dev already runs for {name}{(ideOwned ? " without the Window" : " with its Window")}. Stop it and start dotnet runic dev{(noWindow ? " --no-window" : "")}?")) return null;
            await StopAsync(project, cancellationToken);
            await WaitForAsync(project, current => !current.IsActive, TimeSpan.FromSeconds(30), null, cancellationToken);
        }
        Entry? entry;
        lock (gate) entries.TryGetValue(project, out entry);
        if (entry == null) throw new InvalidOperationException($"{project} is not a Runic SDK project of this solution.");
        DevProcess? previous;
        bool stopping;
        lock (gate) { previous = entry.Run is { HasExited: false } running ? running : null; stopping = entry.Stopping; }
        switch (DevStartGate.Decide(previous != null, stopping))
        {
            case DevStartStep.WaitForStop:
                SdkOutput.WriteLine(DevStartGate.Waiting(Path.GetFileName(project), previous!.ProcessId));
                if (await Task.WhenAny(previous.Exited, Task.Delay(DevStartGate.StopWait, cancellationToken)) != previous.Exited)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new InvalidOperationException(DevStartGate.StillStopping(Path.GetFileName(project), previous.ProcessId));
                }
                break;
            case DevStartStep.AlreadyRunning:
                throw new InvalidOperationException(DevStartGate.AlreadyRunning(Path.GetFileName(project), previous!.ProcessId, Refresh(project).Label));
        }

        string dotnet = await RunicSdkState.DotnetAsync(cancellationToken);
        await TaskScheduler.Default;
        string? manifestVersion = RunicSdkProjects.ProjectToolVersion(project);
        string? toolVersion = await DevToolVersions.DetectAsync(dotnet, project, manifestVersion, cancellationToken);
        // D070.17: a CS-WebUI Window opens its DevTools Protocol port only when an IDE asks, with dev's 0.22 --debug-endpoint.
        bool debugEndpoint = !noWindow && DevToolVersions.SupportsDebugEndpoint(toolVersion);
        // WIN-06: the 0.22 dev stops on a stop line on its input and runs its own stop instead of being ended by force.
        bool stopOnInput = DevToolVersions.SupportsStopOnInput(toolVersion);
        var problems = new DevProblems(project, noWindow, manifestVersion);
        var lastLines = new DevExits.LastLines();
        lock (gate) { entry.Problems = problems; entry.ProblemsVersion++; }
        PublishProblems(project, entry, Array.Empty<RunicErrorEntry>());
        _ = RunicSdkState.Background.RunAsync(async () => await RunicOutput.ShowAsync(await RunicOutput.SdkAsync()));
        DevProcess run;
        try
        {
            run = DevProcess.Start(dotnet, project, noWindow, line =>
            {
                SdkOutput.WriteLine(line);
                lastLines.Add(line);
                bool changed;
                IReadOnlyList<RunicErrorEntry> current;
                lock (gate)
                {
                    if (!ReferenceEquals(entry.Problems, problems)) return;
                    changed = problems.Add(line);
                    current = changed ? problems.Entries : Array.Empty<RunicErrorEntry>();
                }
                if (changed) PublishProblems(project, entry, current);
                // dev writes the session file before its contract lines; read it at once instead of at the next poll.
                if (line.StartsWith("[dev] ", StringComparison.Ordinal)) entry.Follower.Refresh();
            }, debugEndpoint, stopOnInput);
        }
        catch (InvalidOperationException error)
        {
            SdkOutput.WriteLine(error.Message);
            throw new InvalidOperationException(error.Message + " Set the .NET executable in Tools > Options > " + RunicSettings.OptionsCategory + ", or RUNIC_DOTNET.", error);
        }
        SdkOutput.WriteLine("> " + run.CommandLine);
        lock (gate) entry.Run = run;
        Raise();
        _ = run.Exited.ContinueWith(task =>
        {
            SdkOutput.WriteLine($"dotnet runic dev exited with code {task.Result} ({Path.GetFileName(project)}).");
            if (run.EndedLeftovers > 0) SdkOutput.WriteLine(DevStartGate.EndedLeftovers(run.EndedLeftovers));
            DevExit exit;
            bool offer;
            lock (gate)
            {
                // Recorded before Run is cleared, so a wait that sees no running dev finds its exit.
                exit = new DevExit(task.Result, lastLines.Snapshot(), ReferenceEquals(entry.Problems, problems) ? problems.Entries.Count : 0);
                offer = task.Result != 0 && entry.Waiting == 0 && !entry.Stopping && exit.ToolsMissing;
                entry.LastExit = exit; entry.LastExitRun = run;
                if (ReferenceEquals(entry.Run, run)) { entry.Run = null; entry.Stopping = false; }
            }
            run.Dispose();
            entry.Follower.Refresh();
            Raise();
            if (offer) ExitedWithoutWaiter?.Invoke(project, exit);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return run;
    }

    /// <summary>
    /// Waits until the project's <c>dev</c> is ready for the IDE (owner <c>ide</c>), as F5 and Debug the Window do. When
    /// <paramref name="started"/> (the <c>dev</c> <see cref="StartAsync"/> just started) exits first, it throws
    /// <see cref="DevExitException"/> at once with its exit code and last lines (runic-editors#81's VS Code fix); a
    /// <c>dev</c> started elsewhere that stops throws <see cref="DevExits.StoppedElsewhere"/>.
    /// </summary>
    public async Task<DevSnapshot> WaitForReadyAsync(string project, DevProcess? started, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Entry? entry;
        lock (gate) { entries.TryGetValue(project, out entry); if (entry != null) entry.Waiting++; }
        try
        {
            return await WaitForAsync(project, snapshot => snapshot.IsReadyForIde, timeout, snapshot =>
            {
                if (StartedHere(project) || snapshot.IsActive) return null;
                DevExit? exit = null;
                lock (gate) if (entry != null && started != null && ReferenceEquals(entry.LastExitRun, started)) exit = entry.LastExit;
                if (exit != null) throw new DevExitException(project, exit);
                // The started dev's exit is recorded right after it exits: wait for the record.
                return started != null ? null : DevExits.StoppedElsewhere;
            }, cancellationToken);
        }
        finally { lock (gate) if (entry != null) entry.Waiting--; }
    }

    /// <summary>
    /// Stops the project's <c>dev</c> if this extension started it (with its Window and frontend server). One started
    /// elsewhere throws with the Ctrl+C hint (VS Code says the same on Windows); none running throws too unless <paramref name="quiet"/>.
    /// </summary>
    public async Task StopAsync(string project, CancellationToken cancellationToken, bool quiet = false)
    {
        await TaskScheduler.Default;
        DevProcess? run;
        Entry? entry;
        lock (gate) { entries.TryGetValue(project, out entry); run = entry?.Run; }
        if (run is { HasExited: false })
        {
            SdkOutput.WriteLine($"Stopping dotnet runic dev (process {run.ProcessId}) for {Path.GetFileName(project)}.");
            lock (gate) if (ReferenceEquals(entry!.Run, run)) entry.Stopping = true;
            var how = await run.StopAsync(DevProcess.DefaultInputStopTimeout, StopTimeout, cancellationToken);
            if (run.StopOnInput && how == DevStop.Forced)
                SdkOutput.WriteLine($"dotnet runic dev did not stop on its input within {DevProcess.DefaultInputStopTimeout.TotalSeconds:0} seconds; ended it by force.");
            Refresh(project);
            return;
        }
        var snapshot = Refresh(project);
        if (quiet) return;
        if (!snapshot.IsActive || snapshot.Session == null) throw new InvalidOperationException($"dotnet runic dev does not run for {Path.GetFileName(project)}.");
        throw new InvalidOperationException($"dotnet runic dev (process {snapshot.Session.ProcessId}) was started outside Visual Studio; stop it with Ctrl+C in its terminal.");
    }

    /// <summary>Resolves with the snapshot once <paramref name="predicate"/> holds; throws after <paramref name="timeout"/>, on cancellation, or when <paramref name="failWhen"/> says why.</summary>
    public async Task<DevSnapshot> WaitForAsync(string project, Func<DevSnapshot, bool> predicate, TimeSpan timeout, Func<DevSnapshot, string?>? failWhen, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var snapshot = Refresh(project);
            if (predicate(snapshot)) return snapshot;
            if (failWhen?.Invoke(snapshot) is { } failure) throw new InvalidOperationException(failure);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"dotnet runic dev did not get ready within {Math.Round(timeout.TotalSeconds)} seconds; see the Runic SDK Output pane.");
            await Task.Delay(500, cancellationToken);
        }
    }

    private void PublishProblems(string project, Entry entry, IReadOnlyList<RunicErrorEntry> current)
    {
        int version;
        lock (gate) version = ++entry.ProblemsVersion;
        _ = RunicSdkState.Background.RunAsync(() => ReplaceProblemsAsync(project, entry, version, current));
    }

    private async Task ReplaceProblemsAsync(string project, Entry entry, int version, IReadOnlyList<RunicErrorEntry> current)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        lock (gate) if (version != entry.ProblemsVersion) return;
        errorList ??= new DevErrorList(ServiceProvider.GlobalProvider);
        errorList.Replace(project, RunicSdkState.Instance.Hierarchy(project), current);
    }

    private void StopAllOwned()
    {
        List<DevProcess> owned;
        lock (gate) owned = entries.Values.Select(entry => entry.Run).OfType<DevProcess>().Where(run => !run.HasExited).ToList();
        // Visual Studio is closing: stop the processes, as closing VS Code ends its tasks. StopAsync writes the stop
        // request (0.22) or ends the process tree before its first await, so nothing waits here.
        foreach (var run in owned) _ = run.StopAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        lock (gate) { poll?.Dispose(); poll = null; }
    }
}
