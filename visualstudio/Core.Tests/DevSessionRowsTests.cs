using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>The Dev Session section, toolbar state and status text as VS Code's Dev Session view and status bar item show them.</summary>
public class DevSessionRowsTests
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

    private static IEnumerable<string> Lines(IEnumerable<TreeRow> rows) => rows.Select(row => row.ToString());

    [Test]
    public void A_ready_session_with_the_Window_lists_server_window_pages_endpoints_and_generated_clients()
    {
        var rows = DevSessionRows.Details("/app/App.csproj", Snapshot("dev-session-ready.json"));
        Check.Same(new[]
        {
            "dotnet runic dev: ready",
            "Development server: http://127.0.0.1:5173/ — vite",
            "Window: process 41377 — 1 start",
            "Page: index.html — http://127.0.0.1:5173/index.html",
            "Debug endpoint: webview2 — cdp http://127.0.0.1:9222/",
            "Generated clients — Frontend/src/generated",
        }, Lines(rows));
        Check.Equal(RowAction.OpenFile, rows[0].Action);
        Check.Equal(DevSessions.PathFor("/app/App.csproj"), rows[0].Target);
        Check.Equal(RowIcon.DevReady, rows[0].Icon);
        Check.Equal(RowAction.OpenUrl, rows[1].Action);
        Check.Equal("http://127.0.0.1:5173/", rows[1].Target);
        Check.Equal(RowAction.OpenFolder, rows[5].Action);
    }

    [Test]
    public void A_no_window_session_shows_the_IDE_owner_and_the_watcher_and_a_stopped_one_only_its_state()
    {
        Check.Same(new[] { "dotnet runic dev: ready (Window in the IDE)", "Frontend: watcher — /app/Frontend/dist", "Window: started by the IDE — F5" },
            Lines(DevSessionRows.Details("/app/App.csproj", Snapshot("dev-session-no-window.json"))));
        var stopped = DevSessionRows.Details("/app/App.csproj", Snapshot("dev-session-ready.json", "stopped"));
        Check.Same(new[] { "dotnet runic dev: stopped" }, Lines(stopped));
        var none = DevSessionRows.Details("/app/App.csproj", DevSnapshot.None);
        Check.Same(new[] { "dotnet runic dev: not started" }, Lines(none));
        Check.Equal(RowAction.None, none[0].Action);
        Check.Equal(RowIcon.DevStopped, none[0].Icon);
    }

    [Test]
    public void Several_projects_get_one_row_each_with_their_state()
    {
        var projects = new[] { new SdkProjectState("/app/App.csproj"), new SdkProjectState("/tool/Tool.csproj") };
        var ready = Snapshot("dev-session-ready.json");
        var rows = DevSessionRows.Build(projects, project => project == "/app/App.csproj" ? ready : DevSnapshot.None);
        Check.Same(new[] { "App.csproj — ready", "Tool.csproj — not started" }, Lines(rows));
        Check.Equal(6, rows[0].Children.Count);
        Check.Equal(1, DevSessionRows.Build(projects.Take(1).ToList(), _ => DevSnapshot.None).Count);
    }

    [Test]
    public void The_status_text_and_the_toolbar_follow_the_state()
    {
        var ready = Snapshot("dev-session-ready.json");
        Check.Equal("Runic dev: not started", DevSessionRows.StatusText(new[] { DevSnapshot.None }));
        Check.Equal("Runic dev: ready", DevSessionRows.StatusText(new[] { ready }));
        Check.Equal("Runic dev: 1 of 2 running", DevSessionRows.StatusText(new[] { ready, DevSnapshot.None }));

        var idle = new DevCommandState(new[] { (DevSnapshot.None, false) });
        Check.True(idle.CanStart && !idle.CanStop && !idle.CanOpenServer && !idle.CanOpenSessionFile, "nothing runs");
        var running = new DevCommandState(new[] { (ready, true) });
        Check.True(!running.CanStart && running.CanStop && running.CanOpenServer && running.CanOpenSessionFile, "ready");
        var noWindow = new DevCommandState(new[] { (Snapshot("dev-session-no-window.json"), false) });
        Check.True(noWindow.CanStop && !noWindow.CanOpenServer, "--no-window without a development server");
        Check.True(!new DevCommandState(Array.Empty<(DevSnapshot, bool)>()).CanStart, "no project");
    }
}
