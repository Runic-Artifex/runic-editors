using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>What <c>dotnet runic dev</c> says about itself in the session file.</summary>
public enum DevSessionState { Starting, Ready, Stopped }

/// <summary>
/// What the extension shows for one project, as VS Code's <c>DevState</c>: <see cref="None"/> without a session file;
/// <see cref="Stopped"/> when the file says so or when its <c>dotnet runic dev</c> process no longer runs.
/// </summary>
public enum DevState { None, Starting, Ready, Stopped }

public sealed class DevFrontend
{
    internal DevFrontend(Dictionary<string, object?> value)
    {
        DevelopmentServer = Json.String(value, "developmentServer");
        Url = Json.String(value, "url");
        Port = Json.Number(value, "port") is { } port ? (int)port : null;
        StateUrl = Json.String(value, "stateUrl");
        Directory = Json.String(value, "directory");
        OutputDirectory = Json.String(value, "outputDirectory");
        GeneratedDirectory = Json.String(value, "generatedDirectory");
    }

    /// <summary><c>vite</c>, <c>angular</c>, or null when a watcher or the build writes the output.</summary>
    public string? DevelopmentServer { get; }
    /// <summary>The development server's origin, null without one.</summary>
    public string? Url { get; }
    public int? Port { get; }
    public string? StateUrl { get; }
    public string? Directory { get; }
    public string? OutputDirectory { get; }
    /// <summary>The generated TypeScript clients, or null.</summary>
    public string? GeneratedDirectory { get; }
}

public sealed class DevPage
{
    internal DevPage(string document, string? file, string? url) { Document = document; File = file; Url = url; }

    public string Document { get; }
    public string? File { get; }
    public string? Url { get; }
}

public sealed class DevWindow
{
    internal DevWindow(Dictionary<string, object?> value)
    {
        Owner = Json.String(value, "owner");
        Program = Json.String(value, "program");
        ProcessId = Json.Number(value, "processId") is { } id ? (int)id : null;
        Starts = Json.Number(value, "starts") is { } starts ? (int)starts : 0;
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (Json.Object(Json.Get(value, "environment")) is { } variables)
            foreach (var pair in variables)
                if (pair.Value is string text) environment[pair.Key] = text;
        Environment = environment;
    }

    /// <summary><c>dev</c>, or <c>ide</c> with <c>--no-window</c>: the IDE starts the Window.</summary>
    public string? Owner { get; }
    public string? Program { get; }
    /// <summary>The running Window process, or null.</summary>
    public int? ProcessId { get; }
    public int Starts { get; }
    /// <summary>What a Window that the IDE starts needs, as <c>dev</c> would set it.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
    public bool OwnedByIde => Owner == "ide";
}

public sealed class DevDebugEndpoint
{
    internal DevDebugEndpoint(string engine, string protocol, string url, string? reason = null) { Engine = engine; Protocol = protocol; Url = url; Reason = reason; }

    /// <summary><c>webview2</c>, <c>chromium</c>, <c>webkitgtk</c>…</summary>
    public string Engine { get; }
    /// <summary><c>cdp</c>, <c>webkit-remote-inspector</c>…</summary>
    public string Protocol { get; }
    public string Url { get; }
    /// <summary>Why an entry of <c>unavailableDebugEndpoints</c> is not open (<c>elevated</c>, <c>port-taken</c>…); null in <c>debugEndpoints</c>.</summary>
    public string? Reason { get; }
}

/// <summary>
/// <c>obj/runic/dev-session.json</c>, which <c>dotnet runic dev</c> writes (runic-sdk <c>tools/dotnet-runic/README.md</c>,
/// "Session file"; VS Code: <c>vscode/src/dev-session.ts</c> <c>DevSession</c>). Later versions of the schema only add
/// properties, which the reader ignores.
/// </summary>
public sealed class DevSessionFile
{
    private DevSessionFile(string text) { Text = text; }

    /// <summary>The file's text, which identifies the snapshot (each write of <c>dev</c> replaces the whole file).</summary>
    public string Text { get; }
    public DevSessionState State { get; private set; }
    /// <summary><c>dotnet runic dev</c> itself.</summary>
    public int ProcessId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    /// <summary><c>dev</c>'s process start time, for the liveness check; since 0.20.</summary>
    public DateTimeOffset? ProcessStartedAt { get; private set; }
    public string Project { get; private set; } = "";
    public string? Configuration { get; private set; }
    public DevFrontend? Frontend { get; private set; }
    public string? WebRoot { get; private set; }
    public string? SourceMap { get; private set; }
    public IReadOnlyList<DevPage> Pages { get; private set; } = Array.Empty<DevPage>();
    public DevWindow? Window { get; private set; }
    public IReadOnlyList<DevDebugEndpoint> DebugEndpoints { get; private set; } = Array.Empty<DevDebugEndpoint>();
    /// <summary>Endpoints the environment asks for that the engine does not open, with the reason; since 0.20.</summary>
    public IReadOnlyList<DevDebugEndpoint> UnavailableDebugEndpoints { get; private set; } = Array.Empty<DevDebugEndpoint>();

    /// <summary>The development server's address, else the first page's: what Open the Development Server opens.</summary>
    public string? ServerUrl => Frontend?.Url ?? Pages.FirstOrDefault(page => page.Url != null)?.Url;

    internal static DevSessionFile? Parse(string text)
    {
        Dictionary<string, object?>? value;
        try { value = Json.Object(Json.Parse(text)); }
        catch (FormatException) { return null; }
        if (value == null || Json.String(value, "schema") != DevSessions.Schema) return null;
        DevSessionState? state = Json.String(value, "state") switch
        {
            "starting" => DevSessionState.Starting,
            "ready" => DevSessionState.Ready,
            "stopped" => DevSessionState.Stopped,
            _ => null,
        };
        if (state == null || Json.Number(value, "processId") is not { } processId || Json.String(value, "project") is not { } project) return null;
        return new DevSessionFile(text)
        {
            State = state.Value,
            ProcessId = (int)processId,
            StartedAt = Date(Json.String(value, "startedAt")),
            ProcessStartedAt = Date(Json.String(value, "processStartedAt")),
            Project = project,
            Configuration = Json.String(value, "configuration"),
            Frontend = Json.Object(Json.Get(value, "frontend")) is { } frontend ? new DevFrontend(frontend) : null,
            WebRoot = Json.String(value, "webRoot"),
            SourceMap = Json.String(value, "sourceMap"),
            Pages = (Json.Array(Json.Get(value, "pages")) ?? new List<object?>()).Select(Json.Object)
                .Where(page => Json.String(page, "document") != null)
                .Select(page => new DevPage(Json.String(page, "document")!, Json.String(page, "file"), Json.String(page, "url"))).ToList(),
            Window = Json.Object(Json.Get(value, "window")) is { } window ? new DevWindow(window) : null,
            DebugEndpoints = (Json.Array(Json.Get(value, "debugEndpoints")) ?? new List<object?>()).Select(Json.Object)
                .Where(endpoint => Json.String(endpoint, "engine") != null && Json.String(endpoint, "url") != null)
                .Select(endpoint => new DevDebugEndpoint(Json.String(endpoint, "engine")!, Json.String(endpoint, "protocol") ?? "", Json.String(endpoint, "url")!)).ToList(),
            UnavailableDebugEndpoints = (Json.Array(Json.Get(value, "unavailableDebugEndpoints")) ?? new List<object?>()).Select(Json.Object)
                .Where(endpoint => Json.String(endpoint, "engine") != null)
                .Select(endpoint => new DevDebugEndpoint(Json.String(endpoint, "engine")!, Json.String(endpoint, "protocol") ?? "", Json.String(endpoint, "url") ?? "", Json.String(endpoint, "reason"))).ToList(),
        };
    }

    private static DateTimeOffset? Date(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
}

/// <summary>One project's dev session as the extension shows it.</summary>
public sealed class DevSnapshot
{
    public static DevSnapshot None { get; } = new(DevState.None, null, exited: false);

    public DevSnapshot(DevState state, DevSessionFile? session, bool exited)
    {
        State = state; Session = session; Exited = exited;
    }

    public DevState State { get; }
    public DevSessionFile? Session { get; }
    /// <summary>The file still says starting or ready, but its <c>dotnet runic dev</c> no longer runs (a killed <c>dev</c>).</summary>
    public bool Exited { get; }

    /// <summary>Whether a dev session runs (the liveness rule applied): starting or ready.</summary>
    public bool IsActive => State is DevState.Starting or DevState.Ready;

    /// <summary>Ready with <c>--no-window</c>: the IDE starts the Window (what F5 needs).</summary>
    public bool IsReadyForIde => State == DevState.Ready && Session?.Window?.OwnedByIde == true;

    /// <summary>One line for the Dev Session section and messages (VS Code: <c>devStateLabel</c>).</summary>
    public string Label => State switch
    {
        DevState.None => "not started",
        DevState.Starting => "starting",
        DevState.Ready => Session?.Window?.OwnedByIde == true ? "ready (Window in the IDE)" : "ready",
        _ => Exited ? $"stopped (process {Session?.ProcessId} exited)" : "stopped",
    };

    internal bool SameAs(DevSnapshot other) => State == other.State && Exited == other.Exited && Session?.Text == other.Session?.Text;
}

/// <summary>A process the liveness check found: its start time, and its image name and command line where readable.</summary>
public sealed class DevProcessInfo
{
    public DevProcessInfo(DateTimeOffset? startTime, string? imageName = null, IReadOnlyList<string>? commandLine = null)
    {
        StartTime = startTime; ImageName = imageName; CommandLine = commandLine;
    }

    /// <summary>Null when the process exists but its start time cannot be read (access denied).</summary>
    public DateTimeOffset? StartTime { get; }
    /// <summary>The executable's name without directory or extension (<see cref="Process.ProcessName"/>), if known.</summary>
    public string? ImageName { get; }
    /// <summary>The arguments, where the platform shows them (Linux <c>/proc/&lt;pid&gt;/cmdline</c>).</summary>
    public IReadOnlyList<string>? CommandLine { get; }
}

/// <summary>Reads the session file and looks up processes; tests replace it.</summary>
public interface IDevSessionSource
{
    /// <summary>The file's text, or null when it does not exist.</summary>
    string? Read(string path);
    /// <summary>The running process with this ID, or null when none runs.</summary>
    DevProcessInfo? FindProcess(int processId);
}

/// <summary>The file system and <see cref="Process"/>.</summary>
public sealed class FileSystemDevSessionSource : IDevSessionSource
{
    public static FileSystemDevSessionSource Instance { get; } = new();

    public string? Read(string path)
    {
        try
        {
            // dev replaces the file by rename; share everything so a reader never blocks the writer.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public DevProcessInfo? FindProcess(int processId)
    {
        if (processId <= 0) return null;
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        using (process)
        {
            DateTimeOffset? start = null;
            string? name = null;
            try
            {
                if (process.HasExited) return null;
                start = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            try { name = process.ProcessName; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            return new DevProcessInfo(start, name, CommandLineOf(processId));
        }
    }

    private static IReadOnlyList<string>? CommandLineOf(int processId)
    {
        string path = $"/proc/{processId}/cmdline";
        try { return File.Exists(path) ? File.ReadAllText(path).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>The session file's location, schema and liveness rule (VS Code: <c>vscode/src/dev-session.ts</c>).</summary>
public static class DevSessions
{
    /// <summary>The session file schema this extension reads; later versions of the schema only add properties.</summary>
    public const string Schema = "runic.application.tool.dev-session/1";

    /// <summary>How far a process's start time may differ from <c>processStartedAt</c>: APIs report it with different precision.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>The session file of a project: <c>obj/runic/dev-session.json</c> beside the project file (the default <c>BaseIntermediateOutputPath</c>).</summary>
    public static string PathFor(string project) => Path.Combine(Path.GetDirectoryName(project) ?? "", "obj", "runic", "dev-session.json");

    /// <summary>Parses a session file; null for another schema or a file that is not a session.</summary>
    public static DevSessionFile? Parse(string text) => DevSessionFile.Parse(text);

    /// <summary>True when <paramref name="arguments"/> (a process's command line) is <c>dotnet runic dev</c>: the tool (<c>dotnet-runic</c>, its DLL or apphost) with <c>dev</c>.</summary>
    public static bool IsDevCommandLine(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            string name = arguments[index].Replace('\\', '/');
            name = name.Substring(name.LastIndexOf('/') + 1);
            if (string.Equals(name, "dotnet-runic", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "dotnet-runic.dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "dotnet-runic.exe", StringComparison.OrdinalIgnoreCase))
                return arguments.Skip(index + 1).Contains("dev");
        }
        return false;
    }

    /// <summary>
    /// Whether the session's <c>dotnet runic dev</c> still runs (runic-sdk's rule): a process with the top-level
    /// <c>processId</c> runs and its start time matches <c>processStartedAt</c> to within a second, so a reused process
    /// ID reads as stopped. A file without <c>processStartedAt</c> (before 0.20) checks the command line where it can be
    /// read, else the image name (<c>dotnet</c> or <c>dotnet-runic</c>), as VS Code does. A process whose start time cannot
    /// be read counts as running.
    /// </summary>
    public static bool IsRunning(DevSessionFile session, IDevSessionSource source)
    {
        var process = source.FindProcess(session.ProcessId);
        if (process == null) return false;
        if (session.ProcessStartedAt is { } started)
            return process.StartTime is not { } actual || (actual - started).Duration() <= StartTimeTolerance;
        if (process.CommandLine is { Count: > 0 } commandLine) return IsDevCommandLine(commandLine);
        return process.ImageName == null
            || string.Equals(process.ImageName, "dotnet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(process.ImageName, "dotnet-runic", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The snapshot of a session file's text (null: no file); null when the text is not a session.</summary>
    public static DevSnapshot? Snapshot(string? text, IDevSessionSource source)
    {
        if (text == null) return DevSnapshot.None;
        var session = Parse(text);
        if (session == null) return null;
        if (session.State == DevSessionState.Stopped) return new DevSnapshot(DevState.Stopped, session, exited: false);
        return IsRunning(session, source)
            ? new DevSnapshot(session.State == DevSessionState.Ready ? DevState.Ready : DevState.Starting, session, exited: false)
            : new DevSnapshot(DevState.Stopped, session, exited: true);
    }
}

/// <summary>
/// Follows one project's session file (VS Code: <c>DevSessionFollower</c>). <see cref="Refresh"/> rereads it after a
/// file watcher event and on a timer, because a killed <c>dev</c> leaves <c>"ready"</c> behind without another write, and
/// applies the liveness rule. A file that does not parse (another schema, a writer that is not <c>dev</c>) keeps the last
/// snapshot. Thread-safe: a refresh that a later one overtook does not overwrite it.
/// </summary>
public sealed class DevSessionFollower
{
    private readonly IDevSessionSource source;
    private readonly object gate = new();
    private DevSnapshot current = DevSnapshot.None;
    private long revision;

    public DevSessionFollower(string project, IDevSessionSource? source = null, string? path = null)
    {
        Project = project;
        this.source = source ?? FileSystemDevSessionSource.Instance;
        Path = path ?? DevSessions.PathFor(project);
    }

    public string Project { get; }
    public string Path { get; }

    public DevSnapshot Snapshot { get { lock (gate) return current; } }

    /// <summary>Raised, on the refreshing thread, when the shown snapshot changed.</summary>
    public event EventHandler<DevSnapshot>? Changed;

    /// <summary>Rereads the file; returns the new snapshot and raises <see cref="Changed"/> when it differs.</summary>
    public DevSnapshot Refresh()
    {
        long mine;
        lock (gate) mine = ++revision;
        DevSnapshot? next;
        try { next = DevSessions.Snapshot(source.Read(Path), source); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { next = null; }
        lock (gate)
        {
            if (next == null || mine != revision || next.SameAs(current)) return current;
            current = next;
        }
        Changed?.Invoke(this, next);
        return next;
    }
}
