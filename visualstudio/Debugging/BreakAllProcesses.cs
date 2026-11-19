using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Debugging;

/// <summary>
/// Keeps Visual Studio's "Break all processes when one process breaks" off while DevKit debugs a WebView2 page with its
/// .NET Window, and turns it on again when debugging stops (<see cref="BreakAllProcessesGuard"/> says why). The option is
/// DTE's <c>Debugging.General.BreakAllProcesses</c>; the marker that recovers it after a Visual Studio that ended
/// meanwhile is in the instance's user settings (<c>RunicDevKit\Debugging</c>).
/// </summary>
internal sealed class BreakAllProcesses : IVsDebuggerEvents
{
    private const string Collection = @"RunicDevKit\Debugging";
    private const string PendingProperty = "RestoreBreakAllProcesses";

    private static BreakAllProcesses? instance;
    private readonly BreakAllProcessesGuard guard;

    private BreakAllProcesses() => guard = new BreakAllProcessesGuard(Read, Write, ReadPending, WritePending);

    /// <summary>
    /// Before DevKit starts or joins a session with a page of <paramref name="engine"/>: turns the option off for a
    /// WebView2 page. A failure is reported in the Runic SDK Output pane; the session goes on.
    /// </summary>
    public static async Task SuspendForAsync(string engine, string source)
    {
        if (!BreakAllProcessesGuard.AppliesTo(engine)) return;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (Get().guard.Suspend()) SdkOutput.WriteLine($"{source}: {BreakAllProcessesGuard.SuspendedMessage}");
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            SdkOutput.WriteLine($"{source}: could not turn off \"Break all processes when one process breaks\" ({error.Message}). A breakpoint in the page can then hang Visual Studio; turn it off in Tools > Options > Debugging > General.");
        }
    }

    /// <summary>Turns the option on again when the debugger is in design mode (no session runs, as after a failed launch or attach).</summary>
    public static async Task RestoreIfNotDebuggingAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (instance == null || !instance.guard.IsSuspended) return;
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsShellDebugger)) is IVsDebugger debugger)
        {
            var mode = new DBGMODE[1];
            if (ErrorHandler.Succeeded(debugger.GetMode(mode)) && (mode[0] & ~DBGMODE.DBGMODE_EncMask) != DBGMODE.DBGMODE_Design) return;
        }
        instance.Restore();
    }

    /// <summary>When the package loads: turns the option on again if a Visual Studio ended while DevKit had it off.</summary>
    public static async Task RecoverAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (!ReadPending()) return; // the common case touches no DTE
            if (Get().guard.Recover()) SdkOutput.WriteLine("Debugging: " + BreakAllProcessesGuard.RestoredMessage + " (Visual Studio ended while a Runic debugging session had it off.)");
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            SdkOutput.WriteLine($"Debugging: could not turn \"Break all processes when one process breaks\" on again ({error.Message}).");
        }
    }

    private static BreakAllProcesses Get()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (instance != null) return instance;
        instance = new BreakAllProcesses();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsShellDebugger)) is IVsDebugger debugger)
            ErrorHandler.ThrowOnFailure(debugger.AdviseDebuggerEvents(instance, out _));
        return instance;
    }

    /// <summary>Debugging stopped: the session DevKit turned the option off for has ended.</summary>
    public int OnModeChange(DBGMODE dbgmodeNew)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((dbgmodeNew & ~DBGMODE.DBGMODE_EncMask) == DBGMODE.DBGMODE_Design && guard.IsSuspended) Restore();
        return VSConstants.S_OK;
    }

    private void Restore()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (guard.Restore()) SdkOutput.WriteLine("Debugging: " + BreakAllProcessesGuard.RestoredMessage);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            SdkOutput.WriteLine($"Debugging: could not turn \"Break all processes when one process breaks\" on again ({error.Message}); turn it on in Tools > Options > Debugging > General.");
        }
    }

    private static EnvDTE.Property Option()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) throw new InvalidOperationException("Visual Studio automation (DTE) is unavailable.");
        return dte.Properties["Debugging", "General"].Item("BreakAllProcesses");
    }

    private static bool Read()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Convert.ToBoolean(Option().Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Write(bool value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Option().Value = value;
    }

    private static IVsWritableSettingsStore Store()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsSettingsManager)) is not IVsSettingsManager manager) throw new InvalidOperationException("Visual Studio's settings manager is unavailable.");
        ErrorHandler.ThrowOnFailure(manager.GetWritableSettingsStore((uint)__VsSettingsScope.SettingsScope_UserSettings, out var store));
        return store;
    }

    private static bool ReadPending()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var store = Store();
        ErrorHandler.ThrowOnFailure(store.CollectionExists(Collection, out int exists));
        if (exists == 0) return false;
        ErrorHandler.ThrowOnFailure(store.GetBoolOrDefault(Collection, PendingProperty, 0, out int value));
        return value != 0;
    }

    private static void WritePending(bool value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var store = Store();
        ErrorHandler.ThrowOnFailure(store.CollectionExists(Collection, out int exists));
        if (exists == 0) ErrorHandler.ThrowOnFailure(store.CreateCollection(Collection));
        ErrorHandler.ThrowOnFailure(store.SetBool(Collection, PendingProperty, value ? 1 : 0));
    }
}
