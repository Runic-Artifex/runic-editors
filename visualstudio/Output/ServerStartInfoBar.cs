using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Output;

/// <summary>
/// The language server's start failure, once per solution in the main window's InfoBar (VS Code's notification,
/// <c>reportStartFailure</c>): the reason and next step (<see cref="ServerStartFailure.Message"/>), "Show output" and
/// "How to install the tool". Visual Studio's own initialization notification is off
/// (<c>ShowNotificationOnInitializeFailed</c>), so the failure is reported once.
/// </summary>
internal static class ServerStartInfoBar
{
    private static readonly OneTimeNotice Notice = new();

    /// <summary>Call on the UI thread.</summary>
    public static void Show(string solution, string message, IVsOutputWindowPane? output)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!Notice.Claim(solution)) return;
        try
        {
            if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell
                || Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) return;
            if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object value)) || value is not IVsInfoBarHost host) return;
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(message) },
                new IVsInfoBarActionItem[] { new InfoBarHyperlink(ServerStartFailure.ShowOutput), new InfoBarHyperlink(ServerStartFailure.HowToInstall) },
                KnownMonikers.StatusError, isCloseButtonVisible: true);
            var element = factory.CreateInfoBar(model);
            element.Advise(new Events(output), out _);
            host.AddInfoBar(element);
        }
        catch (Exception error)
        {
            // The Output pane already has the failure.
            ActivityLog.TryLogWarning(RunicPackage.Title, "Could not show the language server start failure: " + error.Message);
        }
    }

    private sealed class Events : IVsInfoBarUIEvents
    {
        private readonly IVsOutputWindowPane? output;

        public Events(IVsOutputWindowPane? output) { this.output = output; }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement) { }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (actionItem.Text == ServerStartFailure.HowToInstall) VsShellUtilities.OpenSystemBrowser(ServerStartFailure.InstallHelpUrl);
                else _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () => await RunicOutput.ShowAsync(output ?? await RunicOutput.TranslationsAsync()));
            }
            catch (Exception error) { ActivityLog.TryLogWarning(RunicPackage.Title, "Could not open " + actionItem.Text + ": " + error.Message); }
        }
    }
}
