using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// A Runic tool as a tool manifest names it (VS Code: <c>vscode/src/workspace.ts</c>). The rename (D079, D080.7) gives
/// the core tool the ID <c>RunicArtifex.Tool</c> and the command <c>rax</c> (<c>dotnet rax</c> from a manifest) in place
/// of <c>dotnet-runic</c> (<c>dotnet runic</c>), and the Translations tool <c>RunicArtifex.Translations.Tool</c> /
/// <c>rax-translations</c> in place of <c>dotnet-runic-translations</c> / <c>runic-translations</c>.
/// </summary>
public sealed class RunicToolId
{
    internal RunicToolId(string id, string command, string verb, bool former) { Id = id; Command = command; Verb = verb; Former = former; }

    /// <summary>The NuGet package ID (the manifest's key; NuGet IDs are case-insensitive).</summary>
    public string Id { get; }
    /// <summary>The tool's command.</summary>
    public string Command { get; }
    /// <summary>What follows <c>dotnet</c> to run it from a manifest: <c>dotnet runic …</c>, <c>dotnet rax …</c>.</summary>
    public string Verb { get; }
    /// <summary>True for the IDs before the rename, which <c>rax migrate</c> rewrites.</summary>
    public bool Former { get; }
}

/// <summary>A Runic tool a manifest lists, its pinned version, and the manifest.</summary>
public sealed class ManifestTool
{
    internal ManifestTool(RunicToolId tool, string? version, string? manifest) { Tool = tool; Version = version; Manifest = manifest; }
    public RunicToolId Tool { get; }
    public string? Version { get; }
    public string? Manifest { get; }
}

/// <summary>
/// Both families of tool IDs and both tool manifest locations. DevKit decides by what the manifest contains; the new ID
/// wins when a manifest lists both (mid-migration).
/// </summary>
public static class RunicTools
{
    public static readonly RunicToolId Core = new("RunicArtifex.Tool", "rax", "rax", former: false);
    public static readonly RunicToolId FormerCore = new("dotnet-runic", "dotnet-runic", "runic", former: true);
    public static readonly RunicToolId Translations = new("RunicArtifex.Translations.Tool", "rax-translations", "rax-translations", former: false);
    public static readonly RunicToolId FormerTranslations = new("dotnet-runic-translations", "runic-translations", "runic-translations", former: true);

    /// <summary>Newest first: the order in which a manifest's tools are looked up.</summary>
    public static readonly IReadOnlyList<RunicToolId> CoreTools = new[] { Core, FormerCore };
    public static readonly IReadOnlyList<RunicToolId> TranslationsTools = new[] { Translations, FormerTranslations };

    /// <summary>
    /// The tool manifests of one directory in the order .NET reads them: <c>.config/dotnet-tools.json</c>, then
    /// <c>dotnet-tools.json</c> at the root (which <c>dotnet new tool-manifest</c> writes since .NET 10).
    /// </summary>
    public static readonly IReadOnlyList<string> ManifestNames = new[] { Path.Combine(".config", "dotnet-tools.json"), "dotnet-tools.json" };

    /// <summary>The migration guide the offer links until <c>rax migrate</c> runs in the workspace.</summary>
    public const string MigrationGuide = "https://github.com/Runic-Artifex/runic-editors/blob/main/visualstudio/README.md#moving-to-the-runic-artifex-tool-ids";

    /// <summary>The directory a tool manifest belongs to: the parent of <c>.config</c> for <c>.config/dotnet-tools.json</c>.</summary>
    public static string ManifestDirectory(string manifest)
    {
        string directory = Path.GetDirectoryName(manifest) ?? "";
        return string.Equals(Path.GetFileName(directory), ".config", StringComparison.Ordinal) ? Path.GetDirectoryName(directory) ?? directory : directory;
    }

    /// <summary>The existing tool manifests of one directory, in <see cref="ManifestNames"/> order.</summary>
    public static IEnumerable<string> ManifestsIn(string directory)
        => ManifestNames.Select(name => Path.Combine(directory, name)).Where(File.Exists);

    /// <summary>The tool manifests from <paramref name="directory"/> up to the first root manifest or the drive's root, nearest first, with their text.</summary>
    public static IEnumerable<(string Path, string Text)> ManifestsAbove(string? directory)
    {
        for (; !string.IsNullOrEmpty(directory); directory = System.IO.Path.GetDirectoryName(directory))
        {
            bool root = false;
            foreach (string manifest in ManifestsIn(directory!))
            {
                string text = ReadManifest(manifest);
                yield return (manifest, text);
                root |= IsRoot(text);
            }
            if (root) yield break;
        }
    }

    /// <summary>The first of <paramref name="tools"/> (default: the core tools) that a manifest's text lists, by package ID in any case.</summary>
    public static ManifestTool? Find(string manifestText, IReadOnlyList<RunicToolId>? tools = null)
    {
        var entries = Tools(manifestText);
        if (entries == null) return null;
        foreach (var tool in tools ?? CoreTools)
        {
            var key = entries.Keys.FirstOrDefault(name => string.Equals(name, tool.Id, StringComparison.OrdinalIgnoreCase));
            if (key != null) return new ManifestTool(tool, Json.String(Json.Object(entries[key]), "version"), null);
        }
        return null;
    }

    /// <summary>The core tool of the nearest tool manifest at or above the project's directory that lists one, or null.</summary>
    public static ManifestTool? ProjectCoreTool(string project)
    {
        foreach (var (path, text) in ManifestsAbove(Path.GetDirectoryName(project)))
            if (Find(text) is { } found) return new ManifestTool(found.Tool, found.Version, path);
        return null;
    }

    /// <summary>The project's core tool verb: <c>rax</c> for <c>RunicArtifex.Tool</c>, else <c>runic</c>.</summary>
    public static string VerbFor(string project) => (ProjectCoreTool(project)?.Tool ?? FormerCore).Verb;

    /// <summary>The Translations command a manifest's text declares in a tool's <c>commands</c>: <c>rax-translations</c> before <c>runic-translations</c>.</summary>
    public static string? TranslationsCommand(string manifestText)
    {
        var commands = (Tools(manifestText)?.Values ?? Enumerable.Empty<object?>())
            .SelectMany(tool => Json.Array(Json.Get(Json.Object(tool), "commands")) ?? new List<object?>()).OfType<string>().ToList();
        return TranslationsTools.Select(tool => tool.Command).FirstOrDefault(commands.Contains);
    }

    /// <summary>The former Runic tool IDs (<c>dotnet-runic</c>, <c>dotnet-runic-translations</c>) a manifest's text lists.</summary>
    public static IReadOnlyList<string> FormerIds(string manifestText)
    {
        var keys = Tools(manifestText)?.Keys.ToList() ?? new List<string>();
        return new[] { FormerCore, FormerTranslations }.Where(tool => keys.Any(key => string.Equals(key, tool.Id, StringComparison.OrdinalIgnoreCase))).Select(tool => tool.Id).ToList();
    }

    public static bool IsRoot(string manifestText)
    {
        try { return Json.Boolean(Json.Object(Json.Parse(manifestText)), "isRoot") == true; } catch (FormatException) { return false; }
    }

    /// <summary>
    /// <c>rax migrate</c>: first <c>--dry-run --json</c> (the report only), then the run that writes; <c>dotnet rax …</c>
    /// (<paramref name="globalRax"/> false) or a global <c>rax …</c>.
    /// </summary>
    public static IReadOnlyList<string> MigrateArguments(bool dryRun, bool globalRax = false)
        => (globalRax ? new string[0] : new[] { Core.Verb }).Concat(new[] { "migrate" }).Concat(dryRun ? new[] { "--dry-run", "--json" } : new[] { "--json" }).ToList();

    /// <summary><c>rax --version</c> through <c>dotnet</c> or a global <c>rax</c>: the probe for <see cref="MigrationOffer.For(IEnumerable{ValueTuple{string, string}}, MigrateRunner?)"/>.</summary>
    public static IReadOnlyList<string> RaxVersionArguments(bool globalRax) => globalRax ? new[] { "--version" } : new[] { Core.Verb, "--version" };

    private static Dictionary<string, object?>? Tools(string manifestText)
    {
        try { return Json.Object(Json.Get(Json.Object(Json.Parse(manifestText)), "tools")); } catch (FormatException) { return null; }
    }

    /// <summary>A manifest's text; empty when it cannot be read.</summary>
    public static string ReadManifest(string file)
    {
        try { return File.ReadAllText(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }
}

/// <summary>How <c>rax migrate</c> runs: in <see cref="Directory"/>, as <c>dotnet rax …</c> or a global <c>rax …</c>.</summary>
public sealed class MigrateRunner
{
    public MigrateRunner(string directory, bool globalRax) { Directory = directory; GlobalRax = globalRax; }
    public string Directory { get; }
    public bool GlobalRax { get; }
}

/// <summary>
/// The offer to move a solution to the Runic Artifex tool IDs (D079.13, D080.7; VS Code: <c>vscode/src/migration.ts</c>).
/// <c>rax migrate</c> ships with the rename in <c>RunicArtifex.Tool</c>, so the offer exists only where that tool is
/// available: a manifest with a former ID also lists it, or <c>dotnet rax --version</c> / <c>rax --version</c> runs.
/// Before the rename ships nothing shows.
/// </summary>
public sealed class MigrationOffer
{
    private MigrationOffer(IReadOnlyList<string> manifests, IReadOnlyList<string> formerIds, MigrateRunner runner)
    {
        Manifests = manifests; FormerIds = formerIds; Runner = runner;
    }

    public IReadOnlyList<string> Manifests { get; }
    public IReadOnlyList<string> FormerIds { get; }
    /// <summary>Where and how <c>rax migrate</c> runs.</summary>
    public MigrateRunner Runner { get; }

    public string Message
    {
        get
        {
            var names = FormerIds.Select(id => id == RunicTools.FormerCore.Id
                ? "dotnet-runic → RunicArtifex.Tool (rax)" : "dotnet-runic-translations → RunicArtifex.Translations.Tool (rax-translations)");
            return "This solution's tool manifest uses the former Runic tool IDs. Runic Artifex renames them: " + string.Join("; ", names)
                + ". rax migrate rewrites the project; DevKit shows its dry run first.";
        }
    }

    private static List<(string Path, string Text, IReadOnlyList<string> Former)> Former(IEnumerable<(string Path, string Text)> manifests)
        => manifests.Select(manifest => (manifest.Path, manifest.Text, Former: RunicTools.FormerIds(manifest.Text))).Where(manifest => manifest.Former.Count > 0).ToList();

    /// <summary>
    /// Where to look for a <c>rax</c> the manifests don't list, or null: the folder of the first manifest with a former
    /// ID, when no manifest with a former ID also lists <c>RunicArtifex.Tool</c>. The caller runs <c>dotnet rax
    /// --version</c>, then <c>rax --version</c> there, and passes what resolved to <see cref="For(IEnumerable{ValueTuple{string, string}}, MigrateRunner?)"/>.
    /// </summary>
    public static string? ProbeDirectory(IEnumerable<(string Path, string Text)> manifests)
    {
        var found = Former(manifests);
        if (found.Count == 0 || found.Any(manifest => RunicTools.Find(manifest.Text)?.Tool == RunicTools.Core)) return null;
        return RunicTools.ManifestDirectory(found[0].Path);
    }

    /// <summary>
    /// The offer for a solution's tool manifests, or null: when none lists a former ID, or when <c>RunicArtifex.Tool</c>
    /// is available neither in a manifest with a former ID nor as <paramref name="resolved"/>.
    /// </summary>
    public static MigrationOffer? For(IEnumerable<(string Path, string Text)> manifests, MigrateRunner? resolved = null)
    {
        var found = Former(manifests);
        if (found.Count == 0) return null;
        var listed = found.FirstOrDefault(manifest => RunicTools.Find(manifest.Text)?.Tool == RunicTools.Core);
        var runner = listed.Path != null ? new MigrateRunner(RunicTools.ManifestDirectory(listed.Path), globalRax: false) : resolved;
        if (runner == null) return null;
        var ids = new[] { RunicTools.FormerCore.Id, RunicTools.FormerTranslations.Id }.Where(id => found.Any(manifest => manifest.Former.Contains(id))).ToList();
        return new MigrationOffer(found.Select(manifest => manifest.Path).ToList(), ids, runner);
    }

    /// <summary>The tool manifests in and below <paramref name="root"/> (as <see cref="RunicWorkspace.Detect"/> reads them) and above it.</summary>
    public static IReadOnlyList<(string Path, string Text)> SolutionManifests(RunicWorkspace workspace, string root)
        => workspace.AllToolManifests.Select(path => (Path: path, Text: RunicTools.ReadManifest(path)))
            .Concat(RunicTools.ManifestsAbove(Path.GetDirectoryName(root)))
            .GroupBy(manifest => manifest.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();

    /// <summary>
    /// A one-line summary of <c>rax migrate --json</c>'s report (D079.10: rewritten, moved, reported but not changed):
    /// its counts when the report has those lists, else its last line.
    /// </summary>
    public static string Summary(string output)
    {
        Dictionary<string, object?>? body = null;
        try { var report = Json.Object(Json.Parse(output)); body = Json.Object(Json.Get(report, "payload")) ?? report; } catch (FormatException) { }
        var counts = new[] { "rewritten", "moved", "reported" }.Where(key => Json.Array(Json.Get(body, key)) != null).Select(key => $"{Json.Array(Json.Get(body, key))!.Count} {key}").ToList();
        if (counts.Count > 0) return string.Join(", ", counts);
        string last = output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        return last.Length > 0 ? last : "no report";
    }
}
