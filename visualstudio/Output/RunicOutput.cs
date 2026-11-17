using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Output;

/// <summary>
/// The Output window panes "Runic SDK" and "Runic Translations" (VS Code's output channels). The language server's
/// standard error, start messages and trace go to the Translations pane; <c>dotnet runic</c> output goes to the SDK pane.
/// Panes are created on first use; <see cref="IVsOutputWindowPane.OutputStringThreadSafe"/> writes from any thread.
/// </summary>
internal static class RunicOutput
{
    public static Task<IVsOutputWindowPane> TranslationsAsync(CancellationToken cancellationToken = default)
        => PaneAsync(RunicOutputPanes.TranslationsGuid, RunicOutputPanes.TranslationsName, cancellationToken);

    public static Task<IVsOutputWindowPane> SdkAsync(CancellationToken cancellationToken = default)
        => PaneAsync(RunicOutputPanes.SdkGuid, RunicOutputPanes.SdkName, cancellationToken);

    /// <summary>Opens the Output window on <paramref name="pane"/>.</summary>
    public static async Task ShowAsync(IVsOutputWindowPane pane, CancellationToken cancellationToken = default)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsUIShell)) is IVsUIShell shell)
        {
            var output = VSConstants.StandardToolWindows.Output;
            if (ErrorHandler.Succeeded(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref output, out IVsWindowFrame frame)) && frame != null)
                frame.Show();
        }
        pane.Activate();
    }

    private static async Task<IVsOutputWindowPane> PaneAsync(string id, string name, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var window = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow
            ?? throw new InvalidOperationException("Visual Studio Output window is unavailable.");
        var guid = new Guid(id);
        if (ErrorHandler.Failed(window.GetPane(ref guid, out IVsOutputWindowPane pane)) || pane == null)
        {
            ErrorHandler.ThrowOnFailure(window.CreatePane(ref guid, name, fInitVisible: 1, fClearWithSolution: 0));
            ErrorHandler.ThrowOnFailure(window.GetPane(ref guid, out pane));
        }
        return pane;
    }
}
