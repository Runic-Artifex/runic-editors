using System;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio;

/// <summary>
/// Whether Visual Studio has started to close (runic-editors#44): the package's <c>QueryClose</c>, the shell's
/// <c>VSSPROPID_Zombie</c>, or the package's disposal. Each ends a page attach that is still waiting
/// (<see cref="Debugging.HungAttach"/>).
/// From then on, DevKit's late failures go to the Runic SDK Output pane instead of a modal message box, which would keep
/// a closing Visual Studio open.
/// </summary>
/// <remarks>A close that another package or a save prompt cancels after <c>QueryClose</c> leaves the flag set; the
/// messages then still reach the Output pane.</remarks>
internal sealed class VsClosing : IVsShellPropertyEvents
{
    private static int closing;
    private static CancellationToken disposal;

    /// <summary>The reason both hooks give, so <see cref="Debugging.HungAttach.Recover"/> acts on the first only.</summary>
    public const string Reason = "Visual Studio is closing";

    private VsClosing() { }

    public static bool IsClosing => Volatile.Read(ref closing) == 1 || disposal.IsCancellationRequested;

    /// <summary>Listens for the shell's zombie state; call on the UI thread when the package initializes.</summary>
    public static void Advise(RunicPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        disposal = package.DisposalToken;
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) is IVsShell shell) shell.AdviseShellPropertyChanges(new VsClosing(), out _);
    }

    /// <summary>Visual Studio is closing: end an attach that is waiting (once per attach, <see cref="Debugging.HungAttach.Recover"/>).</summary>
    public static void Begin(string reason)
    {
        Volatile.Write(ref closing, 1);
        Debugging.HungAttach.Recover(reason);
    }

    public int OnShellPropertyChange(int propid, object var)
    {
        if (propid == (int)__VSSPROPID.VSSPROPID_Zombie && var is bool zombie && zombie) Begin(Reason);
        return VSConstants.S_OK;
    }

    /// <summary>
    /// <see cref="VsShellUtilities.ShowMessageBox(IServiceProvider, string, string, OLEMSGICON, OLEMSGBUTTON, OLEMSGDEFBUTTON)"/>
    /// with an OK button, or, once Visual Studio is closing, a line in the Runic SDK Output pane.
    /// </summary>
    public static void ShowMessage(IServiceProvider provider, string message, OLEMSGICON icon)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (IsClosing) { SdkOutput.WriteLine(message); return; }
        VsShellUtilities.ShowMessageBox(provider, message, RunicPackage.Title, icon, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
