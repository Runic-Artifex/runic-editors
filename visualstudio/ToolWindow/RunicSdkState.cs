using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// The Runic SDK projects of the open solution with their ViewModels tree and doctor results, shared by the tool
/// window's sections, the Refresh ViewModels and Run Doctor commands and the Error List (VS Code: <c>sdk.ts</c>
/// <c>ProjectState</c>). The parsing and tree building live in Core; this class finds projects, watches the
/// development source maps and runs <c>dotnet runic doctor</c>.
/// </summary>
internal sealed class RunicSdkState
{
    public static RunicSdkState Instance { get; } = new();

    /// <summary>
    /// Starts the tool window's fire-and-forget work (UI events, solution events) in one collection, as a package's
    /// JoinableTaskFactory does, so that Visual Studio can join it.
    /// </summary>
    public static JoinableTaskFactory Background { get; } = ThreadHelper.JoinableTaskContext.CreateFactory(ThreadHelper.JoinableTaskContext.CreateCollection());

    /// <summary>
    /// The <c>dotnet</c> executable: <c>RUNIC_DOTNET</c> or <c>dotnet</c> on PATH, as the language client uses. The
    /// Options page (VS-2) can replace this.
    /// </summary>
    public static Func<string> DotnetPath { get; set; } = () => Environment.GetEnvironmentVariable("RUNIC_DOTNET") is { Length: > 0 } path ? path : "dotnet";

    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private IReadOnlyList<SdkProjectState> projects = Array.Empty<SdkProjectState>();
    private IReadOnlyDictionary<string, IVsHierarchy> hierarchies = new Dictionary<string, IVsHierarchy>();
    private DoctorErrorList? errorList;
    private Timer? reloadTimer;
    private bool subscribed;
    private bool scanned;

    private RunicSdkState() { }

    /// <summary>Raised on any thread when projects, ViewModels or doctor results change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SdkProjectState> Projects { get { lock (gate) return projects; } }

    /// <summary>True once the solution has been searched for Runic SDK projects.</summary>
    public bool Scanned => scanned;

    /// <summary>Finds the solution's Runic SDK projects once, and again after a solution opens or closes.</summary>
    public async Task EnsureScannedAsync(CancellationToken cancellationToken)
    {
        if (!scanned) await RescanAsync(cancellationToken);
    }

    /// <summary>
    /// The <c>.csproj</c> files of the loaded solution that are Runic SDK projects; for a folder opened without a
    /// solution, those below the folder (VS Code's workspace detection). Then reloads their source maps.
    /// </summary>
    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Subscribe();
        var found = new Dictionary<string, IVsHierarchy>(StringComparer.OrdinalIgnoreCase);
        string? root = null;
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution)
        {
            if (ErrorHandler.Succeeded(solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, Guid.Empty, out var enumerator)) && enumerator != null)
            {
                var buffer = new IVsHierarchy[1];
                while (enumerator.Next(1, buffer, out uint fetched) == VSConstants.S_OK && fetched == 1)
                    if (buffer[0] is IVsProject project && ErrorHandler.Succeeded(project.GetMkDocument((uint)VSConstants.VSITEMID.Root, out string path)) && !string.IsNullOrEmpty(path))
                        found[path] = buffer[0];
            }
            if (ErrorHandler.Succeeded(solution.GetSolutionInfo(out string directory, out _, out _)) && !string.IsNullOrEmpty(directory)) root = directory;
        }
        await TaskScheduler.Default;
        var sdkProjects = RunicSdkProjects.Find(found.Keys);
        if (sdkProjects.Count == 0 && found.Count == 0 && root != null) sdkProjects = RunicSdkProjects.Scan(root);
        lock (gate)
        {
            var previous = projects.ToDictionary(state => state.Project, StringComparer.OrdinalIgnoreCase);
            projects = sdkProjects.Select(project => previous.TryGetValue(project, out var state) ? state : new SdkProjectState(project)).ToList();
            hierarchies = found;
            foreach (var state in projects) state.LoadSourceMap();
            Watch(projects.Select(state => state.Project));
        }
        scanned = true;
        Raise();
    }

    /// <summary>Refresh ViewModels: reads every project's newest development source map again.</summary>
    public async Task RefreshViewModelsAsync(CancellationToken cancellationToken)
    {
        if (!scanned) { await RescanAsync(cancellationToken); return; }
        await TaskScheduler.Default;
        foreach (var state in Projects) state.LoadSourceMap();
        Raise();
    }

    /// <summary>
    /// Runs <c>dotnet runic doctor --format json --fail-on never</c> for the given projects (or, without any, for
    /// <see cref="SdkSections.DoctorTargets"/>), shows the checks in the Doctor section and puts failing and warning
    /// checks into the Error List on the project file.
    /// </summary>
    public async Task RunDoctorAsync(IReadOnlyList<SdkProjectState>? targets, CancellationToken cancellationToken)
    {
        await EnsureScannedAsync(cancellationToken);
        if (Projects.Count == 0) throw new InvalidOperationException(SdkSections.NoSdkProject);
        if (targets == null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            string? active = null;
            try { active = dte?.ActiveDocument?.FullName; } catch (ArgumentException) { }
            targets = SdkSections.DoctorTargets(Projects, active);
        }
        await TaskScheduler.Default;
        await Task.WhenAll(targets.Select(state => RunDoctorAsync(state, cancellationToken)));
    }

    private async Task RunDoctorAsync(SdkProjectState state, CancellationToken cancellationToken)
    {
        state.DoctorRunning = true; Raise();
        string dotnet = DotnetPath();
        try
        {
            SdkOutput.WriteLine("> " + dotnet + " " + string.Join(" ", Doctor.Arguments(state.Project).Select(CommandLine.Quote)));
            var report = Doctor.Parse(await Doctor.RunAsync(dotnet, state.Project, cancellationToken));
            state.Doctor = report; state.DoctorError = null;
            SdkOutput.WriteLine("doctor: " + Doctor.Summary(report));
            foreach (var check in Doctor.OrderedChecks(report).Where(check => check.Status != DoctorStatus.Pass))
                SdkOutput.WriteLine($"  {check.Status.ToString().ToLowerInvariant()} {check.Id}: {check.Message}{(check.Remediation != null ? " Fix: " + check.Remediation : "")}");
            await ShowInErrorListAsync(state.Project, Doctor.ErrorListEntries(state.Project, report, RunicSdkProjects.ProjectToolVersion(state.Project)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            state.Doctor = null;
            state.DoctorError = error.Message.Split(new[] { '\r', '\n' }, 2)[0];
            SdkOutput.WriteLine("doctor failed: " + error.Message);
            await ShowInErrorListAsync(state.Project, Array.Empty<RunicErrorEntry>());
        }
        finally { state.DoctorRunning = false; Raise(); }
    }

    private async Task ShowInErrorListAsync(string project, IReadOnlyList<RunicErrorEntry> entries)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        errorList ??= new DoctorErrorList(ServiceProvider.GlobalProvider);
        IVsHierarchy? hierarchy;
        lock (gate) hierarchies.TryGetValue(project, out hierarchy);
        errorList.Replace(project, hierarchy, entries);
        if (entries.Count > 0) errorList.Show();
    }

    private void Subscribe()
    {
        if (subscribed) return;
        subscribed = true;
        SolutionEvents.OnAfterOpenSolution += (_, _) => Rescan();
        SolutionEvents.OnAfterBackgroundSolutionLoadComplete += (_, _) => Rescan();
        SolutionEvents.OnAfterOpenProject += (_, _) => Rescan();
        SolutionEvents.OnAfterCloseSolution += (_, _) => Rescan();
    }

    private void Rescan() => _ = RunicSdkState.Background.RunAsync(async () =>
    {
        try { await RescanAsync(CancellationToken.None); }
        catch (Exception error) { SdkOutput.WriteLine("Could not find the Runic SDK projects: " + error.Message); }
    });

    /// <summary>As VS Code's <c>**/obj/**/runic-bridge/sources.json</c> watcher: a build or <c>dev</c> reloads the tree, debounced.</summary>
    private void Watch(IEnumerable<string> projectFiles)
    {
        var wanted = new HashSet<string>(projectFiles.Select(project => Path.GetDirectoryName(project)!).Where(directory => !string.IsNullOrEmpty(directory)), StringComparer.OrdinalIgnoreCase);
        foreach (string directory in watchers.Keys.Where(directory => !wanted.Contains(directory)).ToList())
        {
            watchers[directory].Dispose();
            watchers.Remove(directory);
        }
        foreach (string directory in wanted.Where(directory => !watchers.ContainsKey(directory)))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory, "sources.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
                FileSystemEventHandler changed = (_, args) => { if (args.FullPath.IndexOf("runic-bridge", StringComparison.OrdinalIgnoreCase) >= 0) ScheduleReload(); };
                watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += changed;
                watcher.Renamed += (_, args) => changed(watcher, args);
                watcher.EnableRaisingEvents = true;
                watchers[directory] = watcher;
            }
            catch (Exception error) when (error is ArgumentException or IOException)
            {
                Trace.WriteLine("Runic: cannot watch " + directory + ": " + error.Message);
            }
        }
    }

    private void ScheduleReload()
    {
        lock (gate)
        {
            reloadTimer?.Dispose();
            reloadTimer = new Timer(_ =>
            {
                foreach (var state in Projects) state.LoadSourceMap();
                Raise();
            }, null, 200, Timeout.Infinite);
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The Runic SDK Output pane is VS-2's (<c>Output/</c>). Until it is wired here (<see cref="Target"/>), lines go to the
/// diagnostic trace, as the language server's stderr does today.
/// </summary>
internal static class SdkOutput
{
    public static IRunicOutput? Target { get; set; }

    public static void WriteLine(string line)
    {
        if (Target != null) Target.WriteLine(line);
        else Trace.WriteLine("Runic: " + line);
    }
}
