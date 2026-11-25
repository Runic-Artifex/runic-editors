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

    /// <summary><c>dotnet rax migrate</c>: first <c>--dry-run --json</c> (the report only), then the run that writes.</summary>
    public static IReadOnlyList<string> MigrateArguments(bool dryRun)
        => dryRun ? new[] { Core.Verb, "migrate", "--dry-run", "--json" } : new[] { Core.Verb, "migrate", "--json" };

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

/// <summary>
/// The offer to move a solution to the Runic Artifex tool IDs (D079.13, D080.7; VS Code: <c>vscode/src/migration.ts</c>).
/// <c>rax migrate</c> ships with the rename in <c>RunicArtifex.Tool</c>; DevKit runs it only where a manifest with a
/// former ID already lists that tool, and otherwise links <see cref="RunicTools.MigrationGuide"/>.
/// </summary>
public sealed class MigrationOffer
{
    private MigrationOffer(IReadOnlyList<string> manifests, IReadOnlyList<string> formerIds, string? migrateDirectory)
    {
        Manifests = manifests; FormerIds = formerIds; MigrateDirectory = migrateDirectory;
    }

    public IReadOnlyList<string> Manifests { get; }
    public IReadOnlyList<string> FormerIds { get; }
    /// <summary>Where to run <c>dotnet rax migrate</c>, or null when only the guide can be offered.</summary>
    public string? MigrateDirectory { get; }

    public string Message
    {
        get
        {
            var names = FormerIds.Select(id => id == RunicTools.FormerCore.Id
                ? "dotnet-runic → RunicArtifex.Tool (rax)" : "dotnet-runic-translations → RunicArtifex.Translations.Tool (rax-translations)");
            return "This solution's tool manifest uses the former Runic tool IDs. Runic Artifex renames them: " + string.Join("; ", names) + ". "
                + (MigrateDirectory != null ? "rax migrate rewrites the project; DevKit shows its dry run first." : "rax migrate, which ships with the rename, rewrites the project; the migration guide has the steps.");
        }
    }

    /// <summary>The offer for a solution's tool manifests, or null when none lists a former ID.</summary>
    public static MigrationOffer? For(IEnumerable<(string Path, string Text)> manifests)
    {
        var found = manifests.Select(manifest => (manifest.Path, manifest.Text, Former: RunicTools.FormerIds(manifest.Text))).Where(manifest => manifest.Former.Count > 0).ToList();
        if (found.Count == 0) return null;
        var ids = new[] { RunicTools.FormerCore.Id, RunicTools.FormerTranslations.Id }.Where(id => found.Any(manifest => manifest.Former.Contains(id))).ToList();
        var runnable = found.FirstOrDefault(manifest => RunicTools.Find(manifest.Text)?.Tool == RunicTools.Core);
        return new MigrationOffer(found.Select(manifest => manifest.Path).ToList(), ids, runnable.Path != null ? RunicTools.ManifestDirectory(runnable.Path) : null);
    }

    /// <summary>The offer for the tool manifests in and below <paramref name="root"/> (as <see cref="RunicWorkspace.Detect"/> reads them) and above it.</summary>
    public static MigrationOffer? For(RunicWorkspace workspace, string root)
        => For(workspace.AllToolManifests.Select(path => (Path: path, Text: RunicTools.ReadManifest(path)))
            .Concat(RunicTools.ManifestsAbove(Path.GetDirectoryName(root)))
            .GroupBy(manifest => manifest.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()));

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
