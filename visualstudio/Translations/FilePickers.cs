using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>The file and folder pickers of the Translations commands (VS Code's <c>showOpenDialog</c>).</summary>
internal static class FilePickers
{
    /// <summary>An existing file; null when the user cancels.</summary>
    public static string? File(string title, string? directory, string filter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        if (directory != null && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        var owner = System.Windows.Application.Current?.MainWindow;
        return (owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }

    /// <summary>A folder, through Visual Studio's folder browser; null when the user cancels.</summary>
    public static string? Folder(string title, string? directory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) is not IVsUIShell shell)
            throw new InvalidOperationException("Visual Studio shell service is unavailable.");
        const int MaxPath = 1024;
        IntPtr buffer = Marshal.AllocCoTaskMem((MaxPath + 1) * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var info = new VSBROWSEINFOW[]
            {
                new()
                {
                    lStructSize = (uint)Marshal.SizeOf(typeof(VSBROWSEINFOW)),
                    pwzDlgTitle = title,
                    pwzInitialDir = directory != null && Directory.Exists(directory) ? directory : null,
                    nMaxDirName = MaxPath,
                    pwzDirName = buffer,
                    hwndOwner = Owner(),
                },
            };
            int hr = shell.GetDirectoryViaBrowseDlg(info);
            if (hr == VSConstants.OLE_E_PROMPTSAVECANCELLED) return null;
            ErrorHandler.ThrowOnFailure(hr);
            string? folder = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(folder) ? null : folder;
        }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    private static IntPtr Owner()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) is IVsUIShell shell && ErrorHandler.Succeeded(shell.GetDialogOwnerHwnd(out IntPtr owner))
            ? owner : Process.GetCurrentProcess().MainWindowHandle;
    }
}
