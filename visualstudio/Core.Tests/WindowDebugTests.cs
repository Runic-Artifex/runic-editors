using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// Debug the Window and Attach to the Page (VS-5): the run properties, the Window's environment and DevTools Protocol
/// port per host, the session file's page endpoints, and the <c>--debug-endpoint</c> version gate.
/// </summary>
public class WindowDebugTests
{
    private static readonly Dictionary<string, string> NoEnvironment = new();

    [Test]
    public void Run_properties_give_the_application_host_or_the_dll()
    {
        Check.Same(new[] { "msbuild", "/a/App.csproj", "-p:Configuration=Debug", "-getProperty:RunCommand", "-getProperty:RunArguments", "-getProperty:RunWorkingDirectory", "-getProperty:TargetPath" },
            WindowDebug.RunPropertiesArguments("/a/App.csproj", "Debug"));
        var host = WindowDebug.Program("/a/App.csproj", WindowDebug.ParseRunProperties(
            "{\n  \"Properties\": { \"RunCommand\": \"C:\\\\a\\\\bin\\\\Debug\\\\net10.0\\\\App.exe\", \"RunArguments\": \"--x \\\"a b\\\"\", \"RunWorkingDirectory\": \"C:\\\\a\\\\bin\", \"TargetPath\": \"C:\\\\a\\\\bin\\\\App.dll\" }\n}\n"));
        Check.Equal(@"C:\a\bin\Debug\net10.0\App.exe", host.Program);
        Check.Same(new[] { "--x", "a b" }, host.Arguments);
        Check.Equal("--x \"a b\"", host.CommandLineArguments);
        Check.Equal(@"C:\a\bin", host.WorkingDirectory);

        var shared = WindowDebug.Program("/a/App.csproj", new RunProperties("dotnet", "exec \"/a/bin/App.dll\" --flag", "", "/a/bin/App.dll"));
        Check.Equal("/a/bin/App.dll", shared.Program);
        Check.Same(new[] { "--flag" }, shared.Arguments);
        Check.Equal(Path.GetDirectoryName("/a/App.csproj"), shared.WorkingDirectory);

        Check.True(Throws(() => WindowDebug.Program("/a/Lib.csproj", new RunProperties("", null, null, "/a/Lib.dll"))).Contains("Lib.csproj"), "no RunCommand");
        Check.True(Throws(() => WindowDebug.ParseRunProperties("Debug")).Contains("run properties"), "not JSON");
        Check.True(Throws(() => WindowDebug.ParseRunProperties("{}")).Contains("no properties"), "no Properties");
    }

    [Test]
    public void Webview2_gets_a_free_port_merged_into_its_browser_arguments()
    {
        var session = new Dictionary<string, string> { ["DOTNET_ENVIRONMENT"] = "Development", ["RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS"] = @"C:\ext" };
        var visualStudio = new Dictionary<string, string> { ["PATH"] = @"C:\bin", ["DOTNET_ENVIRONMENT"] = "Production" };
        var plan = WindowDebug.Plan(visualStudio, session, () => 41000);
        Check.Equal(PageHost.WebView2, plan.Host);
        Check.Equal(41000, plan.Port);
        Check.Equal("http://127.0.0.1:41000/", plan.EndpointUrl);
        Check.Equal("Development", plan.Environment["DOTNET_ENVIRONMENT"]);
        Check.Equal(@"C:\bin", plan.Environment["PATH"]);
        Check.Equal("--remote-debugging-port=41000", plan.Environment[WindowDebug.WebView2ArgumentsVariable]);
        Check.Same(new[] { "DOTNET_ENVIRONMENT", "RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS", WindowDebug.WebView2ArgumentsVariable }, plan.Added.Keys);

        // Without a session environment (0.19 and older): Development, as dev sets it.
        Check.Equal("Development", WindowDebug.Plan(NoEnvironment, null, () => 1).Environment["DOTNET_ENVIRONMENT"]);
    }

    [Test]
    public void Existing_browser_arguments_are_kept_and_a_named_port_wins()
    {
        Check.Equal(("--enable-features=X --remote-debugging-port=41000", 41000), WindowDebug.MergeBrowserArguments("--enable-features=X", 41000));
        Check.Equal(("--remote-debugging-port=9222 --enable-features=X", 9222), WindowDebug.MergeBrowserArguments(" --remote-debugging-port=9222 --enable-features=X ", 41000));
        // js-debug's --remote-debugging-port=0 reports its port only over a pipe: replaced.
        Check.Equal(("--x --remote-debugging-port=41000", 41000), WindowDebug.MergeBrowserArguments("--remote-debugging-port=0 --x", 41000));
        Check.Equal(("--remote-debugging-port=41000", 41000), WindowDebug.MergeBrowserArguments(null, 41000));

        // The session's value wins over Visual Studio's, then gets the port.
        var plan = WindowDebug.Plan(new Dictionary<string, string> { [WindowDebug.WebView2ArgumentsVariable] = "--vs" },
            new Dictionary<string, string> { [WindowDebug.WebView2ArgumentsVariable] = "--session" }, () => 41001);
        Check.Equal("--session --remote-debugging-port=41001", plan.Environment[WindowDebug.WebView2ArgumentsVariable]);
        var inherited = WindowDebug.Plan(new Dictionary<string, string> { [WindowDebug.WebView2ArgumentsVariable] = "--remote-debugging-port=9333" }, NoEnvironment, () => 41002);
        Check.Equal(9333, inherited.Port);
        Check.True(!inherited.Added.ContainsKey(WindowDebug.WebView2ArgumentsVariable), "unchanged values are not listed as added");
    }

    [Test]
    public void Cswebui_uses_the_sessions_port()
    {
        // dev --no-window (0.22) puts 9225 into window.environment (runic-sdk#1082).
        var plan = WindowDebug.Plan(NoEnvironment, new Dictionary<string, string> { ["DOTNET_ENVIRONMENT"] = "Development", [WindowDebug.WebUiPortVariable] = "9225" }, () => throw new InvalidOperationException("no free port needed"));
        Check.Equal(PageHost.CsWebUi, plan.Host);
        Check.Equal(9225, plan.Port);
        Check.Equal("9225", plan.Environment[WindowDebug.WebUiPortVariable]);
        Check.True(!plan.Environment.ContainsKey(WindowDebug.WebView2ArgumentsVariable), "no WebView2 variable for CS-WebUI");
        // 0 (a free port the Window reports only on its output) and nonsense become a port the extension knows.
        Check.Equal(41003, WindowDebug.Plan(NoEnvironment, new Dictionary<string, string> { [WindowDebug.WebUiPortVariable] = "0" }, () => 41003).Port);
        var fixedNonsense = WindowDebug.Plan(NoEnvironment, new Dictionary<string, string> { [WindowDebug.WebUiPortVariable] = "99999" }, () => 41004);
        Check.Equal("41004", fixedNonsense.Environment[WindowDebug.WebUiPortVariable]);
    }

    [Test]
    public void Environment_block_is_sorted_and_double_null_terminated()
    {
        var block = WindowDebug.EnvironmentBlock(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1", ["=C:"] = "skip", ["bad\0"] = "x" });
        Check.Equal("A=1\0b=2\0\0", block);
        Check.Equal("\0", WindowDebug.EnvironmentBlock(NoEnvironment));
        int port = WindowDebug.FreePort();
        Check.True(port is > 0 and < 65536, "a port");
    }

    [Test]
    public void Session_page_endpoints_are_webview2_and_chromium_cdp_on_loopback()
    {
        var session = DevSessions.Parse("""
            {
              "schema": "runic.application.tool.dev-session/1", "state": "ready", "processId": 1, "project": "/a/App.csproj",
              "debugEndpoints": [
                { "engine": "webview2", "protocol": "cdp", "url": "http://127.0.0.1:9222/" },
                { "engine": "chromium", "protocol": "cdp", "url": "http://localhost:9225" },
                { "engine": "webkitgtk", "protocol": "cdp", "url": "http://127.0.0.1:9223/" },
                { "engine": "webkitgtk", "protocol": "webkit-remote-inspector", "url": "inspector://127.0.0.1:41653" },
                { "engine": "chromium", "protocol": "cdp", "url": "http://192.168.1.2:9226/" },
                { "engine": "webview2", "protocol": "cdp", "url": "http://127.0.0.1:9222/" }
              ],
              "window": { "owner": "dev", "processId": 4242 },
              "unavailableDebugEndpoints": [ { "engine": "chromium", "protocol": "cdp", "url": "http://127.0.0.1:9230/", "reason": "port in use" } ]
            }
            """)!;
        var endpoints = CdpEndpoints.FromSession(session);
        Check.Same(new[] { "webview2 http://127.0.0.1:9222/", "chromium http://127.0.0.1:9225/", "webview2 http://127.0.0.1:9222/" }, endpoints.Select(item => item.Engine + " " + item.Url));
        var launched = new PageEndpoint("webview2", "http://127.0.0.1:41000/", "Debug the Window");
        Check.Same(new[] { "http://127.0.0.1:41000/", "http://127.0.0.1:9222/", "http://127.0.0.1:9225/" }, CdpEndpoints.Distinct(new[] { launched }.Concat(endpoints)).Select(item => item.Url));
        Check.Equal(9225, endpoints[1].Port);
        Check.Equal("port in use", session.UnavailableDebugEndpoints.Single().Reason);
        Check.Equal<int?>(4242, endpoints[0].WindowProcessId, "the session's Window holds its endpoints");
        Check.Equal<int?>(null, launched.WindowProcessId);
        Check.Equal(0, CdpEndpoints.FromSession(null).Count);
    }

    [Test]
    public void Session_endpoints_the_session_lists_as_unavailable_are_not_offered()
    {
        // DEV-SEC-83: a port dev reports as unavailable is another process's, even while debugEndpoints still names it.
        var session = DevSessions.Parse("""
            {
              "schema": "runic.application.tool.dev-session/1", "state": "ready", "processId": 1, "project": "/a/App.csproj",
              "debugEndpoints": [
                { "engine": "chromium", "protocol": "cdp", "url": "http://localhost:9225/" },
                { "engine": "chromium", "protocol": "cdp", "url": "http://127.0.0.1:51234/" }
              ],
              "unavailableDebugEndpoints": [ { "engine": "chromium", "protocol": "cdp", "url": "http://127.0.0.1:9225/", "reason": "port in use" } ]
            }
            """)!;
        Check.Same(new[] { "http://127.0.0.1:51234/" }, CdpEndpoints.FromSession(session).Select(item => item.Url));
    }

    [Test]
    public void Cdp_qualifiers_targets_and_version_response()
    {
        Check.Equal("http://127.0.0.1:9222/", CdpEndpoints.Url(9222));
        Check.Equal("http://127.0.0.1:9222/", CdpEndpoints.Normalize("http://localhost:9222"), "localhost is attached as 127.0.0.1 (DEV-SEC-82)");
        Check.Equal(null, CdpEndpoints.Normalize("http://[::1]:9222/"));
        Check.Equal("http://127.0.0.1:9222/json/version", CdpEndpoints.VersionUrl("http://127.0.0.1:9222/"));
        Check.Equal(null, CdpEndpoints.Normalize("https://127.0.0.1:9222/"));
        Check.Equal(null, CdpEndpoints.Normalize("http://127.0.0.1/"));
        Check.True(CdpEndpoints.IsPageTarget("http://127.0.0.1:50604/surface-102c/index.html"), "the Window's page");
        Check.True(CdpEndpoints.IsPageTarget("Runic Views starter"), "a title");
        foreach (string skipped in new[] { "", "about:blank", "devtools://devtools/bundled/inspector.html", "chrome-extension://cddbfcinbkaindmfgcfdgapddkjeadgh/panel.html", "edge://settings" })
            Check.True(!CdpEndpoints.IsPageTarget(skipped), skipped);
        Check.True(CdpEndpoints.IsVersionResponse("""{ "Browser": "Edg/157.0.4341.46", "webSocketDebuggerUrl": "ws://127.0.0.1:9222/devtools/browser/x" }"""), "WebView2");
        Check.True(!CdpEndpoints.IsVersionResponse("<html>"), "not JSON");
        Check.True(!CdpEndpoints.IsVersionResponse("{}"), "not DevTools");
    }

    [Test]
    public void Debug_endpoint_needs_dotnet_runic_0_22()
    {
        foreach (string version in new[] { "0.22.0", "0.22.0-rc.1", "0.23.1", "1.0.0-rc.2", "1.0.0" })
            Check.True(DevToolVersions.SupportsDebugEndpoint(version), version);
        foreach (string? version in new[] { "0.21.0", "0.9.0", "0.2.30", null, "", "main" })
            Check.True(!DevToolVersions.SupportsDebugEndpoint(version), version ?? "null");
        Check.Equal("0.22.0", DevToolVersions.ParseVersionOutput("0.22.0+abc123\n"));
        Check.Equal("0.21.0", DevToolVersions.ParseVersionOutput("dotnet-runic 0.21.0\r\n"));
        Check.Equal(null, DevToolVersions.ParseVersionOutput("Could not execute because the specified command or file was not found."));
        Check.Same(new[] { "runic", "dev", "--debug-endpoint", "--project", "/a/App.csproj" }, DevOutput.Arguments("/a/App.csproj", noWindow: false, debugEndpoint: true));
        Check.Same(new[] { "runic", "dev", "--no-window", "--project", "/a/App.csproj" }, DevOutput.Arguments("/a/App.csproj", noWindow: true, debugEndpoint: true));
    }

    [Test]
    public void Stop_on_input_needs_dotnet_runic_0_22()
    {
        foreach (string version in new[] { "0.22.0", "0.22.0-rc.1", "0.23.1", "1.0.0" })
            Check.True(DevToolVersions.SupportsStopOnInput(version), version);
        foreach (string? version in new[] { "0.21.0", "0.21.5", null, "", "main" })
            Check.True(!DevToolVersions.SupportsStopOnInput(version), version ?? "null");
    }

    [Test]
    public async Task Detect_prefers_the_manifest_and_falls_back_to_the_tools_version()
    {
        Check.Equal("0.22.0", await DevToolVersions.DetectAsync("/nonexistent/dotnet", "/a/App.csproj", "0.22.0", CancellationToken.None));
        // A dotnet that cannot start gives no version, not an error.
        Check.Equal(null, await DevToolVersions.DetectAsync(Path.Combine(Path.GetTempPath(), "no-such-dotnet-" + Guid.NewGuid().ToString("N")), Path.Combine(Path.GetTempPath(), "App.csproj"), null, CancellationToken.None));
        if (OperatingSystem.IsWindows()) return;
        string directory = Directory.CreateTempSubdirectory("runic-version-").FullName;
        try
        {
            string fake = Path.Combine(directory, "dotnet");
            File.WriteAllText(fake, "#!/bin/sh\necho \"0.22.1+deadbeef\"\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Check.Equal("0.22.1", await DevToolVersions.DetectAsync(fake, Path.Combine(directory, "App.csproj"), null, CancellationToken.None));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) { return error.Message; }
        throw new InvalidOperationException("expected an exception");
    }
}

/// <summary>runic-editors#44: the page attach is a js-debug attach configuration, started without Attach2's nested wait.</summary>
public class JsDebugAttachTests
{
    [Test]
    public void The_options_attach_js_debug_to_the_page_of_the_endpoint()
    {
        string options = JsDebugAttach.Options(59449, "http://127.0.0.1:59451/surface-87e3/index.html");
        var parsed = Json.Object(Json.Parse(options));
        Check.Equal("pwa-chrome", Json.String(parsed, "type"));
        Check.Equal("attach", Json.String(parsed, "request"));
        Check.Equal("127.0.0.1", Json.String(parsed, "address"));
        Check.Equal<double?>(59449, Json.Number(parsed, "port"));
        Check.Equal("http://127.0.0.1:59451/surface-87e3/index.html", Json.String(parsed, "url"));
        Check.Equal<bool?>(true, Json.Boolean(parsed, "sourceMaps"));
        Check.Equal<object?>(null, Json.Get(parsed, "webRoot"), "no webRoot, as the transport attach");
    }

    [Test]
    public void Page_urls_are_escaped()
    {
        string options = JsDebugAttach.Options(9222, "http://localhost:5173/a\"b\\c\u0001");
        Check.Equal("http://localhost:5173/a\"b\\c\u0001", Json.String(Json.Object(Json.Parse(options)), "url"));
        Check.Equal("127.0.0.1", Json.String(Json.Object(Json.Parse(options)), "address"), "never localhost, which can be [::1] (DEV-SEC-82)");
    }
}

/// <summary>DEV-SEC-83: the debugger attaches only to a port the Window's page holds.</summary>
public class EndpointOwnersTests
{
    private static readonly DateTime WindowStart = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    // 100: the Window; 101: its WebView2 browser; 200: this user's CS-WebUI browser, whose parent cmd exited;
    // 300: another user's listener; 400: this user's program from before the Window; 500: a reused parent ID.
    private static readonly Dictionary<int, int> Parents = new() { [100] = 1, [101] = 100, [200] = 199, [300] = 2, [400] = 3, [500] = 100 };
    private static readonly Dictionary<int, DateTime> Starts = new()
    {
        [100] = WindowStart, [101] = WindowStart.AddSeconds(1), [200] = WindowStart.AddSeconds(2),
        [300] = WindowStart.AddSeconds(3), [400] = WindowStart.AddMinutes(-5), [500] = WindowStart.AddMinutes(-1),
    };

    private static bool Mine(int id) => id != 300;
    private static DateTime? Start(int id) => Starts.TryGetValue(id, out var start) ? start : null;
    private static bool Expected(int id, int? window = 100) => EndpointOwners.Expected(id, window, Parents, Mine, Start);

    private static List<TcpListenerEntry> Listening(params (string Address, int Port, int Id)[] rows)
        => rows.Select(row => new TcpListenerEntry(IPAddress.Parse(row.Address), row.Port, row.Id)).ToList();

    [Test]
    public void The_window_its_children_and_this_users_later_processes_may_hold_the_port()
    {
        Check.True(Expected(100), "the Window");
        Check.True(Expected(101), "WebView2's browser, the Window's child");
        Check.True(Expected(200), "CS-WebUI's browser, started through cmd /c START after the Window");
        Check.True(!Expected(300), "another user's process");
        Check.True(!Expected(400), "this user's process from before the Window");
        Check.True(!Expected(500), "a process older than the Window that names its ID as parent");
        Check.True(!Expected(999), "a process without a start time");
        Check.True(Expected(400, window: null), "without a Window, any process of this user");
        Check.True(!Expected(300, window: null), "another user, without a Window");
    }

    [Test]
    public void Only_listeners_that_127_0_0_1_reaches_count()
    {
        var owned = EndpointOwners.Check(9225, Listening(("127.0.0.1", 9225, 101), ("192.168.1.2", 9225, 300), ("127.0.0.1", 9226, 300)), id => Expected(id));
        Check.Equal(EndpointOwnerVerdict.Owned, owned.Verdict);

        var squatted = EndpointOwners.Check(9225, Listening(("0.0.0.0", 9225, 300), ("127.0.0.1", 9225, 101)), id => Expected(id));
        Check.Equal(EndpointOwnerVerdict.Foreign, squatted.Verdict);
        Check.Same(new[] { 300 }, squatted.Foreign);
        Check.True(squatted.Refusal("http://127.0.0.1:9225/").Contains("process 300"), squatted.Refusal("http://127.0.0.1:9225/"));

        Check.Equal(EndpointOwnerVerdict.NotListening, EndpointOwners.Check(9225, Listening(("127.0.0.1", 9226, 101)), id => Expected(id)).Verdict);
        Check.Equal(EndpointOwnerVerdict.Unknown, EndpointOwners.Check(9225, null, id => Expected(id)).Verdict);
    }

    [Test]
    public void On_Windows_the_listener_table_names_this_process_and_other_accounts()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            Check.Equal(EndpointOwnerVerdict.Unknown, EndpointOwners.CheckNow(9225, null).Verdict);
            return;
        }
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            Check.True(EndpointOwners.ListenersNow()!.Any(item => item.Port == port && item.ProcessId == self && item.Address.Equals(IPAddress.Loopback)), "GetExtendedTcpTable lists this process's 127.0.0.1 listener");
            Check.Equal(EndpointOwnerVerdict.Owned, EndpointOwners.CheckNow(port, self).Verdict, "the Window itself");
            Check.Equal(EndpointOwnerVerdict.Owned, EndpointOwners.CheckNow(port, null).Verdict, "this user's process");
            // RPC's endpoint mapper (0.0.0.0:135) runs as NETWORK SERVICE on every Windows: another account.
            var rpc = EndpointOwners.CheckNow(135, null);
            Check.Equal(EndpointOwnerVerdict.Foreign, rpc.Verdict, "a service account's listener");
        }
        finally { listener.Stop(); }
        Check.Equal(EndpointOwnerVerdict.NotListening, EndpointOwners.CheckNow(1, null).Verdict);
    }
}
