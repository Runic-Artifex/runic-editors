using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// The Dev Session section's frontend dependencies (devtools-library-dx S9) as VS Code's dependency tree shows them: the
/// session file's <c>dependencies[]</c> (runic-sdk 0.24), the build's graph file, and sessions before 0.24 without them.
/// </summary>
public class FrontendDependenciesTests
{
    private static string Fixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "visualstudio", "Core.Tests", "Fixtures", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new InvalidOperationException(name + " not found");
    }

    private sealed class Alive : IDevSessionSource
    {
        public string? Read(string path) => null;
        public DevProcessInfo? FindProcess(int processId) => new(null);
    }

    private static DevSnapshot Snapshot(string fixture, string state = "ready")
        => DevSessions.Snapshot(Fixture(fixture).Replace("\"state\": \"ready\"", $"\"state\": \"{state}\""), new Alive())!;

    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "runic-graph-fixture"));

    /// <summary>A graph file as runic-frontend-graph.mjs writes it (rax.frontend-graph/1).</summary>
    private static string Graph(string schema = FrontendDependencies.GraphSchema) => Json(new Dictionary<string, object?>
    {
        ["schema"] = schema, ["workspaceRoot"] = Root, ["packageManager"] = "bun", ["lockFile"] = Path.Combine(Root, "bun.lock"), ["member"] = true,
        ["application"] = new Dictionary<string, object?> { ["directory"] = Path.Combine(Root, "App", "Frontend"), ["kind"] = "compiled" },
        ["packages"] = new object[]
        {
            new Dictionary<string, object?> { ["directory"] = Path.Combine(Root, "tokens"), ["package"] = "@acme/tokens", ["kind"] = "source", ["build"] = null, ["watch"] = null, ["exports"] = new[] { "./src/index.ts" } },
            new Dictionary<string, object?> { ["directory"] = Path.Combine(Root, "shared-ui"), ["package"] = "@acme/shared-ui", ["kind"] = "compiled", ["build"] = "bun run --bun build", ["watch"] = "bun run --bun watch", ["exports"] = new[] { "./dist/index.js", "./dist/generated/runic-clients.js" } },
        },
    });

    private static string Json(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        bool flag => flag ? "true" : "false",
        Dictionary<string, object?> map => "{" + string.Join(",", map.Select(pair => Json(pair.Key) + ":" + Json(pair.Value))) + "}",
        System.Collections.IEnumerable items => "[" + string.Join(",", items.Cast<object?>().Select(Json)) + "]",
        _ => value.ToString()!,
    };

    private static IEnumerable<string> Lines(IEnumerable<TreeRow> rows) => rows.Select(row => row.ToString());

    [Test]
    public void A_0_24_session_lists_its_dependencies_and_a_session_before_0_24_has_none()
    {
        var current = Snapshot("dev-session-dependencies.json").Session!;
        Check.Same(new[] { "@acme/shared-ui", "@acme/tokens" }, current.Dependencies!.Select(item => item.Label));
        var shared = current.Dependencies![0];
        Check.Equal("SharedUi", shared.OwnerName);
        Check.Equal("failed", shared.State);
        Check.Equal("rebuilt on change", shared.WatchLabel);
        Check.True(shared.NeedsBuild, "a failed build needs one");
        Check.Same(new[] { "/repo/shared-ui/dist/index.js" }, shared.Exports);
        Check.Equal(null, current.Dependencies[1].Owner);
        Check.Equal("watched by Vite", current.Dependencies[1].WatchLabel);
        // The schema stays /1; a session without the field still parses, with no dependencies.
        var old = Snapshot("dev-session-ready.json").Session!;
        Check.Equal(DevSessionState.Ready, old.State);
        Check.Equal(null, old.Dependencies);
        Check.Equal(null, FrontendDependencies.For(Snapshot("dev-session-ready.json"), _ => null));
    }

    [Test]
    public void The_graph_file_gives_built_missing_output_or_source_from_the_files_on_disk()
    {
        Check.Equal(null, FrontendDependencies.ParseGraph("{", _ => true));
        Check.Equal(null, FrontendDependencies.ParseGraph(Graph("rax.frontend-graph/2"), _ => true));
        var none = FrontendDependencies.ParseGraph(Graph(), _ => false)!;
        Check.Same(new[] { "@acme/tokens:source", "@acme/shared-ui:missing-output" }, none.Select(item => $"{item.Label}:{item.State}"));
        Check.Same(new[] { Path.Combine(Root, "shared-ui", "dist", "index.js"), Path.Combine(Root, "shared-ui", "dist", "generated", "runic-clients.js") }, none[1].Exports);
        Check.Equal("not built", none[1].StateLabel);
        var known = new[] { new DevDependency(Path.Combine(Root, "shared-ui")) { Owner = "/repo/SharedUi/SharedUi.csproj", OwnerName = "SharedUi" } };
        var built = FrontendDependencies.ParseGraph(Graph(), _ => true, known)!;
        Check.Equal("built", built[1].State);
        Check.Equal("SharedUi", built[1].OwnerName);
        Check.Equal("bun run --bun watch", built[1].WatchCommand);
    }

    [Test]
    public void The_list_is_the_running_sessions_else_the_graphs_else_a_stopped_sessions()
    {
        var graph = FrontendDependencies.ParseGraph(Graph(), _ => false)!;
        var running = FrontendDependencies.For(Snapshot("dev-session-dependencies.json"), _ => graph)!;
        Check.Equal(DependencySource.Session, running.Source);
        Check.Equal("failed", running.Dependencies[0].State);
        // dev runs but is older than 0.24: the graph.
        Check.Equal(DependencySource.Graph, FrontendDependencies.For(Snapshot("dev-session-ready.json"), _ => graph)!.Source);
        // dev stopped: the graph, given the session's entries for their owners.
        IReadOnlyList<DevDependency>? given = null;
        var stopped = FrontendDependencies.For(Snapshot("dev-session-dependencies.json", "stopped"), known => { given = known; return graph; })!;
        Check.Equal(DependencySource.Graph, stopped.Source);
        Check.Equal(2, given!.Count);
        // No graph: the stopped session's list; nothing at all: none.
        Check.Equal(DependencySource.LastSession, FrontendDependencies.For(Snapshot("dev-session-dependencies.json", "stopped"), _ => null)!.Source);
        Check.Equal(null, FrontendDependencies.For(DevSnapshot.None, _ => null));
    }

    [Test]
    public void The_graph_file_sits_in_obj_runic_and_in_obj_rax_first_for_RunicArtifex_Tool()
    {
        string obj = Path.Combine("/repo/App", "obj");
        Check.Same(new[] { Path.Combine(obj, "runic", "frontend-graph.json") }, FrontendDependencies.GraphPaths("/repo/App/App.csproj", null));
        Check.Same(new[] { Path.Combine(obj, "rax", "frontend-graph.json"), Path.Combine(obj, "runic", "frontend-graph.json") },
            FrontendDependencies.GraphPaths("/repo/App/App.csproj", RunicTools.Core));
    }

    [Test]
    public void The_Dev_Session_section_shows_a_Frontend_dependencies_row_with_one_row_per_package()
    {
        var snapshot = Snapshot("dev-session-dependencies.json");
        var rows = DevSessionRows.Details("/app/App.csproj", snapshot, FrontendDependencies.For(snapshot, _ => null));
        var dependencies = rows.Last();
        Check.Equal("Frontend dependencies — 2 packages, 1 not built", dependencies.ToString());
        Check.True(dependencies.Expanded, "expanded");
        Check.Same(new[] { "@acme/shared-ui — SharedUi · failed · rebuilt on change", "@acme/tokens — source · watched by Vite" }, Lines(dependencies.Children));
        var shared = dependencies.Children[0];
        Check.Equal(RowIcon.Error, shared.Icon);
        Check.Equal(RowAction.OpenFile, shared.Action);
        Check.Equal(Path.Combine("/repo/shared-ui", "package.json"), shared.Target);
        Check.Same(new[] { "shared-ui/src/about-panel.ts(42,31): error TS1109: Expression expected." }, Lines(shared.Children));
        Check.True(shared.Tooltip!.Contains("The frontend package of SharedUi"), shared.Tooltip);
        Check.Equal(RowIcon.Page, dependencies.Children[1].Icon);
        // Before 0.24 the section is as it was; a stopped session with a graph still lists the packages.
        Check.Equal(6, DevSessionRows.Details("/app/App.csproj", Snapshot("dev-session-ready.json")).Count);
        Check.Equal(null, DevSessionRows.DependencyRow("/app/App.csproj", new DependencyList(DependencySource.Graph, Array.Empty<DevDependency>())));
        var stopped = DevSessionRows.Details("/app/App.csproj", Snapshot("dev-session-ready.json", "stopped"), new DependencyList(DependencySource.Graph, FrontendDependencies.ParseGraph(Graph(), _ => false)!));
        Check.Same(new[] { "dotnet runic dev: stopped", "Frontend dependencies — 2 packages, 1 not built" }, Lines(stopped));
        Check.Equal("@acme/shared-ui — not built", stopped[1].Children[1].ToString());
        Check.Equal(RowIcon.Warning, stopped[1].Children[1].Icon);
    }
}
