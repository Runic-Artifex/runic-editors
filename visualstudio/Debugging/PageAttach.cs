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
/// Inspector)" transport with the endpoint as qualifier, then <c>Process2.Attach2</c> with its JavaScript engine. Also
/// remembers the endpoints of the Windows Debug the Window started, which the session file does not list.
/// </summary>
internal static class PageAttach
{
    /// <summary>How long a Window may take to open its endpoint and page. A paused .NET breakpoint extends it (see <see cref="WaitForPageAsync"/>).</summary>
    public static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(3);
    /// <summary>
    /// How long one <c>Process2.Attach2</c> may take. A broken JavaScript adapter (a stale <c>.vs</c> folder) makes it
    /// never return (runic-editors#23); DevKit then stops waiting and says so in the Runic SDK Output pane.
    /// </summary>
    public static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The Output pane's advice when Visual Studio's JavaScript debugger does not attach.</summary>
    public static string HungAttachMessage(string page) =>
        $"Visual Studio's JavaScript debugger did not finish attaching to {page} within {AttachTimeout.TotalSeconds:0} seconds; Runic DevKit stopped waiting, and Debug the Window can run again. "
        + "If it happens again, close Visual Studio, delete the solution's .vs folder and reopen the solution (see Troubleshooting in the Runic DevKit README).";

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
    /// Gives up after <see cref="PageTimeout"/> of the Window running, not counting time the debugger spends in break mode,
    /// or when <paramref name="stillRunning"/> turns false; <paramref name="status"/> reports progress.
    /// Returns the attached pages' names.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WaitForPageAsync(string url, int? debuggedProcess, Func<bool>? stillRunning, Func<string, Task> status, CancellationToken cancellationToken, Action? attachHung = null)
    {
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
            if (stillRunning != null && !stillRunning()) throw new InvalidOperationException($"The Window exited before its page's DevTools endpoint {url} answered.");
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
                var attached = await TryAttachAsync(url, cancellationToken, listing =>
                {
                    if (listing != lastListing) SdkOutput.WriteLine("Debug the Window: " + listing);
                    lastListing = listing;
                }, attachHung);
                if (attached.Count > 0) return attached;
            }
            await Task.Delay(step, cancellationToken);
            if (!await InBreakModeAsync()) waited += iteration.Elapsed;
            if (waited >= PageTimeout)
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

    private static async Task<bool> InBreakModeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try { return ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte && dte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode; }
        catch (COMException) { return false; }
    }

    /// <summary>
    /// One attempt: lists the endpoint's targets on the CDP transport and attaches to the pages; empty when none is listed
    /// yet. <paramref name="report"/> receives what each qualifier listed, for the Output pane.
    /// </summary>
    /// <paramref name="attachHung"/> runs on the thread pool when an <c>Attach2</c> has not returned within
    /// <see cref="AttachTimeout"/> (after the Output pane says so); the call itself cannot be cancelled.
    public static async Task<IReadOnlyList<string>> TryAttachAsync(string url, CancellationToken cancellationToken, Action<string>? report = null, Action? attachHung = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE80.DTE2 dte) throw new InvalidOperationException("Visual Studio automation (DTE) is unavailable.");
        var debugger = (EnvDTE80.Debugger2)dte.Debugger;
        EnvDTE80.Transport? transport = null;
        foreach (EnvDTE80.Transport candidate in debugger.Transports)
            if (string.Equals(candidate.ID, DebugEngines.CdpTransport, StringComparison.OrdinalIgnoreCase)) { transport = candidate; break; }
        if (transport == null)
            throw new InvalidOperationException("Visual Studio has no \"JavaScript and TypeScript (Chrome DevTools/V8 Inspector)\" debugger transport. Add the \"JavaScript diagnostics\" component (Microsoft.VisualStudio.Component.JavaScript.Diagnostics) in the Visual Studio Installer.");
        var attached = new List<string>();
        var listings = new List<string>();
        foreach (string qualifier in CdpEndpoints.Qualifiers(url))
        {
            EnvDTE.Processes processes;
            try { processes = await RetryAsync(() => { ThreadHelper.ThrowIfNotOnUIThread(); return debugger.GetProcesses(transport, qualifier); }, cancellationToken); }
            catch (Exception error) when (error is COMException or ArgumentException) { listings.Add($"{qualifier}: {error.Message}"); continue; }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var all = processes.OfType<EnvDTE80.Process2>().ToList();
            var pages = all.Where(process => { ThreadHelper.ThrowIfNotOnUIThread(); return CdpEndpoints.IsPageTarget(process.Name); }).ToList();
            listings.Add($"{qualifier}: {(all.Count == 0 ? "no targets" : string.Join(", ", all.Select(process => { ThreadHelper.ThrowIfNotOnUIThread(); return process.Name; })))}");
            if (pages.Count == 0) continue;
            report?.Invoke(string.Join("; ", listings));
            foreach (var page in pages)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                string name = page.Name;
                SdkOutput.WriteLine($"Attaching the JavaScript debugger to {name} ({qualifier}).");
                using (var watchdog = Watchdog.Start(AttachTimeout, () => { SdkOutput.WriteLine(HungAttachMessage(name)); attachHung?.Invoke(); }))
                {
                    await RetryAsync(() => { ThreadHelper.ThrowIfNotOnUIThread(); page.Attach2(DebugEngines.JavaScriptOverCdp); return true; }, cancellationToken);
                    if (watchdog.Expired) SdkOutput.WriteLine($"The JavaScript debugger's attach to {name} returned after Runic DevKit stopped waiting.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                attached.Add(name);
            }
            return attached;
        }
        report?.Invoke(string.Join("; ", listings));
        return attached;
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
