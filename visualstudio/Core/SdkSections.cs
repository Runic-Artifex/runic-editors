using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>The icon of a tool-window row; the extension maps each to a Visual Studio image moniker.</summary>
public enum RowIcon
{
    None, Project, ViewModel, Property, Boolean, Method, Event, Interface, Field,
    Healthy, NotHealthy, Pass, Warning, Error, Running, NotRun, Fix,
    // The Dev Session section.
    DevReady, DevStarting, DevStopped, Globe, Watcher, Window, IdeWindow, Page, DebugEndpoint, Folder,
    // A note, such as the Dev Session section's "No Window project".
    Info,
}

/// <summary>What activating (double-clicking, Enter, Open C# Member) a row does.</summary>
public enum RowAction { None, OpenLocation, RunDoctor, OpenUrl, OpenFile, OpenFolder }

/// <summary>One row of a tool-window section tree; the WPF side renders it without further logic.</summary>
public sealed class TreeRow
{
    public TreeRow(string label, RowIcon icon, string? project = null)
    {
        Label = label; Icon = icon; Project = project;
    }

    public string Label { get; }
    public RowIcon Icon { get; }
    /// <summary>The Runic SDK project the row belongs to.</summary>
    public string? Project { get; }
    public string? Description { get; set; }
    public string? Tooltip { get; set; }
    public SourceLocation? Location { get; set; }
    /// <summary>The URL, file or folder that <see cref="RowAction.OpenUrl"/>, <see cref="RowAction.OpenFile"/> or <see cref="RowAction.OpenFolder"/> opens.</summary>
    public string? Target { get; set; }
    public RowAction Action { get; set; }
    public bool Expanded { get; set; }
    public List<TreeRow> Children { get; } = new();

    /// <summary>Label, then description, as the row reads.</summary>
    public override string ToString() => Description != null ? $"{Label} — {Description}" : Label;
}

/// <summary>What the tool window knows about one Runic SDK project of the solution.</summary>
public sealed class SdkProjectState
{
    public SdkProjectState(string project, bool isWindow = true) { Project = project; IsWindow = isWindow; }

    public string Project { get; }
    /// <summary>
    /// True for a Window project (<see cref="RunicSdkProjects.IsWindowProject"/>), a <c>dotnet runic dev</c> target;
    /// false for a library or test project, which the ViewModels and Doctor sections list but dev never targets.
    /// </summary>
    public bool IsWindow { get; set; }
    public string? SourceMapPath { get; private set; }
    public IReadOnlyList<SourceTreeNode>? SourceTree { get; private set; }
    public string? SourceMapError { get; private set; }
    public DoctorReport? Doctor { get; set; }
    public string? DoctorError { get; set; }
    public bool DoctorRunning { get; set; }

    /// <summary>Reads the newest development source map of the project again (Refresh ViewModels).</summary>
    public void LoadSourceMap()
    {
        SourceMapPath = SourceMap.Newest(Project);
        SourceTree = null; SourceMapError = null;
        if (SourceMapPath == null) return;
        try { SourceTree = SourceMapTree.Build(SourceMap.Parse(File.ReadAllText(SourceMapPath))); }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException) { SourceMapError = FirstLine(error.Message); }
    }

    internal static string FirstLine(string text)
    {
        string line = text.Split(new[] { '\r', '\n' }, 2)[0].Trim();
        return line.Length > 0 ? line : "Unknown error.";
    }
}

/// <summary>
/// The ViewModels and Doctor sections as VS Code's views show them (<c>vscode/src/sdk.ts</c> <c>viewModelRoots</c>,
/// <c>doctorRoots</c>): with one project its rows directly, with several one project row each.
/// </summary>
public static class SdkSections
{
    public const string NoSdkProject = "This solution has no Runic SDK project (a .csproj of a Runic Views Window or one that references Runic.Application, Runic.Desktop or Runic.Platform).";
    public const string ViewModelsWelcome = "No development source map yet. Build the project, or start dotnet runic dev, to list its ViewModels and Bridge members.";
    public const string DoctorWelcome = "dotnet runic doctor checks this machine and the project: the .NET SDK, the JavaScript runtime, lock files, the Window host's runtime and more.";

    /// <summary>The tooltip of the Dev Session section's "No Window project" row (VS Code's Dev Session view).</summary>
    public const string NoWindowProjectTooltip = "dotnet runic dev runs a Window project (RunicApplicationFrontendWindowProject, or an executable). The projects found here are libraries or tests; open the solution or folder of the Window project that references them.";

    /// <summary>
    /// Why no dev command has a target: no Runic SDK project at all, or only libraries and test projects (VS Code's
    /// <c>pick</c> message).
    /// </summary>
    public static string NoWindowProject(IReadOnlyList<string> libraries)
    {
        if (libraries.Count == 0) return NoSdkProject;
        string names = string.Join(", ", libraries.Select(Path.GetFileName));
        return $"This solution has no Window project that dotnet runic dev can run; {names} {(libraries.Count == 1 ? "is a library or test project" : "are library or test projects")}.";
    }

    /// <summary>The projects <c>dotnet runic dev</c>, F5's guard and Debug the Window act on: the Window projects.</summary>
    public static IReadOnlyList<string> DevTargets(IReadOnlyList<SdkProjectState> projects)
        => projects.Where(state => state.IsWindow).Select(state => state.Project).ToList();

    /// <summary>The libraries and test projects, which are no dev target.</summary>
    public static IReadOnlyList<string> Libraries(IReadOnlyList<SdkProjectState> projects)
        => projects.Where(state => !state.IsWindow).Select(state => state.Project).ToList();

    public static IReadOnlyList<TreeRow> ViewModels(IReadOnlyList<SdkProjectState> projects) => PerProject(projects, state =>
    {
        if (state.SourceMapError != null) return new[] { new TreeRow("The development source map could not be read: " + state.SourceMapError, RowIcon.Warning, state.Project) { Tooltip = state.SourceMapPath } };
        return state.SourceTree?.Select(node => Row(state.Project, node)).ToArray() ?? Array.Empty<TreeRow>();
    }, state => state.SourceTree != null ? $"{state.SourceTree.Count} ViewModel{(state.SourceTree.Count == 1 ? "" : "s")}" : "not built");

    public static IReadOnlyList<TreeRow> Doctor(IReadOnlyList<SdkProjectState> projects)
    {
        if (!projects.Any(state => state.Doctor != null || state.DoctorError != null || state.DoctorRunning)) return Array.Empty<TreeRow>();
        return PerProject(projects, state =>
        {
            if (state.DoctorRunning) return new[] { new TreeRow("Running dotnet runic doctor…", RowIcon.Running, state.Project) };
            if (state.DoctorError != null) return new[] { new TreeRow("doctor failed: " + state.DoctorError, RowIcon.Error, state.Project) };
            var report = state.Doctor;
            if (report == null) return new[] { new TreeRow("Not run yet", RowIcon.NotRun, state.Project) { Action = RowAction.RunDoctor, Tooltip = "Run dotnet runic doctor" } };
            var rows = new List<TreeRow> { new(report.Healthy ? "Healthy" : "Not healthy", report.Healthy ? RowIcon.Healthy : RowIcon.NotHealthy, state.Project) { Description = Core.Doctor.Summary(report) } };
            rows.AddRange(Core.Doctor.OrderedChecks(report).Select(check => CheckRow(state.Project, check)));
            return rows;
        }, state => state.Doctor != null ? Core.Doctor.Summary(state.Doctor) : null);
    }

    internal static IReadOnlyList<TreeRow> PerProject(IReadOnlyList<SdkProjectState> projects, Func<SdkProjectState, IReadOnlyList<TreeRow>> build, Func<SdkProjectState, string?> describe)
    {
        if (projects.Count == 1) return build(projects[0]);
        return projects.Select(state =>
        {
            var row = new TreeRow(Path.GetFileName(state.Project), RowIcon.Project, state.Project) { Description = describe(state), Tooltip = state.Project, Expanded = true };
            row.Children.AddRange(build(state));
            return row;
        }).ToList();
    }

    private static TreeRow Row(string project, SourceTreeNode node)
    {
        var row = new TreeRow(node.Label, node.Kind == SourceMapTree.ViewModelKind ? RowIcon.ViewModel : MemberIcon(node.Kind), project)
        {
            Description = node.Description, Tooltip = node.Tooltip, Location = node.Location,
            Action = node.Location != null ? RowAction.OpenLocation : RowAction.None,
        };
        row.Children.AddRange(node.Children.Select(child => Row(project, child)));
        return row;
    }

    private static RowIcon MemberIcon(string kind) => kind switch
    {
        "state" or "setter" => RowIcon.Property,
        "commandState" => RowIcon.Boolean,
        "command" => RowIcon.Method,
        "interaction" => RowIcon.Event,
        "request" => RowIcon.Interface,
        _ => RowIcon.Field,
    };

    private static TreeRow CheckRow(string project, DoctorCheck check)
    {
        var row = new TreeRow(check.Id, check.Status switch { DoctorStatus.Fail => RowIcon.Error, DoctorStatus.Warn => RowIcon.Warning, _ => RowIcon.Pass }, project)
        {
            Description = check.Message,
            Tooltip = check.Remediation != null ? $"{check.Message}\n\nFix: {check.Remediation}" : check.Message,
            Expanded = check.Status != DoctorStatus.Pass,
        };
        if (check.Remediation != null) row.Children.Add(new TreeRow(check.Remediation, RowIcon.Fix, project) { Tooltip = check.Remediation });
        return row;
    }

    /// <summary>
    /// The projects a Run Doctor without a target checks: the only one; with several, the one containing the active
    /// document; otherwise all of them (Visual Studio has no quick pick, so VS Code's project choice runs every project).
    /// </summary>
    public static IReadOnlyList<SdkProjectState> DoctorTargets(IReadOnlyList<SdkProjectState> projects, string? activeDocument)
    {
        if (projects.Count <= 1 || string.IsNullOrEmpty(activeDocument)) return projects;
        var containing = projects.Where(state => IsUnder(activeDocument!, Path.GetDirectoryName(state.Project) ?? "")).ToList();
        return containing.Count == 1 ? containing : projects;
    }

    private static bool IsUnder(string file, string directory)
    {
        if (directory.Length == 0) return false;
        string prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).StartsWith(Path.GetFullPath(prefix), StringComparison.OrdinalIgnoreCase);
    }
}
