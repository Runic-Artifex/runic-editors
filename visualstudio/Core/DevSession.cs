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
        if (value == null || Json.String(value, "schema") is not (DevSessions.Schema or DevSessions.RaxSchema)) return null;
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

/// <summary>
/// The file times and the touch of <c>obj/runic/dev-stopped</c> (<see cref="DevSessions.StoppedStampName"/>) that the
/// follower needs to mark a killed <c>dev</c> as stopped; tests replace it.
/// </summary>
public interface IDevStoppedStamp
{
    /// <summary>The file's last write time (UTC), or null when it does not exist.</summary>
    DateTime? LastWriteTimeUtc(string path);
    /// <summary>Writes the file empty with the current time, as <c>dev</c> does when it stops; throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    void Touch(string path);
}

/// <summary>The file system and <see cref="Process"/>.</summary>
public sealed class FileSystemDevSessionSource : IDevSessionSource, IDevStoppedStamp
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

    public DateTime? LastWriteTimeUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Touch(string path)
    {
        // As runic-sdk's DevSession.Stop: an empty file, and its time set, since rewriting an empty file need not change
        // it. The directory is not created: without obj/runic (a clean), the next build runs anyway.
        File.WriteAllBytes(path, Array.Empty<byte>());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
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
    /// <summary>The same schema under the rename's names (D079, R5 A8), which <c>rax dev</c> writes to <c>obj/rax/</c>.</summary>
    public const string RaxSchema = "rax.application.tool.dev-session/1";

    /// <summary>How far a process's start time may differ from <c>processStartedAt</c>: APIs report it with different precision.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The session file of a project: <c>obj/runic/dev-session.json</c> beside the project file (the default
    /// <c>BaseIntermediateOutputPath</c>), or <c>obj/rax/dev-session.json</c> when the project's tool manifest lists
    /// <c>RunicArtifex.Tool</c> (R5 A8).
    /// </summary>
    public static string PathFor(string project) => PathFor(project, RunicTools.ProjectCoreTool(project)?.Tool);

    /// <summary>The session file of a project whose core tool is <paramref name="tool"/> (null: <c>dotnet-runic</c>).</summary>
    public static string PathFor(string project, RunicToolId? tool)
        => Path.Combine(Path.GetDirectoryName(project) ?? "", "obj", tool is { Former: false } ? "rax" : "runic", "dev-session.json");

    /// <summary>
    /// The stamp that <c>dotnet runic dev</c> touches when it stops (runic-sdk <c>DevSession.StoppedStampName</c>).
    /// Visual Studio's fast up-to-date check compares it with the build's <c>runic-www.stamp</c>, so the next F5 builds
    /// and copies the frontend over the development pages that <c>dev</c> left in <c>www/</c>.
    /// </summary>
    public const string StoppedStampName = "dev-stopped";

    /// <summary>The <c>dev-stopped</c> stamp beside a session file (<c>obj/runic/dev-stopped</c>).</summary>
    public static string StoppedStampPathFor(string sessionPath) => Path.Combine(Path.GetDirectoryName(sessionPath) ?? "", StoppedStampName);

    /// <summary>
    /// Whether the follower touches <c>dev-stopped</c> for this snapshot (VS Code: <c>needsStoppedStamp</c>): only when
    /// the file still says starting or ready and its <c>dev</c> no longer runs (<see cref="DevSnapshot.Exited"/>; not for
    /// a live process, a reused process ID that runs, or a file that says stopped, whose <c>dev</c> touched it), and not
    /// when the stamp is already at least as new as the session file (an earlier touch for this session, also from
    /// another IDE or before a restart). The follower adds: at most once per process ID.
    /// </summary>
    public static bool NeedsStoppedStamp(DevSnapshot snapshot, DateTime? stampWrittenUtc, DateTime? sessionWrittenUtc)
    {
        if (!snapshot.Exited || snapshot.Session is not { State: DevSessionState.Starting or DevSessionState.Ready }) return false;
        return !(stampWrittenUtc is { } stamp && sessionWrittenUtc is { } session && stamp >= session);
    }

    /// <summary>Parses a session file; null for another schema or a file that is not a session.</summary>
    public static DevSessionFile? Parse(string text) => DevSessionFile.Parse(text);

    /// <summary>
    /// The core tool's program names in a command line: <c>dotnet-runic</c> (its DLL or apphost), or after the rename
    /// <c>rax</c> (the command, the global tool's shim and, under NativeAOT, the native program itself, R5 A7) and
    /// <c>RunicArtifex.Tool</c> (its DLL or apphost). <c>dotnet rax dev</c> need not run under a <c>dotnet</c> parent, so
    /// the program alone decides.
    /// </summary>
    private static readonly HashSet<string> ToolPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet-runic", "dotnet-runic.dll", "dotnet-runic.exe", "rax", "rax.exe", "RunicArtifex.Tool", "RunicArtifex.Tool.dll", "RunicArtifex.Tool.exe",
    };

    /// <summary>Images (without <c>.exe</c>) of a running <c>dev</c>: the <c>dotnet</c> host or the tool's own program.</summary>
    private static readonly HashSet<string> DevImages = new(StringComparer.OrdinalIgnoreCase) { "dotnet", "dotnet-runic", "rax", "RunicArtifex.Tool" };

    /// <summary>True when <paramref name="arguments"/> (a process's command line) is <c>dotnet runic dev</c> or <c>rax dev</c>: the tool's program with <c>dev</c>.</summary>
    public static bool IsDevCommandLine(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            string name = arguments[index].Replace('\\', '/');
            name = name.Substring(name.LastIndexOf('/') + 1);
            if (ToolPrograms.Contains(name)) return arguments.Skip(index + 1).Contains("dev");
        }
        return false;
    }

    /// <summary>
    /// Whether the session's <c>dotnet runic dev</c> still runs (runic-sdk's rule): a process with the top-level
    /// <c>processId</c> runs and its start time matches <c>processStartedAt</c> to within a second, so a reused process
    /// ID reads as stopped. A file without <c>processStartedAt</c> (before 0.20) checks the command line where it can be
    /// read, else the image name (<c>dotnet</c>, <c>dotnet-runic</c>, <c>rax</c> or <c>RunicArtifex.Tool</c>), as VS Code does. A process whose start time cannot
    /// be read counts as running.
    /// </summary>
    public static bool IsRunning(DevSessionFile session, IDevSessionSource source)
    {
        var process = source.FindProcess(session.ProcessId);
        if (process == null) return false;
        if (session.ProcessStartedAt is { } started)
            return process.StartTime is not { } actual || (actual - started).Duration() <= StartTimeTolerance;
        if (process.CommandLine is { Count: > 0 } commandLine) return IsDevCommandLine(commandLine);
        return process.ImageName == null || DevImages.Contains(process.ImageName);
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
/// snapshot. Thread-safe: a refresh that a later one overtook does not overwrite it. When the file says starting or
/// ready but its <c>dev</c> has exited (killed, crashed), it touches <c>obj/runic/dev-stopped</c> beside the file once
/// per process ID, as <c>dev</c> does on a clean stop (<see cref="DevSessions.NeedsStoppedStamp"/>), so the next F5
/// builds and puts the production frontend back in <c>www/</c> (runic-editors#29).
/// </summary>
public sealed class DevSessionFollower
{
    private readonly IDevSessionSource source;
    private readonly IDevStoppedStamp? stamp;
    private readonly IRunicOutput? output;
    private readonly object gate = new();
    private readonly HashSet<int> marked = new();
    private DevSnapshot current = DevSnapshot.None;
    private long revision;

    /// <param name="stamp">Touches <c>dev-stopped</c>; by default the file system, or <paramref name="source"/> when it is one; null with another source: no stamp.</param>
    /// <param name="output">Where the touch, or why it failed, is logged (the Runic SDK Output pane).</param>
    public DevSessionFollower(string project, IDevSessionSource? source = null, string? path = null, IDevStoppedStamp? stamp = null, IRunicOutput? output = null)
    {
        Project = project;
        this.source = source ?? FileSystemDevSessionSource.Instance;
        this.stamp = stamp ?? this.source as IDevStoppedStamp;
        this.output = output;
        Path = path ?? DevSessions.PathFor(project);
    }

    public string Project { get; }
    public string Path { get; }
    /// <summary><c>obj/runic/dev-stopped</c> beside the session file.</summary>
    public string StoppedStampPath => DevSessions.StoppedStampPathFor(Path);

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
        if (next is { Exited: true }) MarkStopped(next);
        lock (gate)
        {
            if (next == null || mine != revision || next.SameAs(current)) return current;
            current = next;
        }
        Changed?.Invoke(this, next);
        return next;
    }

    /// <summary>Touches <c>dev-stopped</c> for a killed <c>dev</c>, at most once per process ID; logs a failure instead of throwing.</summary>
    private void MarkStopped(DevSnapshot snapshot)
    {
        if (stamp == null || snapshot.Session is not { } session) return;
        lock (gate) if (marked.Contains(session.ProcessId)) return;
        string target = StoppedStampPath;
        DateTime? stampWritten, sessionWritten;
        try { stampWritten = stamp.LastWriteTimeUtc(target); sessionWritten = stamp.LastWriteTimeUtc(Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { stampWritten = sessionWritten = null; }
        // Decided and recorded under the lock, so two refreshes that both see the dead process touch once.
        lock (gate)
        {
            if (!marked.Add(session.ProcessId)) return;
            if (!DevSessions.NeedsStoppedStamp(snapshot, stampWritten, sessionWritten)) return;
        }
        string name = System.IO.Path.GetFileName(Project);
        try
        {
            stamp.Touch(target);
            output?.WriteLine($"{name}: dotnet runic dev (process {session.ProcessId}) exited without stopping; touched {target} so that the next build restores www/.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            output?.WriteLine($"{name}: dotnet runic dev (process {session.ProcessId}) exited without stopping, and {target} could not be touched ({error.Message}); rebuild before the next F5 if www/ still has the development pages.");
        }
    }
}
