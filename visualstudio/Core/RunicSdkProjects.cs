using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// Runic SDK projects and their <c>dotnet-runic</c> tool, as VS Code's <c>vscode/src/workspace.ts</c> finds them.
/// </summary>
public static class RunicSdkProjects
{
    private static readonly Regex WindowProject = new(@"<RunicApplicationFrontendWindowProject>\s*true\s*<", RegexOptions.IgnoreCase);
    private static readonly Regex SdkReference = new(@"<(?:PackageReference|ProjectReference)\b[^>]*\bInclude\s*=\s*""[^""]*\bRunic\.(?:Application|Desktop|Platform)\b[^""]*""", RegexOptions.IgnoreCase);
    private static readonly Regex NotWindow = new(@"<(?:RunicApplicationFrontendClientLibrary|IsTestProject)>\s*true\s*<", RegexOptions.IgnoreCase);
    private static readonly Regex TestReference = new(@"<(?:PackageReference|ProjectReference)\b[^>]*\bInclude\s*=\s*""[^""]*\b(?:Microsoft\.NET\.Test\.Sdk|TUnit|xunit(?:\.v3)?|NUnit|MSTest(?:\.Sdk)?|Runic\.Application\.Testing)\b[^""]*""", RegexOptions.IgnoreCase);
    private static readonly Regex OutputType = new(@"<OutputType(?:\s[^>]*)?>\s*([A-Za-z]+)\s*<", RegexOptions.IgnoreCase);
    private static readonly Regex WebOrWorkerSdk = new(@"<Project\b[^>]*\bSdk\s*=\s*""Microsoft\.NET\.Sdk\.(?:Web|Worker)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ToolVersionPattern = new(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$");

    /// <summary>
    /// True for a project file of the Runic SDK: a Window project (<c>RunicApplicationFrontendWindowProject</c>), or one
    /// that references a <c>Runic.Application.*</c>, <c>Runic.Desktop*</c> or <c>Runic.Platform.*</c> package or project. A
    /// project that references only Runic Translations packages is not one.
    /// </summary>
    public static bool IsSdkProject(string projectText) => WindowProject.IsMatch(projectText) || SdkReference.IsMatch(projectText);

    /// <summary>
    /// True for a Runic SDK project that <c>dotnet runic dev</c> can run, a Window project (VS Code:
    /// <c>isWindowProject</c>): one that sets <c>RunicApplicationFrontendWindowProject</c>, or an executable
    /// (<c>OutputType</c> <c>Exe</c> or <c>WinExe</c>, or the Web and Worker SDKs without an <c>OutputType</c>) that is
    /// neither a client library (<c>RunicApplicationFrontendClientLibrary</c>) nor a test project (<c>IsTestProject</c>,
    /// or a reference to a test framework or <c>Runic.Application.Testing</c>). <c>dev</c> stops on a library with
    /// RAPPDEV1005, so a library is no dev target; the ViewModels and Doctor sections still list it.
    /// </summary>
    public static bool IsWindowProject(string projectText)
    {
        if (!IsSdkProject(projectText)) return false;
        if (WindowProject.IsMatch(projectText)) return true;
        if (NotWindow.IsMatch(projectText) || TestReference.IsMatch(projectText)) return false;
        var outputs = OutputType.Matches(projectText).Cast<Match>().Select(match => match.Groups[1].Value).ToList();
        if (outputs.Count > 0) return outputs.Any(output => output.Equals("Exe", StringComparison.OrdinalIgnoreCase) || output.Equals("WinExe", StringComparison.OrdinalIgnoreCase));
        return WebOrWorkerSdk.IsMatch(projectText);
    }

    /// <summary>The Window projects (<see cref="IsWindowProject"/>) among Runic SDK project files; an unreadable file is none.</summary>
    public static IReadOnlyList<string> WindowProjects(IEnumerable<string> projectFiles)
        => projectFiles.Where(file =>
        {
            try { return IsWindowProject(File.ReadAllText(file)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }).ToList();

    /// <summary>The <c>.csproj</c> files among a solution's project files that are Runic SDK projects, sorted and unique.</summary>
    public static IReadOnlyList<string> Find(IEnumerable<string> projectFiles)
    {
        var result = new List<string>();
        foreach (string file in projectFiles.Where(file => !string.IsNullOrEmpty(file)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(file)) continue;
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            if (IsSdkProject(text)) result.Add(file);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "artifacts", "dist", "www" };

    /// <summary>
    /// The Runic SDK projects below a folder (at most <paramref name="depth"/> levels, skipping hidden, build and
    /// package directories), as VS Code's workspace detection finds them; for a folder opened without a solution.
    /// </summary>
    public static IReadOnlyList<string> Scan(string root, int depth = 4)
    {
        var files = new List<string>();
        void Visit(string directory, int level)
        {
            try { files.AddRange(Directory.GetFiles(directory, "*.csproj")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            if (level >= depth) return;
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (!name.StartsWith(".", StringComparison.Ordinal) && !Skipped.Contains(name)) Visit(child, level + 1);
            }
        }
        Visit(root, 0);
        return Find(files);
    }

    /// <summary>The core tool version (<c>RunicArtifex.Tool</c> or <c>dotnet-runic</c>) a tool manifest pins, or null.</summary>
    public static string? ToolVersion(string manifestText) => RunicTools.Find(manifestText)?.Version;

    /// <summary>
    /// The core tool version of the nearest tool manifest (<c>.config/dotnet-tools.json</c> or <c>dotnet-tools.json</c>)
    /// at or above the project's directory that lists the tool, if any.
    /// </summary>
    public static string? ProjectToolVersion(string project) => RunicTools.ProjectCoreTool(project)?.Version;

    internal static string HelpRef(string? toolVersion)
        => toolVersion != null && ToolVersionPattern.IsMatch(toolVersion) ? "v" + toolVersion : "main";
}

/// <summary>Links to runic-sdk's documentation for Runic diagnostics and doctor checks (VS Code: <c>vscode/src/diagnostics.ts</c>).</summary>
public static class RunicHelpLinks
{
    private static readonly Regex RunicCode = new(@"^(?:RUNICBRIDGE|RAPPCLI|RAPPDEV|RAPPPKG|RCLI|RUNICP)\d{3,4}$");

    /// <summary>Runic diagnostic codes from <c>dotnet runic</c>, the Bridge generator, the build targets and the Vite plugin (<c>RUNICP</c>).</summary>
    public static bool IsRunicCode(string code) => RunicCode.IsMatch(code);

    /// <summary>
    /// The code's entry in docs/diagnostics.md at the release tag of the project's <c>dotnet-runic</c> tool
    /// (<c>v&lt;version&gt;</c>), or on <c>main</c> when the version is unknown.
    /// </summary>
    public static string Diagnostic(string code, string? toolVersion = null)
        => $"https://github.com/Runic-Artifex/runic-sdk/blob/{RunicSdkProjects.HelpRef(toolVersion)}/docs/diagnostics.md#{code.ToLowerInvariant()}";

    /// <summary>The dotnet-runic README section that lists the doctor checks.</summary>
    public static string Doctor(string? toolVersion = null)
        => $"https://github.com/Runic-Artifex/runic-sdk/blob/{RunicSdkProjects.HelpRef(toolVersion)}/tools/dotnet-runic/README.md#doctor-json-output";
}
