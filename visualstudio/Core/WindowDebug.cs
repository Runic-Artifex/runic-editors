using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// The Visual Studio debugger's identifiers that Debug the Window and Attach to the Page use (SPIKE-VS-0 §3, observed in
/// Visual Studio 2026 18.8 and 18.12). Visual Studio documents none of them for this use.
/// </summary>
public static class DebugEngines
{
    /// <summary>"Managed (.NET Core, .NET 5+)", the CoreCLR engine.</summary>
    public const string CoreClr = "{2E36F1D4-B23C-435D-AB41-18E608940038}";
    /// <summary>The transport "JavaScript and TypeScript (Chrome DevTools/V8 Inspector)"; its qualifier is <c>http://&lt;host&gt;:&lt;port&gt;/</c>.</summary>
    public const string CdpTransport = "{61D1E397-7AA6-4ED9-815A-0C5CA0E728B4}";
    /// <summary>The "JavaScript and TypeScript" engine of the CDP transport (js-debug's <c>pwa-chrome</c> attach).</summary>
    public const string JavaScriptOverCdp = "{394120B6-2FF9-4D0D-8953-913EF5CD0BCD}";
    /// <summary>
    /// "JavaScript and TypeScript" on the local transport: Visual Studio's js-debug engine, which runs the launch or attach
    /// configuration it gets as options (as for the <c>jsWebView2Debugging</c> launch profile). DevKit attaches the page
    /// through it (<see cref="JsDebugAttach"/>).
    /// </summary>
    public const string JavaScript = "{3FBCC828-6272-46D4-B5FA-B7E643672113}";
}

/// <summary>
/// The page attach Debug the Window and Attach to the Page start (runic-editors#44): a js-debug <c>attach</c>
/// configuration for the page's DevTools endpoint, run by the <see cref="DebugEngines.JavaScript"/> engine through
/// <c>IVsDebugger4.LaunchDebugTargets4</c>. That call returns once the attach has started. <c>Process2.Attach2</c>, used
/// before, waits for the attach in a nested message loop on Visual Studio's UI thread; when the attach never completed,
/// it never returned, and a Visual Studio told to close handled the close inside that loop and could not exit.
/// </summary>
public static class JsDebugAttach
{
    /// <summary>The configuration's name, which Visual Studio shows for the debugged page.</summary>
    public const string Name = "Runic page";

    /// <summary>
    /// The js-debug configuration (<c>type: pwa-chrome</c>, <c>request: attach</c>) for the page <paramref name="pageUrl"/>
    /// of the endpoint on 127.0.0.1:<paramref name="port"/>. The address is always 127.0.0.1, never <c>localhost</c>,
    /// which can resolve to <c>[::1]</c> where another user may listen (DEV-SEC-55, DEV-SEC-82). Like the transport
    /// attach, it sets no <c>webRoot</c>: TSX breakpoints bind through the development server's absolute source map sources.
    /// </summary>
    public static string Options(int port, string pageUrl)
    {
        var json = new StringBuilder("{");
        json.Append("\"type\":\"pwa-chrome\",\"request\":\"attach\",");
        json.Append("\"name\":").Append(Quote(Name)).Append(',');
        json.Append("\"address\":").Append(Quote(CdpEndpoints.LoopbackAddress)).Append(',');
        json.Append("\"port\":").Append(port.ToString(CultureInfo.InvariantCulture)).Append(',');
        json.Append("\"url\":").Append(Quote(pageUrl)).Append(',');
        json.Append("\"sourceMaps\":true}");
        return json.ToString();
    }

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                default:
                    if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(c);
                    break;
            }
        }
        return text.Append('"').ToString();
    }
}

/// <summary>The MSBuild properties <c>dotnet run</c> starts a project with (VS Code: <c>RunProperties</c>).</summary>
public sealed class RunProperties
{
    public RunProperties(string? runCommand, string? runArguments, string? runWorkingDirectory, string? targetPath)
    {
        RunCommand = runCommand; RunArguments = runArguments; RunWorkingDirectory = runWorkingDirectory; TargetPath = targetPath;
    }

    public string? RunCommand { get; }
    public string? RunArguments { get; }
    public string? RunWorkingDirectory { get; }
    public string? TargetPath { get; }
}

/// <summary>The program, arguments and directory of the Window, as <c>dotnet run</c> would start it.</summary>
public sealed class WindowProgram
{
    public WindowProgram(string program, string commandLineArguments, string workingDirectory, string? profile = null)
    {
        Program = program; CommandLineArguments = commandLineArguments; WorkingDirectory = workingDirectory; Profile = profile;
    }

    public string Program { get; }
    /// <summary>
    /// The arguments as one command line (<c>VsDebugTargetInfo4.bstrArg</c> takes one), as the project and the profile
    /// write them: never split and quoted again, so <c>/config:"C:\Program Files\x"</c> keeps its quotes and backslashes.
    /// </summary>
    public string CommandLineArguments { get; }
    /// <summary>The arguments the program gets from <see cref="CommandLineArguments"/> (<see cref="CommandLine.Split"/>).</summary>
    public IReadOnlyList<string> Arguments => CommandLine.Split(CommandLineArguments);
    public string WorkingDirectory { get; }
    /// <summary>The name of the launch profile applied, or null.</summary>
    public string? Profile { get; }
}

/// <summary>Which page engine Debug the Window opens a DevTools Protocol port for.</summary>
public enum PageHost { WebView2, CsWebUi }

/// <summary>
/// The environment and port of a Window that Debug the Window starts: the session file's <c>window.environment</c> on
/// top of Visual Studio's own, and the page's Chrome DevTools Protocol port (SPIKE-VS-0 §6, D070.17).
/// </summary>
public sealed class WindowDebugPlan
{
    public WindowDebugPlan(PageHost host, int port, IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> added)
    {
        Host = host; Port = port; Environment = environment; Added = added;
    }

    public PageHost Host { get; }
    /// <summary>The loopback port the page's engine listens on.</summary>
    public int Port { get; }
    /// <summary>The Window's whole environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
    /// <summary>What the plan sets beyond Visual Studio's environment (for the Output pane).</summary>
    public IReadOnlyDictionary<string, string> Added { get; }
    /// <summary>The endpoint the JavaScript engine attaches to.</summary>
    public string EndpointUrl => CdpEndpoints.Url(Port);
}

/// <summary>Debug the Window's decisions that need no Visual Studio (VS Code: <c>window-debug.ts</c>).</summary>
public static class WindowDebug
{
    public const string WebView2ArgumentsVariable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
    /// <summary>Runic.Desktop's CS-WebUI switch since 0.22 (runic-sdk#1082): <c>0</c> or a fixed port, in Development only.</summary>
    public const string WebUiPortVariable = "RUNIC_WEBUI_REMOTE_DEBUGGING_PORT";
    public const string EnvironmentVariable = "DOTNET_ENVIRONMENT";

    private static readonly Regex RemoteDebuggingPort = new(@"(?:^|\s)--remote-debugging-port=(\d+)(?=\s|$)", RegexOptions.CultureInvariant);

    /// <summary><c>dotnet msbuild &lt;project&gt; -getProperty:…</c> for the program <c>dotnet run</c> starts.</summary>
    public static IReadOnlyList<string> RunPropertiesArguments(string project, string configuration)
        => new[] { "msbuild", project, "-p:Configuration=" + configuration, "-getProperty:RunCommand", "-getProperty:RunArguments", "-getProperty:RunWorkingDirectory", "-getProperty:TargetPath" };

    /// <summary><c>dotnet msbuild -getProperty</c> prints <c>{ "Properties": { … } }</c> for several properties.</summary>
    public static RunProperties ParseRunProperties(string stdout)
    {
        Dictionary<string, object?>? properties;
        try { properties = Json.Object(Json.Get(Json.Object(Json.Parse(stdout.Trim())), "Properties")); }
        catch (FormatException error) { throw new InvalidOperationException("dotnet msbuild did not return the run properties: " + error.Message, error); }
        if (properties == null) throw new InvalidOperationException("dotnet msbuild returned no properties.");
        return new RunProperties(Json.String(properties, "RunCommand"), Json.String(properties, "RunArguments"), Json.String(properties, "RunWorkingDirectory"), Json.String(properties, "TargetPath"));
    }

    /// <summary>
    /// The program <c>dotnet run</c> starts, with the active launch profile applied as Visual Studio's F5 applies it
    /// (design/ide-run-debug §3.3, D084.2):
    /// <list type="bullet">
    /// <item>the program: the application host, or the DLL's <see cref="RunProperties.TargetPath"/> when <c>RunCommand</c>
    /// is <c>dotnet</c> (then the debugger runs it on the shared host, and <c>RunArguments</c>' leading
    /// <c>exec "&lt;dll&gt;"</c> is left out);</item>
    /// <item>the arguments: the project's <c>RunArguments</c> (from <c>StartArguments</c>), then the profile's
    /// <c>commandLineArgs</c>, joined as written, never split and quoted again;</item>
    /// <item>the directory: the profile's <c>workingDirectory</c> against the project folder, else
    /// <c>RunWorkingDirectory</c>, else the project folder.</item>
    /// </list>
    /// Without a profile it is what <c>dotnet run</c> would start.
    /// </summary>
    public static WindowProgram Program(string project, RunProperties properties, LaunchProfile? profile = null)
    {
        string? command = properties.RunCommand?.Trim();
        if (string.IsNullOrEmpty(command)) throw new InvalidOperationException($"The project has no RunCommand; is {Path.GetFileName(project)} an executable?");
        string name = command!.Replace('\\', '/');
        name = name.Substring(name.LastIndexOf('/') + 1);
        bool viaDotnet = Regex.IsMatch(name, "^dotnet(?:\\.exe)?$", RegexOptions.IgnoreCase);
        string arguments = (properties.RunArguments ?? "").Trim();
        if (viaDotnet) arguments = WithoutDotnetExec(arguments);
        string? program = viaDotnet ? properties.TargetPath : command;
        if (string.IsNullOrEmpty(program)) throw new InvalidOperationException("The project names no program to debug.");
        string folder = Path.GetDirectoryName(project) ?? "";
        string directory = properties.RunWorkingDirectory?.Trim() is { Length: > 0 } run ? run : folder;
        if (profile != null)
        {
            if (profile.CommandLineArgs?.Trim() is { Length: > 0 } extra) arguments = arguments.Length == 0 ? extra : arguments + " " + extra;
            if (profile.WorkingDirectory?.Trim() is { Length: > 0 } relative) directory = Resolve(folder, relative);
        }
        return new WindowProgram(program!, arguments, directory, profile?.Name);
    }

    /// <summary><c>RunArguments</c> without <c>dotnet</c>'s leading <c>exec</c> and <c>"&lt;dll&gt;"</c>; the rest as written.</summary>
    private static string WithoutDotnetExec(string arguments)
    {
        var tokens = CommandLine.Tokens(arguments);
        int skip = 0;
        if (skip < tokens.Count && tokens[skip].Value == "exec") skip++;
        if (skip < tokens.Count && tokens[skip].Value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) skip++;
        return skip == 0 ? arguments : skip < tokens.Count ? arguments.Substring(tokens[skip].Start).Trim() : "";
    }

    /// <summary><paramref name="directory"/> against <paramref name="folder"/> (an absolute path stays as it is).</summary>
    private static string Resolve(string folder, string directory)
    {
        string combined = Path.Combine(folder, directory);
        try { return Path.GetFullPath(combined); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return combined; }
    }

    /// <summary>
    /// <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> with <c>--remote-debugging-port=&lt;port&gt;</c> added to an existing
    /// value; a value that already names a port keeps it, and that port is the one to attach to.
    /// </summary>
    public static (string Value, int Port) MergeBrowserArguments(string? existing, int port)
    {
        string current = (existing ?? "").Trim();
        var match = RemoteDebuggingPort.Match(current);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int named) && named > 0) return (current, named);
        // --remote-debugging-port=0 asks WebView2 for a free port it reports only over js-debug's pipe: replace it.
        current = Regex.Replace(current, @"(?:^|\s)--remote-debugging-port=\S*", "").Trim();
        string flag = "--remote-debugging-port=" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (current.Length == 0 ? flag : current + " " + flag, port);
    }

    /// <summary>
    /// The Window's environment and port. CS-WebUI when the session asks for its port (<c>RUNIC_WEBUI_REMOTE_DEBUGGING_PORT</c>
    /// in <c>window.environment</c>, 9225 with <c>--no-window</c> since 0.22; <c>0</c> becomes <paramref name="freePort"/>);
    /// otherwise WebView2, whose port goes into <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c>, merged with the session's or
    /// Visual Studio's value. The variables, from lowest to highest (design/ide-run-debug §3.3): Visual Studio's,
    /// <c>DOTNET_ENVIRONMENT=Development</c> (as <c>dev</c> sets it; the session's own value counts as this default), the
    /// launch profile's <paramref name="profile"/> variables, then the session's Runic variables and the port. A profile
    /// that sets <c>RUNIC_WEBUI_REMOTE_DEBUGGING_PORT</c> (the template's CS-WebUI profile) asks for CS-WebUI too.
    /// </summary>
    public static WindowDebugPlan Plan(IReadOnlyDictionary<string, string> visualStudio, IReadOnlyDictionary<string, string>? session, Func<int> freePort, IReadOnlyList<KeyValuePair<string, string>>? profile = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in visualStudio) environment[pair.Key] = pair.Value;
        var added = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Set(string key, string value)
        {
            if (environment.TryGetValue(key, out string? old) && old == value) return;
            environment[key] = value; added[key] = value;
        }
        session ??= new Dictionary<string, string>();
        var sessionDefault = session.Where(pair => string.Equals(pair.Key, EnvironmentVariable, StringComparison.OrdinalIgnoreCase)).ToList();
        Set(EnvironmentVariable, sessionDefault.Count > 0 ? sessionDefault[0].Value : "Development");
        foreach (var pair in profile ?? Array.Empty<KeyValuePair<string, string>>()) Set(pair.Key, pair.Value);
        foreach (var pair in session)
            if (!string.Equals(pair.Key, EnvironmentVariable, StringComparison.OrdinalIgnoreCase)) Set(pair.Key, pair.Value);

        string? webUi = session.TryGetValue(WebUiPortVariable, out string? sessionPort) ? sessionPort
            : profile?.LastOrDefault(pair => string.Equals(pair.Key, WebUiPortVariable, StringComparison.OrdinalIgnoreCase)).Value;
        if (webUi != null)
        {
            int port = int.TryParse(webUi.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int fixedPort) && fixedPort is > 0 and < 65536 ? fixedPort : freePort();
            Set(WebUiPortVariable, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new WindowDebugPlan(PageHost.CsWebUi, port, environment, added);
        }
        environment.TryGetValue(WebView2ArgumentsVariable, out string? existing);
        var (value, webView2Port) = MergeBrowserArguments(existing, freePort());
        Set(WebView2ArgumentsVariable, value);
        return new WindowDebugPlan(PageHost.WebView2, webView2Port, environment, added);
    }

    /// <summary>A Win32 environment block (<c>VsDebugTargetInfo4.bstrEnv</c>): <c>name=value\0</c> each, sorted, and a final <c>\0</c>.</summary>
    public static string EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var pair in environment.Where(pair => pair.Key.Length > 0 && pair.Key.IndexOf('=') < 0 && pair.Key.IndexOf('\0') < 0 && pair.Value.IndexOf('\0') < 0)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        return block.Append('\0').ToString();
    }

    /// <summary>A free TCP port on 127.0.0.1, for the page's endpoint.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>A page's Chrome DevTools Protocol endpoint that Attach to the Page offers.</summary>
public sealed class PageEndpoint
{
    public PageEndpoint(string engine, string url, string source, int? windowProcessId = null) { Engine = engine; Url = url; Source = source; WindowProcessId = windowProcessId; }

    /// <summary><c>webview2</c> or <c>chromium</c>.</summary>
    public string Engine { get; }
    /// <summary><c>http://127.0.0.1:&lt;port&gt;/</c>.</summary>
    public string Url { get; }
    /// <summary>Where it comes from, for the picker: the session file, or a Window Debug the Window started.</summary>
    public string Source { get; }
    public int Port => new Uri(Url).Port;
    /// <summary>The Window whose page holds the endpoint, when known; the attach checks the port's owner against it (DEV-SEC-83).</summary>
    public int? WindowProcessId { get; }
}

/// <summary>The session file's CDP endpoints and the attach qualifier (SPIKE-VS-0 §3: <c>http://127.0.0.1:&lt;port&gt;/</c>).</summary>
public static class CdpEndpoints
{
    private static readonly string[] PageEngines = { "webview2", "chromium" };

    /// <summary>The only host DevKit attaches to: the Window's page engines listen on 127.0.0.1.</summary>
    public const string LoopbackAddress = "127.0.0.1";

    /// <summary>The loopback endpoint of a port, on 127.0.0.1 rather than <c>localhost</c>, which can resolve to <c>[::1]</c> (DEV-SEC-55).</summary>
    public static string Url(int port) => "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";

    /// <summary>
    /// The <c>debugEndpoints</c> a JavaScript engine can attach to: WebView2's and CS-WebUI's Chromium CDP endpoints on
    /// loopback, without those the session also lists under <c>unavailableDebugEndpoints</c> (DEV-SEC-83).
    /// </summary>
    public static IReadOnlyList<PageEndpoint> FromSession(DevSessionFile? session)
    {
        if (session == null) return Array.Empty<PageEndpoint>();
        var unavailable = new HashSet<string>(session.UnavailableDebugEndpoints.Select(endpoint => Normalize(endpoint.Url)).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        return session.DebugEndpoints
            .Where(endpoint => endpoint.Protocol == "cdp" && PageEngines.Contains(endpoint.Engine) && !(Normalize(endpoint.Url) is { } url && unavailable.Contains(url)))
            .Select(endpoint => Normalize(endpoint.Url) is { } url ? new PageEndpoint(endpoint.Engine, url, "dev session", session.Window?.ProcessId) : null)
            .OfType<PageEndpoint>().ToList();
    }

    /// <summary>The endpoints in order, without repeated URLs (the first wins).</summary>
    public static IReadOnlyList<PageEndpoint> Distinct(IEnumerable<PageEndpoint> endpoints)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return endpoints.Where(endpoint => seen.Add(endpoint.Url)).ToList();
    }

    /// <summary><c>http://127.0.0.1:&lt;port&gt;/</c> for a loopback HTTP URL with a port (<c>localhost</c> becomes 127.0.0.1); null otherwise.</summary>
    public static string? Normalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.IsDefaultPort) return null;
        if (!(uri.Host == "127.0.0.1" || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))) return null;
        return Url(uri.Port);
    }

    /// <summary>The URL that says whether the endpoint answers (<c>/json/version</c>).</summary>
    public static string VersionUrl(string url) => url.TrimEnd('/') + "/json/version";

    /// <summary>
    /// Which targets the CDP transport lists to attach to: the pages, not DevTools or extension pages (the Runic DevTools
    /// extension's) and not <c>about:blank</c>.
    /// </summary>
    public static bool IsPageTarget(string? name)
    {
        string text = (name ?? "").Trim();
        if (text.Length == 0 || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return false;
        string[] skipped = { "devtools://", "chrome-extension://", "chrome://", "edge://", "extension://" };
        return !skipped.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a <c>/json/version</c> response is a DevTools endpoint's (it names a <c>webSocketDebuggerUrl</c> or a <c>Browser</c>).</summary>
    public static bool IsVersionResponse(string text)
    {
        try
        {
            var value = Json.Object(Json.Parse(text));
            return Json.String(value, "webSocketDebuggerUrl") != null || Json.String(value, "Browser") != null;
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>
/// Whether the project's <c>dotnet-runic</c> has <c>dev --debug-endpoint</c> (0.22, runic-sdk#1082), which a CS-WebUI
/// Window needs to open its DevTools Protocol port when DevKit starts <c>dev</c> with its Window (D070.17).
/// </summary>
public static class DevToolVersions
{
    private static readonly Regex Version = new(@"(?<![\d.])(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?", RegexOptions.CultureInvariant);

    /// <summary>The first version in <c>dotnet runic --version</c>'s output (<c>0.22.0</c>, <c>0.22.0+abc</c>, <c>dotnet-runic 0.22.0</c>), or null.</summary>
    public static string? ParseVersionOutput(string? text)
    {
        var match = Version.Match(text ?? "");
        return match.Success ? match.Value : null;
    }

    /// <summary>0.22.0 and later, including 0.22's pre-releases and 1.x; false for an unknown version.</summary>
    public static bool SupportsDebugEndpoint(string? version)
    {
        var match = Version.Match(version ?? "");
        if (!match.Success) return false;
        int major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int minor = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return major > 0 || minor >= 22;
    }

    /// <summary>
    /// Whether <c>dev</c> stops at a <c>stop</c> line or the end of its standard input with
    /// <c>RUNIC_DEV_STOP_ON_INPUT=1</c> (0.22, runic-sdk#1092, WIN-06); the same versions as <see cref="SupportsDebugEndpoint"/>.
    /// </summary>
    public static bool SupportsStopOnInput(string? version) => SupportsDebugEndpoint(version);

    /// <summary>
    /// The tool manifest's version when it has one, else <c>&lt;dotnet&gt; runic|rax --version</c> in the project's directory
    /// (within 15 seconds); null when neither says.
    /// </summary>
    public static async Task<string?> DetectAsync(string dotnet, string project, string? manifestVersion, CancellationToken cancellationToken)
    {
        if (manifestVersion != null) return manifestVersion;
        try
        {
            var result = await ToolProcess.RunAsync(dotnet, new[] { RunicTools.VerbFor(project), "--version" }, Path.GetDirectoryName(project) ?? Environment.CurrentDirectory, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? ParseVersionOutput(result.Output) : null;
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException) { return null; }
    }
}

/// <summary>A finished command: its exit code and output.</summary>
public sealed class ToolProcessResult
{
    public ToolProcessResult(int exitCode, string output, string error) { ExitCode = exitCode; Output = output; Error = error; }
    public int ExitCode { get; }
    public string Output { get; }
    public string Error { get; }
    /// <summary>The last line of standard error, else the exit code, for a message.</summary>
    public string Detail => (Error.Trim().Length > 0 ? Error : Output.Trim().Length > 0 ? Output : $"exit code {ExitCode}").Trim().Split('\n').Last().Trim();
}

/// <summary>Runs a short command with redirected output (<c>dotnet msbuild -getProperty</c>, <c>dotnet runic --version</c>).</summary>
public static class ToolProcess
{
    public static async Task<ToolProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, string directory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = program,
            Arguments = string.Join(" ", arguments.Select(CommandLine.Quote)),
            WorkingDirectory = directory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        start.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        try { if (!process.Start()) throw new InvalidOperationException($"Could not start {program}."); }
        catch (System.ComponentModel.Win32Exception error) { throw new InvalidOperationException($"Could not start {program}: {error.Message}", error); }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        void Kill() { try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        using (cancellationToken.Register(Kill))
        {
            var all = Task.WhenAll(stdout, stderr, exited.Task);
            if (await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false) != all)
            {
                Kill();
                throw new TimeoutException($"{Path.GetFileName(program)} {string.Join(" ", arguments.Take(2))} did not finish within {Math.Round(timeout.TotalSeconds)} seconds.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        process.WaitForExit();
        return new ToolProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }
}
