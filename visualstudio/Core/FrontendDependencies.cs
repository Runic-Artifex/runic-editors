using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// One frontend package the page depends on: an entry of the session file's <c>dependencies[]</c> (runic-sdk 0.24,
/// "Library packages under dev"), or a package of the build's graph file (VS Code: <c>vscode/src/dependencies.ts</c>
/// <c>DevDependency</c>). The values are open strings, so a state a later <c>dev</c> adds still shows.
/// </summary>
public sealed class DevDependency
{
    public DevDependency(string directory) { Directory = directory; }

    public string? Package { get; set; }
    public string Directory { get; }
    /// <summary><c>compiled</c> (it has a build script) or <c>source</c>.</summary>
    public string Kind { get; set; } = "compiled";
    /// <summary>The .NET project that owns it, or null for a workspace package.</summary>
    public string? Owner { get; set; }
    public string? OwnerName { get; set; }
    public string? Specifier { get; set; }
    public string? BuildCommand { get; set; }
    public string? WatchCommand { get; set; }
    public IReadOnlyList<string> Exports { get; set; } = Array.Empty<string>();
    /// <summary><c>built</c>, <c>building</c>, <c>failed</c>, <c>missing-output</c>, <c>source</c> or <c>not-built</c>.</summary>
    public string State { get; set; } = "unknown";
    /// <summary><c>script</c>, <c>rebuild</c>, <c>vite</c> or <c>none</c>.</summary>
    public string Watch { get; set; } = "none";
    /// <summary>The last failure while <see cref="State"/> is <c>failed</c>.</summary>
    public string? Message { get; set; }

    /// <summary>The row label: its package.json name, else its folder.</summary>
    public string Label => Package ?? Path.GetFileName(Directory.TrimEnd('/', '\\'));

    /// <summary>The state as the row says it (VS Code: <c>dependencyStateLabel</c>).</summary>
    public string StateLabel => State switch
    {
        "missing-output" => "not built",
        "not-built" => "build off",
        _ => State,
    };

    /// <summary>How <c>dev</c> keeps it current, or null (VS Code: <c>dependencyWatchLabel</c>).</summary>
    public string? WatchLabel => Watch switch
    {
        "script" => "watched by its watch script",
        "rebuild" => "rebuilt on change",
        "vite" => "watched by Vite",
        _ => null,
    };

    /// <summary>A failed build, or exports that are missing: the page cannot use its output yet.</summary>
    public bool NeedsBuild => State is "failed" or "missing-output";

    internal static DevDependency? Parse(Dictionary<string, object?>? entry)
    {
        if (Json.String(entry, "directory") is not { } directory) return null;
        return new DevDependency(directory)
        {
            Package = Json.String(entry, "package"), Kind = Json.String(entry, "kind") ?? "compiled", Owner = Json.String(entry, "owner"),
            OwnerName = Json.String(entry, "ownerName"), Specifier = Json.String(entry, "specifier"), BuildCommand = Json.String(entry, "buildCommand"),
            WatchCommand = Json.String(entry, "watchCommand"), Exports = Strings(Json.Get(entry, "exports")), State = Json.String(entry, "state") ?? "unknown",
            Watch = Json.String(entry, "watch") ?? "none", Message = Json.String(entry, "message"),
        };
    }

    internal static IReadOnlyList<string> Strings(object? value) => (Json.Array(value) ?? new List<object?>()).OfType<string>().ToList();
}

/// <summary>Where a dependency list came from: the running <c>dev</c>, the build's graph file, or the last session.</summary>
public enum DependencySource { Session, Graph, LastSession }

public sealed class DependencyList
{
    public DependencyList(DependencySource source, IReadOnlyList<DevDependency> dependencies) { Source = source; Dependencies = dependencies; }

    public DependencySource Source { get; }
    public IReadOnlyList<DevDependency> Dependencies { get; }
}

/// <summary>
/// The frontend packages a Window project's page depends on (runic-sdk devtools-library-dx §4.2, S9): the running
/// session's <c>dependencies[]</c>, else the build's <c>obj/runic/frontend-graph.json</c> (<c>rax.frontend-graph/1</c>,
/// Views targets 0.24), else a stopped session's own list. A session file before 0.24 has no <c>dependencies</c>, and a
/// project whose targets are older writes no graph; then there are none. Visual Studio's build and F5 build the
/// packages through MSBuild, so the extension only shows them.
/// </summary>
public static class FrontendDependencies
{
    public const string GraphSchema = "rax.frontend-graph/1";

    /// <summary>
    /// The graph files a project's build may have written, in the order they are read: <c>obj/rax/</c> first for a
    /// project whose tool is <c>RunicArtifex.Tool</c> (as its session file), then <c>obj/runic/</c>.
    /// </summary>
    public static IReadOnlyList<string> GraphPaths(string project, RunicToolId? tool)
    {
        string obj = Path.Combine(Path.GetDirectoryName(project) ?? "", "obj");
        string runic = Path.Combine(obj, "runic", "frontend-graph.json");
        return tool is { Former: false } ? new[] { Path.Combine(obj, "rax", "frontend-graph.json"), runic } : new[] { runic };
    }

    /// <summary>
    /// The packages of a graph file's text, with the state the files on disk give: a source package is <c>source</c>, a
    /// compiled one <c>built</c> when every file it exports exists, else <c>missing-output</c>. Owners come from
    /// <paramref name="known"/> (the last session's entries) by directory. Null for another schema or a file that is not
    /// one.
    /// </summary>
    public static IReadOnlyList<DevDependency>? ParseGraph(string text, Func<string, bool> exists, IReadOnlyList<DevDependency>? known = null)
    {
        Dictionary<string, object?>? graph;
        try { graph = Json.Object(Json.Parse(text)); }
        catch (FormatException) { return null; }
        if (graph == null || Json.String(graph, "schema") != GraphSchema || Json.String(graph, "workspaceRoot") == null) return null;
        var result = new List<DevDependency>();
        foreach (var entry in (Json.Array(Json.Get(graph, "packages")) ?? new List<object?>()).Select(Json.Object))
        {
            if (Json.String(entry, "directory") is not { } directory) continue;
            var exports = DevDependency.Strings(Json.Get(entry, "exports")).Select(target => Path.GetFullPath(Path.Combine(directory, target))).ToList();
            string kind = Json.String(entry, "kind") ?? "source";
            var previous = known?.FirstOrDefault(item => SamePath(item.Directory, directory));
            result.Add(new DevDependency(directory)
            {
                Package = Json.String(entry, "package"), Kind = kind, Owner = previous?.Owner, OwnerName = previous?.OwnerName, Specifier = previous?.Specifier,
                BuildCommand = Json.String(entry, "build"), WatchCommand = Json.String(entry, "watch"), Exports = exports,
                State = kind != "compiled" ? "source" : exports.All(exists) ? "built" : "missing-output", Watch = "none",
            });
        }
        return result;
    }

    /// <summary>The list the Dev Session section shows (VS Code: <c>dependencyList</c>).</summary>
    public static DependencyList? For(DevSnapshot snapshot, Func<IReadOnlyList<DevDependency>?, IReadOnlyList<DevDependency>?> readGraph)
    {
        var fromSession = snapshot.Session?.Dependencies;
        if (snapshot.IsActive && fromSession != null) return new DependencyList(DependencySource.Session, fromSession);
        if (readGraph(fromSession) is { } graph) return new DependencyList(DependencySource.Graph, graph);
        if (fromSession is { Count: > 0 }) return new DependencyList(DependencySource.LastSession, fromSession);
        return null;
    }

    /// <summary>Reads the project's graph file from disk.</summary>
    public static DependencyList? Read(string project, DevSnapshot snapshot) => For(snapshot, known =>
    {
        foreach (string path in GraphPaths(project, RunicTools.ProjectCoreTool(project)?.Tool))
        {
            string text;
            try { text = File.ReadAllText(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (ParseGraph(text, File.Exists, known) is { } graph) return graph;
        }
        return null;
    });

    private static bool SamePath(string a, string b)
    {
        static string Normal(string path) => Path.GetFullPath(path).TrimEnd('/', '\\');
        return string.Equals(Normal(a), Normal(b), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
