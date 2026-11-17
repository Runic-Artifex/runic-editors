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
            request.Timeout = 2000; request.ReadWriteTimeout = 2000; request.Proxy = null;
            using (cancellationToken.Register(request.Abort))
            using (var response = await request.GetResponseAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(response.GetResponseStream()))
                return CdpEndpoints.IsVersionResponse(await reader.ReadToEndAsync().ConfigureAwait(false));
        }
        catch (Exception error) when (error is WebException or IOException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Waits until the endpoint answers and the transport lists a page, then attaches the JavaScript engine to each page.
    /// Gives up after <see cref="PageTimeout"/> of the Window running, not counting time the debugger spends in break mode,
    /// or when <paramref name="stillRunning"/> turns false; <paramref name="status"/> reports progress.
    /// Returns the attached pages' names.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WaitForPageAsync(string url, Func<bool>? stillRunning, Func<string, Task> status, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(500);
        bool answered = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stillRunning != null && !stillRunning()) throw new InvalidOperationException($"The Window exited before its page's DevTools endpoint {url} answered.");
            if (!answered && await AnswersAsync(url, cancellationToken))
            {
                answered = true;
                await status($"Runic: attaching the JavaScript debugger to {url}…");
            }
            if (answered)
            {
                var attached = await TryAttachAsync(url, cancellationToken);
                if (attached.Count > 0) return attached;
            }
            if (!await InBreakModeAsync()) waited += step;
            if (waited >= PageTimeout)
                throw new TimeoutException(answered
                    ? $"The DevTools endpoint {url} answered, but showed no page within {Math.Round(PageTimeout.TotalMinutes)} minutes. Use Attach to the Page once the Window shows its page."
                    : $"The page's DevTools endpoint {url} did not answer within {Math.Round(PageTimeout.TotalMinutes)} minutes of the Window running.{(IsElevated() ? " Visual Studio runs as administrator, so WebView2 ignores the port; run it without elevation." : "")} The .NET debugger stays attached; use Attach to the Page once the Window shows its page.");
            await Task.Delay(step, cancellationToken);
        }
    }

    private static async Task<bool> InBreakModeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try { return ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte && dte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgBreakMode; }
        catch (COMException) { return false; }
    }

    /// <summary>One attempt: lists the endpoint's targets on the CDP transport and attaches to the pages; empty when none is listed yet.</summary>
    public static async Task<IReadOnlyList<string>> TryAttachAsync(string url, CancellationToken cancellationToken)
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
        foreach (string qualifier in CdpEndpoints.Qualifiers(url))
        {
            EnvDTE.Processes processes;
            try { processes = await RetryAsync(() => { ThreadHelper.ThrowIfNotOnUIThread(); return debugger.GetProcesses(transport, qualifier); }, cancellationToken); }
            catch (Exception error) when (error is COMException or ArgumentException) { continue; }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var pages = processes.OfType<EnvDTE80.Process2>().Where(process => { ThreadHelper.ThrowIfNotOnUIThread(); return CdpEndpoints.IsPageTarget(process.Name); }).ToList();
            if (pages.Count == 0) continue;
            foreach (var page in pages)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                string name = page.Name;
                await RetryAsync(() => { ThreadHelper.ThrowIfNotOnUIThread(); page.Attach2(DebugEngines.JavaScriptOverCdp); return true; }, cancellationToken);
                attached.Add(name);
            }
            return attached;
        }
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
