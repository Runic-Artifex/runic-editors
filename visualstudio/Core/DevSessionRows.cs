using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// The Dev Session section as VS Code's Dev Session view shows it (<c>vscode/src/sdk.ts</c> <c>devRoots</c>,
/// <c>devDetails</c>): the state, and while <c>dev</c> runs the development server or watcher, the Window's owner or
/// process, the pages, the <c>debugEndpoints</c> and the generated clients. With several projects, one project row each.
/// Also the toolbar's enabled commands and the status text (VS Code's "Runic dev" status bar item).
/// </summary>
public static class DevSessionRows
{
    public const string Welcome = "dotnet runic dev serves the frontend and runs the Window with hot reload. Start it here, or press F5 to start it with --no-window.";

    public static IReadOnlyList<TreeRow> Build(IReadOnlyList<SdkProjectState> projects, Func<string, DevSnapshot> snapshotOf)
        => SdkSections.PerProject(projects, state =>
        {
            var snapshot = snapshotOf(state.Project);
            return Details(state.Project, snapshot, FrontendDependencies.Read(state.Project, snapshot));
        }, state => snapshotOf(state.Project).Label);

    /// <summary>
    /// The rows of one project; <paramref name="dependencies"/> (<see cref="FrontendDependencies.Read"/>) adds the
    /// Frontend dependencies row, while <c>dev</c> runs and also when it does not.
    /// </summary>
    public static IReadOnlyList<TreeRow> Details(string project, DevSnapshot snapshot, DependencyList? dependencies = null)
    {
        var rows = SessionRows(project, snapshot);
        if (DependencyRow(project, dependencies) is { } row) rows.Add(row);
        return rows;
    }

    /// <summary>
    /// "Frontend dependencies" with one row per package (VS Code: <c>dependencyRows</c>): its owner, state and watcher,
    /// and a failure's message below it. A row opens the package's package.json. Visual Studio's build (F5) builds them
    /// through MSBuild, so the section offers no build or install of its own.
    /// </summary>
    public static TreeRow? DependencyRow(string project, DependencyList? list)
    {
        if (list == null || list.Dependencies.Count == 0) return null;
        int count = list.Dependencies.Count, unbuilt = list.Dependencies.Count(item => item.NeedsBuild);
        string source = list.Source switch { DependencySource.Session => "from dotnet runic dev", DependencySource.Graph => "from the last build", _ => "from the last dev session" };
        var row = new TreeRow("Frontend dependencies", RowIcon.Folder, project)
        {
            Description = $"{count} package{(count == 1 ? "" : "s")}{(unbuilt > 0 ? $", {unbuilt} not built" : "")}",
            Tooltip = $"The frontend packages {Path.GetFileName(project)}'s page imports, dependencies first, {source}. Building the project (F5) builds them.",
            Expanded = true,
        };
        foreach (var dependency in list.Dependencies)
        {
            var child = new TreeRow(dependency.Label, dependency.State switch
            {
                "built" => RowIcon.Pass, "building" => RowIcon.Running, "failed" => RowIcon.Error, "missing-output" => RowIcon.Warning,
                "source" => RowIcon.Page, "not-built" => RowIcon.NotRun, _ => RowIcon.None,
            }, project)
            {
                Description = string.Join(" · ", new[] { dependency.OwnerName, dependency.StateLabel, dependency.WatchLabel }.Where(part => !string.IsNullOrEmpty(part))),
                Tooltip = DependencyTooltip(dependency),
                Action = RowAction.OpenFile,
                Target = Path.Combine(dependency.Directory, "package.json"),
                Expanded = dependency.Message != null,
            };
            if (dependency.Message != null) child.Children.Add(new TreeRow(dependency.Message, RowIcon.Error, project) { Tooltip = dependency.Message });
            row.Children.Add(child);
        }
        return row;
    }

    private static string DependencyTooltip(DevDependency dependency)
    {
        var lines = new List<string>
        {
            $"{dependency.Label} ({dependency.Kind})",
            dependency.Owner != null ? $"The frontend package of {dependency.OwnerName ?? Path.GetFileNameWithoutExtension(dependency.Owner)} ({dependency.Owner})." : "A workspace package that no .NET project owns.",
            dependency.Directory,
        };
        if (dependency.Specifier != null) lines.Add("Imported as " + dependency.Specifier);
        if (dependency.BuildCommand != null) lines.Add("Build: " + dependency.BuildCommand);
        if (dependency.WatchCommand != null) lines.Add("Watch: " + dependency.WatchCommand);
        if (dependency.State == "missing-output") lines.Add("Missing: an exported file. Build the project to build it.");
        if (dependency.Message != null) lines.Add("\n" + dependency.Message);
        return string.Join("\n", lines);
    }

    private static List<TreeRow> SessionRows(string project, DevSnapshot snapshot)
    {
        var session = snapshot.Session;
        var rows = new List<TreeRow>
        {
            new($"dotnet runic dev: {snapshot.Label}", snapshot.State switch { DevState.Ready => RowIcon.DevReady, DevState.Starting => RowIcon.DevStarting, _ => RowIcon.DevStopped }, project)
            {
                Tooltip = snapshot.Exited
                    ? $"The session file says \"{session?.State.ToString().ToLowerInvariant()}\", but process {session?.ProcessId} is no longer dotnet runic dev, so the session counts as stopped."
                    : DevSessions.PathFor(project),
                Action = session != null ? RowAction.OpenFile : RowAction.None,
                Target = session != null ? DevSessions.PathFor(project) : null,
            },
        };
        if (session == null || !snapshot.IsActive) return rows;
        var frontend = session.Frontend;
        if (frontend?.Url != null)
            rows.Add(new TreeRow($"Development server: {frontend.Url}", RowIcon.Globe, project) { Description = frontend.DevelopmentServer, Action = RowAction.OpenUrl, Target = frontend.Url, Tooltip = "Open in the browser" });
        else if (frontend != null)
            rows.Add(new TreeRow("Frontend: watcher", RowIcon.Watcher, project) { Description = frontend.OutputDirectory });
        var window = session.Window;
        if (window?.OwnedByIde == true)
            rows.Add(new TreeRow("Window: started by the IDE", RowIcon.IdeWindow, project) { Description = "F5", Tooltip = "dotnet runic dev --no-window: start debugging the project (F5) to start the Window." });
        else if (window != null)
            rows.Add(new TreeRow(window.ProcessId is { } id ? $"Window: process {id}" : "Window: not running", RowIcon.Window, project)
            {
                Description = window.Starts > 0 ? $"{window.Starts} start{(window.Starts == 1 ? "" : "s")}" : null,
            });
        foreach (var page in session.Pages.Where(page => page.Url != null))
            rows.Add(new TreeRow($"Page: {page.Document}", RowIcon.Page, project) { Description = page.Url, Action = RowAction.OpenUrl, Target = page.Url, Tooltip = "Open in the browser" });
        foreach (var endpoint in session.DebugEndpoints)
            rows.Add(new TreeRow($"Debug endpoint: {endpoint.Engine}", RowIcon.DebugEndpoint, project) { Description = $"{endpoint.Protocol} {endpoint.Url}" });
        if (frontend?.GeneratedDirectory is { } generated)
            rows.Add(new TreeRow("Generated clients", RowIcon.Folder, project) { Description = Relative(project, generated), Tooltip = generated, Action = RowAction.OpenFolder, Target = generated });
        return rows;
    }

    private static string Relative(string project, string path)
    {
        string directory = (Path.GetDirectoryName(project) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return directory.Length > 1 && normalized.StartsWith(directory.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? normalized.Substring(directory.Length).Replace('\\', '/') : path;
    }

    /// <summary>
    /// The status text (VS Code's status bar item): with one project its state, with several how many run. Visual Studio
    /// has no persistent status bar item, so the extension shows it on each change (PARITY §2.2).
    /// </summary>
    public static string StatusText(IReadOnlyList<DevSnapshot> snapshots)
    {
        if (snapshots.Count == 1) return $"Runic dev: {(snapshots[0].State == DevState.None ? "not started" : snapshots[0].State.ToString().ToLowerInvariant())}";
        return $"Runic dev: {snapshots.Count(snapshot => snapshot.IsActive)} of {snapshots.Count} running";
    }
}

/// <summary>
/// Which dev commands the toolbar and the Tools → Runic menu enable, from the snapshots of the solution's projects (VS
/// Code's view title buttons with <c>runic.devActive</c>; its Actions quick pick offers Start while <c>dev</c> does not run).
/// </summary>
public sealed class DevCommandState
{
    public DevCommandState(IReadOnlyList<(DevSnapshot Snapshot, bool StartedHere)> projects)
    {
        bool any = projects.Count > 0;
        CanStart = any && projects.Any(project => !project.Snapshot.IsActive);
        CanStop = projects.Any(project => project.StartedHere || project.Snapshot.IsActive);
        CanOpenServer = projects.Any(project => project.Snapshot.State == DevState.Ready && project.Snapshot.Session?.ServerUrl != null);
        CanOpenSessionFile = projects.Any(project => project.Snapshot.Session != null);
    }

    /// <summary>Start dotnet runic dev and its <c>--no-window</c> form: some project's <c>dev</c> does not run.</summary>
    public bool CanStart { get; }
    /// <summary>Some project's <c>dev</c> runs (Stop explains when it was started elsewhere).</summary>
    public bool CanStop { get; }
    public bool CanOpenServer { get; }
    public bool CanOpenSessionFile { get; }
}
