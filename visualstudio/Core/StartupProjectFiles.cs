using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>A loaded project as Visual Studio's automation names it: <c>Project.UniqueName</c> and <c>Project.FullName</c>.</summary>
public sealed class LoadedProject
{
    public LoadedProject(string uniqueName, string fullName) { UniqueName = uniqueName; FullName = fullName; }
    public string UniqueName { get; }
    public string FullName { get; }
}

/// <summary>
/// The project files of <c>Solution.SolutionBuild.StartupProjects</c>, whose entries are the projects'
/// <c>UniqueName</c>s: relative to the solution's folder, or rooted.
/// </summary>
/// <remarks>
/// A project opened without a solution (<c>File &gt; Open &gt; Project</c> on the template's single <c>.csproj</c>, which
/// has no <c>.sln</c>) leaves <c>Solution.FullName</c> empty. .NET Framework's <c>Path.GetDirectoryName("")</c> throws
/// "The path is not of a legal form" there, which made the F5 guard fail and ask nothing (0.24.0 checklist P6), so the
/// solution's folder is only computed from a non-empty path, and a name is matched to the loaded project with that
/// <c>UniqueName</c> first.
/// </remarks>
public static class StartupProjectFiles
{
    public static IReadOnlyList<string> Resolve(IEnumerable<string> startupNames, string? solutionFile, IReadOnlyList<LoadedProject> projects)
    {
        string? directory = string.IsNullOrWhiteSpace(solutionFile) ? null : Path.GetDirectoryName(solutionFile);
        var files = new List<string>();
        foreach (string name in startupNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string? file = projects.FirstOrDefault(project => string.Equals(project.UniqueName, name, StringComparison.OrdinalIgnoreCase) && IsRooted(project.FullName))?.FullName;
            if (file == null && IsRooted(name)) file = name;
            if (file == null && !string.IsNullOrEmpty(directory)) file = Path.GetFullPath(Path.Combine(directory, name));
            // No solution folder and no matching UniqueName: the only loaded project of that file name.
            if (file == null)
            {
                var named = projects.Where(project => IsRooted(project.FullName) && string.Equals(Path.GetFileName(project.FullName), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase)).ToList();
                file = named.Count == 1 ? named[0].FullName : name;
            }
            files.Add(file);
        }
        return files;
    }

    private static bool IsRooted(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);
}
