using System;
using System.Collections.Generic;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// Visual Studio's "Break all processes when one process breaks" (Tools → Options → Debugging → General, DTE
/// <c>Debugging.General.BreakAllProcesses</c>, on by default) while DevKit debugs a WebView2 page together with its .NET
/// Window. With it on, a page breakpoint makes the CoreCLR engine stop the Window too. The Window's UI thread owns the
/// window that WebView2's browser process puts its own window in, so the browser process then blocks on that stopped
/// thread, the page's DevTools Protocol endpoint stops answering, and the JavaScript engine never finishes the break:
/// Visual Studio waits on "a debugger operation is taking a long time" until the Window ends (runic-editors checklist
/// row 10, 0.22). DevKit turns the option off for such a session and on again when debugging stops.
/// </summary>
/// <remarks>
/// The option is read and written through <paramref name="read"/> and <paramref name="write"/>; <paramref name="pending"/>
/// and <paramref name="setPending"/> keep a marker in Visual Studio's settings that survives a Visual Studio that ends while
/// the option is off, so the next one turns it on again (<see cref="Recover"/>).
/// </remarks>
public sealed class BreakAllProcessesGuard
{
    private readonly Func<bool> read;
    private readonly Action<bool> write;
    private readonly Func<bool> pending;
    private readonly Action<bool> setPending;

    public BreakAllProcessesGuard(Func<bool> read, Action<bool> write, Func<bool> pending, Action<bool> setPending)
    {
        this.read = read; this.write = write; this.pending = pending; this.setPending = setPending;
    }

    /// <summary>Whether this Visual Studio turned the option off and has not turned it on again.</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>Whether a session with a page of this engine needs the option off: WebView2's (<see cref="PageEndpoint.Engine"/>).</summary>
    public static bool AppliesTo(string? engine) => string.Equals(engine, "webview2", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Output pane's line when <see cref="Suspend"/> turned the option off.</summary>
    public const string SuspendedMessage =
        "Turned off Tools > Options > Debugging > General > \"Break all processes when one process breaks\" while this debugging session runs: "
        + "with it on, a breakpoint in the page also stops the .NET Window, WebView2 then stops answering the JavaScript debugger, and Visual Studio hangs. "
        + "Runic DevKit turns it on again when debugging stops.";

    /// <summary>The Output pane's line when <see cref="Restore"/> or <see cref="Recover"/> turned the option on again.</summary>
    public const string RestoredMessage = "Turned \"Break all processes when one process breaks\" on again.";

    /// <summary>
    /// Turns the option off for a session that debugs a WebView2 page; true when it was on. An option that is off
    /// already, by the user or by an earlier call, stays as it is.
    /// </summary>
    public bool Suspend()
    {
        if (!read()) return false;
        setPending(true); // before the change, so a Visual Studio that ends now still has it turned on again
        write(false);
        IsSuspended = true;
        return true;
    }

    /// <summary>
    /// After the session ends: turns the option on again if this Visual Studio turned it off; true when it did. If the
    /// user turned it on meanwhile, it stays on.
    /// </summary>
    public bool Restore()
    {
        if (!IsSuspended) return false;
        IsSuspended = false;
        setPending(false);
        if (read()) return false;
        write(true);
        return true;
    }

    /// <summary>
    /// When DevKit loads: turns the option on again if a Visual Studio ended while DevKit had it off (the marker is set);
    /// true when it did.
    /// </summary>
    public bool Recover()
    {
        if (IsSuspended || !pending()) return false;
        setPending(false);
        if (read()) return false;
        write(true);
        return true;
    }
}

/// <summary>
/// Debug → Break All while Debug the Window debugs a WebView2 page (runic-editors#43, D077.6). Break All stops the .NET
/// Window and the page. The Window's stopped UI thread blocks WebView2's browser process, so the page's part of the break
/// cannot finish and Visual Studio repeats "The script document cannot be opened". DevKit's priority command target turns
/// Break All into a break of the CoreCLR process alone; the page keeps running, and its breakpoints still stop it.
/// </summary>
public static class BreakAllCommand
{
    /// <summary>Debug.BreakAll's command set (guidVSStd97).</summary>
    public static readonly Guid CommandSet = DevGuard.LaunchCommandSet;
    /// <summary>Debug.BreakAll (Ctrl+Alt+Break): command 180 of guidVSStd97, as DTE's <c>Commands.Item("Debug.BreakAll")</c> reports it in Visual Studio 18.12.</summary>
    public const uint BreakAll = 180;

    public static bool Is(Guid commandSet, uint command) => commandSet == CommandSet && command == BreakAll;

    /// <summary>
    /// The process Break All should stop instead: a WebView2 Window that Debug the Window started and the debugger runs,
    /// while it also debugs another process (the page). Null passes Break All on unchanged: no such Window, or only the
    /// Window is debugged (its page did not attach), when Break All stops nothing else anyway.
    /// </summary>
    public static int? Target(IReadOnlyCollection<int> debuggedProcessIds, IReadOnlyCollection<int> webView2Windows)
    {
        if (debuggedProcessIds.Count < 2) return null;
        foreach (int window in webView2Windows)
            if (debuggedProcessIds.Contains(window)) return window;
        return null;
    }

    /// <summary>The Runic SDK Output pane's line when Break All stopped only the Window.</summary>
    public static string BrokeWindowOnlyMessage(int processId) =>
        $"Break All: stopped the .NET Window (process {processId}) only. The page keeps running: a stopped Window would block WebView2, and the page's part of the break would fail with \"The script document cannot be opened\" (runic-editors#43). Page breakpoints still stop the page.";
}
