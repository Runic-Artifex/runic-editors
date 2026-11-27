using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Debugging;

/// <summary>
/// The CDP attach of SPIKE-VS-0 §3: <c>Debugger2.GetProcesses</c> on the "JavaScript and TypeScript (Chrome DevTools/V8
/// Inspector)" transport with the endpoint as qualifier, then a js-debug attach configuration for each page
/// (<see cref="JsDebugAttach"/>, runic-editors#44; <c>Process2.Attach2</c> only as the fallback). Also
/// remembers the endpoints of the Windows Debug the Window started, which the session file does not list.
/// </summary>
internal static class PageAttach
{
    /// <summary>How long a Window may take to open its endpoint and page. A paused .NET breakpoint extends it (see <see cref="WaitForPageAsync"/>).</summary>
    public static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(3);
    /// <summary>
    /// How long a Window outside Development may take: its host does not open the endpoint (runic-sdk
    /// specs/desktop/development-host-profile.md), so the wait only covers an application that sets its environment itself.
    /// </summary>
    public static readonly TimeSpan NotDevelopmentTimeout = TimeSpan.FromSeconds(20);
    /// <summary>
    /// How long one page attach may take. A broken JavaScript adapter (a stale <c>.vs</c> folder) can keep it from ever
    /// completing (runic-editors#23); DevKit then ends the adapter and says so in the Runic SDK Output pane (#44).
    /// </summary>
    public static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The Output pane's advice when Visual Studio's JavaScript debugger does not attach.</summary>
    public static string HungAttachMessage(string what) =>
        $"Visual Studio's JavaScript debugger did not answer within {AttachTimeout.TotalSeconds:0} seconds while {what}; Runic DevKit stopped waiting and ends the JavaScript debug adapter that Visual Studio started for it, and Debug the Window can run again. "
        + "If it happens again, close Visual Studio, end any node.exe running vsDebugServer.js that is left over, delete the solution's .vs folder and reopen the solution (see Troubleshooting in the Runic DevKit README).";

    private const int CallRejected = unchecked((int)0x80010001);

    private static readonly object gate = new();
    private static readonly List<(string Project, PageEndpoint Endpoint, int ProcessId)> launched = new();

    /// <summary>Remembers a Window's endpoint while its process runs.</summary>
    public static void Remember(string project, PageEndpoint endpoint, int processId)
    {
        lock (gate) launched.Add((project, endpoint, processId));
    }

    /// <summary>The endpoints of running Windows Debug the Window started for the project.</summary>
    public static IReadOnlyList<PageEndpoint> Launched(string project)
    {
        lock (gate)
        {
            launched.RemoveAll(item => !IsRunning(item.ProcessId));
            return launched.Where(item => string.Equals(item.Project, project, StringComparison.OrdinalIgnoreCase)).Select(item => item.Endpoint).ToList();
        }
    }

    /// <summary>The process IDs of running Windows Debug the Window started with a page of <paramref name="engine"/> (Break All, runic-editors#43).</summary>
    public static IReadOnlyList<int> LaunchedWindows(string engine)
    {
        lock (gate)
        {
            launched.RemoveAll(item => !IsRunning(item.ProcessId));
            return launched.Where(item => string.Equals(item.Endpoint.Engine, engine, StringComparison.OrdinalIgnoreCase)).Select(item => item.ProcessId).Distinct().ToList();
        }
    }

    public static bool IsRunning(int processId)
    {
        if (processId <= 0) return false;
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Whether Visual Studio runs elevated; WebView2 then ignores <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> in the Window it starts.</summary>
    public static bool IsElevated()
    {
        try { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Whether <c>/json/version</c> answers as a DevTools endpoint.</summary>
    public static async Task<bool> AnswersAsync(string url, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;
        try
        {
            var request = WebRequest.CreateHttp(CdpEndpoints.VersionUrl(url));
            request.Timeout = 2000; request.ReadWriteTimeout = 2000; request.Proxy = null; request.KeepAlive = false;
            // GetResponseAsync ignores Timeout: abort the request after it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using (timeout.Token.Register(request.Abort))
            using (var response = await request.GetResponseAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(response.GetResponseStream()))
                return CdpEndpoints.IsVersionResponse(await reader.ReadToEndAsync().ConfigureAwait(false));
        }
        catch (Exception error) when (error is WebException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>
    /// Waits until the endpoint answers and the transport lists a page, then attaches the JavaScript engine to each page.
    /// Gives up after <see cref="PageTimeout"/> of the Window running, not counting time the debugger spends in break mode
    /// or Visual Studio shows a modal dialog (such as its Release "Just My Code" warning, which holds the Window before it
    /// creates its page), or when <paramref name="stillRunning"/> turns false; <paramref name="status"/> reports progress.
    /// With <paramref name="notDevelopment"/> (why the Window, outside Development, opens no endpoint) it gives up after
    /// <see cref="NotDevelopmentTimeout"/> and says that instead.
    /// Returns the attached pages' names.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WaitForPageAsync(string url, int? debuggedProcess, Func<bool>? stillRunning, Func<string, Task> status, CancellationToken cancellationToken, Action? attachHung = null, string? notDevelopment = null)
    {
        var limit = notDevelopment != null ? NotDevelopmentTimeout : PageTimeout;
        var settled = System.Diagnostics.Stopwatch.StartNew();
        // Wall-clock time of the Window running; an iteration that ends in break mode does not count.
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(500);
        bool answered = false;
        string? lastListing = null;
        while (true)
        {
            var iteration = System.Diagnostics.Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            if (stillRunning != null && !stillRunning()) throw new InvalidOperationException($"The Window exited before its page's DevTools endpoint {url} answered.{(notDevelopment != null ? " " + notDevelopment : "")}");
            if (!answered && await AnswersAsync(url, cancellationToken))
            {
                answered = true;
                await status($"Runic: attaching the JavaScript debugger to {url}…");
            }
            // Attach2 while the debugger is still starting the launched process can hang (VS 18.12): wait until it
            // runs that process, in run mode, for a moment.
            if (debuggedProcess is { } id && !await RunsAsync(id)) settled.Restart();
            if (answered && settled.Elapsed >= SettleTime)
            {
                var attached = await TryAttachAsync(url, debuggedProcess, cancellationToken, listing =>
                {
                    if (listing != lastListing) SdkOutput.WriteLine("Debug the Window: " + listing);
                    lastListing = listing;
                }, attachHung);
                if (attached.Count > 0) return attached;
            }
            await Task.Delay(step, cancellationToken);
            if (!await PausedAsync()) waited += iteration.Elapsed;
            if (waited >= limit && notDevelopment != null && !answered) throw new TimeoutException(notDevelopment);
            if (waited >= limit)
                throw new TimeoutException(answered
                    ? $"The DevTools endpoint {url} answered, but showed no page within {Math.Round(PageTimeout.TotalMinutes)} minutes. Use Attach to the Page once the Window shows its page."
                    : $"The page's DevTools endpoint {url} did not answer within {Math.Round(PageTimeout.TotalMinutes)} minutes of the Window running.{(IsElevated() ? " Visual Studio runs as administrator, so WebView2 ignores the port; run it without elevation." : "")} The .NET debugger stays attached; use Attach to the Page once the Window shows its page.");
        }
    }

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    /// <summary>Whether the debugger is in run mode and debugs the process.</summary>
    private static async Task<bool> RunsAsync(int processId)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte || dte.Debugger.CurrentMode != EnvDTE.dbgDebugMode.dbgRunMode) return false;
            foreach (EnvDTE.Process process in dte.Debugger.DebuggedProcesses)
                if (process.ProcessID == processId) return true;
            return false;
        }
        catch (COMException) { return false; }
    }

    /// <summary>
    /// Whether the Window is held: the debugger is in break mode, or Visual Studio shows a modal dialog. The Release run of
    /// the 0.24 VM checks timed out while Visual Studio's "Just My Code" warning waited for Continue; the Window created its
    /// WebView2, and the port, only after it.
    /// </summary>
    private static async Task<bool> PausedAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte && dte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode) return true;
            return MessageBoxes.VisualStudioIsModal();
        }
        catch (COMException) { return false; }
    }

    /// <summary>
    /// One attempt: lists the endpoint's targets on the CDP transport and attaches to the pages; empty when none is listed
    /// yet. <paramref name="report"/> receives what the endpoint listed, for the Output pane. Refuses an endpoint whose
    /// port the Window (<paramref name="windowProcessId"/>, when known) does not hold (<see cref="VerifyOwnerAsync"/>).
    /// </summary>
    /// <remarks>
    /// <c>Debugger2.GetProcesses</c> (which starts Visual Studio's JavaScript debug adapter) and <c>Process2.Attach2</c>
    /// block Visual Studio's UI thread until the adapter answers, and cannot be cancelled. When one has not returned within
    /// <see cref="AttachTimeout"/>, or Visual Studio starts closing meanwhile, DevKit ends this Visual Studio's adapter so it
    /// returns (<see cref="HungAttach"/>, runic-editors#23 and #44); <paramref name="attachHung"/> then runs on the thread
    /// pool, after the Output pane says so.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> TryAttachAsync(string url, int? windowProcessId, CancellationToken cancellationToken, Action<string>? report = null, Action? attachHung = null)
    {
        // Adapters that an exited Visual Studio left running (runic-editors#44).
        await TaskScheduler.Default;
        JsAdapterProcesses.EndOrphansOnce("JavaScript debugger");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE80.DTE2 dte) throw new InvalidOperationException("Visual Studio automation (DTE) is unavailable.");
        var debugger = (EnvDTE80.Debugger2)dte.Debugger;
        EnvDTE80.Transport? transport = null;
        foreach (EnvDTE80.Transport candidate in debugger.Transports)
            if (string.Equals(candidate.ID, DebugEngines.CdpTransport, StringComparison.OrdinalIgnoreCase)) { transport = candidate; break; }
        if (transport == null)
            throw new InvalidOperationException("Visual Studio has no \"JavaScript and TypeScript (Chrome DevTools/V8 Inspector)\" debugger transport. Add the \"JavaScript diagnostics\" component (Microsoft.VisualStudio.Component.JavaScript.Diagnostics) in the Visual Studio Installer.");
        var attached = new List<string>();
        // Only 127.0.0.1, never localhost, which can resolve to [::1] (DEV-SEC-82); and only while the Window holds the port (DEV-SEC-83).
        string qualifier = CdpEndpoints.Url(new Uri(url).Port);
        await VerifyOwnerAsync(url, windowProcessId, cancellationToken);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        EnvDTE.Processes processes;
        try { processes = await GuardedAsync($"listing the targets of {qualifier}", () => { ThreadHelper.ThrowIfNotOnUIThread(); return debugger.GetProcesses(transport, qualifier); }, attachHung, cancellationToken); }
        catch (Exception error) when (error is COMException or ArgumentException) { report?.Invoke($"{qualifier}: {error.Message}"); return attached; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var all = processes.OfType<EnvDTE80.Process2>().ToList();
        var pages = all.Where(process => { ThreadHelper.ThrowIfNotOnUIThread(); return CdpEndpoints.IsPageTarget(process.Name); }).ToList();
        report?.Invoke($"{qualifier}: {(all.Count == 0 ? "no targets" : string.Join(", ", all.Select(process => { ThreadHelper.ThrowIfNotOnUIThread(); return process.Name; })))}");
        foreach (var page in pages)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            string name = page.Name;
            SdkOutput.WriteLine($"Attaching the JavaScript debugger to {name} ({qualifier}).");
            await AttachPageAsync(dte, page, name, qualifier, attachHung, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            attached.Add(name);
        }
        return attached;
    }

    /// <summary>
    /// Refuses the attach unless the processes listening on the endpoint's port are the Window's page or this user's
    /// processes started after the Window (<see cref="EndpointOwners"/>, DEV-SEC-83). Another user's program can take a
    /// planned port first, or bind a port the browser has released.
    /// </summary>
    public static async Task VerifyOwnerAsync(string url, int? windowProcessId, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;
        cancellationToken.ThrowIfCancellationRequested();
        var check = EndpointOwners.CheckNow(new Uri(url).Port, windowProcessId is > 0 ? windowProcessId : null);
        if (check.Verdict == EndpointOwnerVerdict.Owned) return;
        throw new EndpointNotOwnedException(check.Refusal(url));
    }

    /// <summary>
    /// Attaches the JavaScript debugger to the page with a js-debug attach configuration (<see cref="JsDebugAttach"/>),
    /// which <c>IVsDebugger4.LaunchDebugTargets4</c> starts without waiting for it, then waits off the UI thread until the
    /// debugger lists the page. When the attach has not completed within <see cref="AttachTimeout"/>, or Visual Studio
    /// starts closing meanwhile, DevKit ends the adapter it started (<see cref="HungAttach"/>). When Visual Studio refuses
    /// that launch, it falls back to <c>Process2.Attach2</c>, which waits in a nested message loop.
    /// </summary>
    private static async Task AttachPageAsync(EnvDTE80.DTE2 dte, EnvDTE80.Process2 page, string name, string qualifier, Action? attachHung, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsShellDebugger)) is not IVsDebugger4 debugger)
        {
            await GuardedAsync($"attaching to {name}", () => { ThreadHelper.ThrowIfNotOnUIThread(); page.Attach2(DebugEngines.JavaScriptOverCdp); return true; }, attachHung, cancellationToken);
            return;
        }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        int before = DebuggedCount(dte);
        string what = $"attaching to {name}";
        using (HungAttach.Begin(what, JsAdapterProcesses.NodeIdsNow()))
        using (var watchdog = Watchdog.Start(AttachTimeout, () =>
        {
            // Recovery first: writing to the Output pane marshals to the UI thread, which a stuck adapter can block.
            HungAttach.Recover($"No answer within {AttachTimeout.TotalSeconds:0} seconds");
            attachHung?.Invoke();
            SdkOutput.WriteLine(HungAttachMessage(what));
        }))
        {
            var target = new VsDebugTargetInfo4
            {
                dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                bstrExe = name,
                bstrCurDir = Path.GetTempPath(),
                bstrOptions = JsDebugAttach.Options(new Uri(qualifier).Port, name),
                guidLaunchDebugEngine = new Guid(DebugEngines.JavaScript),
                fSendToOutputWindow = true,
            };
            try { await RetryAsync(() => { ThreadHelper.ThrowIfNotOnUIThread(); debugger.LaunchDebugTargets4(1, new[] { target }, new VsDebugTargetProcessInfo[1]); return true; }, cancellationToken); }
            catch (Exception error) when (error is COMException or ArgumentException or NotImplementedException)
            {
                SdkOutput.WriteLine($"Visual Studio did not start the js-debug attach to {name} ({error.Message}); attaching through the debugger transport instead.");
                watchdog.Dispose();
                await GuardedAsync(what, () => { ThreadHelper.ThrowIfNotOnUIThread(); page.Attach2(DebugEngines.JavaScriptOverCdp); return true; }, attachHung, cancellationToken);
                return;
            }
            // The debugger lists the page's program once js-debug has attached.
            while (true)
            {
                await Task.Delay(250, cancellationToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (DebuggedCount(dte) > before) return;
                if (watchdog.Expired || VsClosing.IsClosing)
                    throw new HungAttachEndedException($"Visual Studio's JavaScript debugger did not attach to {name}; Runic DevKit ended its adapter. The .NET debugger stays attached; use Attach to the Page to try again.", new TimeoutException());
            }
        }
    }

    private static int DebuggedCount(EnvDTE80.DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return dte.Debugger.DebuggedProcesses.Count; }
        catch (COMException) { return 0; }
    }

    /// <summary>
    /// Runs a debugger call that can block the UI thread forever (<see cref="TryAttachAsync"/>) under the watchdog: after
    /// <see cref="AttachTimeout"/>, or when Visual Studio starts closing, this Visual Studio's adapter is ended. A failure
    /// after that is a <see cref="HungAttachEndedException"/>, which callers report in the Output pane only.
    /// </summary>
    private static async Task<T> GuardedAsync<T>(string what, Func<T> call, Action? attachHung, CancellationToken cancellationToken)
    {
        var before = JsAdapterProcesses.NodeIdsNow();
        using (HungAttach.Begin(what, before))
        using (var watchdog = Watchdog.Start(AttachTimeout, () =>
        {
            // Recovery first: writing to the Output pane marshals to the UI thread, which the hung call may block.
            HungAttach.Recover($"No answer within {AttachTimeout.TotalSeconds:0} seconds");
            attachHung?.Invoke();
            SdkOutput.WriteLine(HungAttachMessage(what));
        }))
        {
            try
            {
                var result = await RetryAsync(call, cancellationToken);
                if (watchdog.Expired) SdkOutput.WriteLine($"Visual Studio's JavaScript debugger returned from {what} after Runic DevKit stopped waiting.");
                return result;
            }
            catch (Exception error) when ((watchdog.Expired || VsClosing.IsClosing) && error is COMException or ArgumentException or InvalidOperationException)
            {
                throw new HungAttachEndedException($"Visual Studio's JavaScript debugger failed {what} after Runic DevKit ended its adapter ({error.Message}). The .NET debugger stays attached; use Attach to the Page to try again.", error);
            }
        }
    }

    /// <summary>The debugger rejects calls while it is busy (<c>RPC_E_CALL_REJECTED</c>); try again for a few seconds.</summary>
    private static async Task<T> RetryAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            try { return call(); }
            catch (COMException error) when (error.HResult == CallRejected && attempt < 20) { }
            await Task.Delay(250, cancellationToken);
        }
    }

    public static async Task SetStatusAsync(string text)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
        {
            bar.IsFrozen(out int frozen);
            if (frozen == 0) bar.SetText(text);
        }
    }
}

/// <summary>The endpoint's port is held by a process that is not the Window's (DEV-SEC-83); the attach stops.</summary>
internal sealed class EndpointNotOwnedException : InvalidOperationException
{
    public EndpointNotOwnedException(string message) : base(message) { }
}
