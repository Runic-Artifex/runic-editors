using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace RunicArtifex.DevKit.VisualStudio;

/// <summary>
/// DevKit's message boxes, in front. Visual Studio's message box is owned by its main window, so when the app's Window
/// (started by <c>dotnet runic dev</c> or Debug the Window) is in front of Visual Studio, the box opened behind that
/// Window and blocked Visual Studio until the Window was minimized (the 0.24 VM run, Attach to the Page). The main window
/// is brought to the front first, so its box is too.
/// </summary>
internal static class MessageBoxes
{
    public static int Show(IServiceProvider provider, string message, OLEMSGICON icon, OLEMSGBUTTON buttons = OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON defaultButton = OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        BringVisualStudioToFront(provider);
        return VsShellUtilities.ShowMessageBox(provider, message, RunicPackage.Title, icon, buttons, defaultButton);
    }

    /// <summary>
    /// Restores and activates the window that owns Visual Studio's dialogs. Windows lets a process take the foreground
    /// only while it has the input, so the call joins the foreground window's input queue for a moment, as is usual for
    /// this; a refusal leaves the window as it was.
    /// </summary>
    public static void BringVisualStudioToFront(IServiceProvider provider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (provider.GetService(typeof(SVsUIShell)) is not IVsUIShell shell || ErrorHandler.Failed(shell.GetDialogOwnerHwnd(out IntPtr owner)) || owner == IntPtr.Zero) return;
            if (IsIconic(owner)) ShowWindow(owner, RestoreCommand);
            IntPtr foreground = GetForegroundWindow();
            if (foreground == owner) return;
            uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
            uint thread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
            try { BringWindowToTop(owner); SetForegroundWindow(owner); }
            finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>Whether a modal dialog holds Visual Studio: a modal dialog disables the main window it is owned by.</summary>
    public static bool VisualStudioIsModal()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) return false;
            IntPtr main = dte.MainWindow.HWnd;
            return main != IntPtr.Zero && !IsWindowEnabled(main);
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private const int RestoreCommand = 9; // SW_RESTORE

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
