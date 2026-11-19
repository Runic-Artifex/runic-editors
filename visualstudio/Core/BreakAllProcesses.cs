using System;

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
