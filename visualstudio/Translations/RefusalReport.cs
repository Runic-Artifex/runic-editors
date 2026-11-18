using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Output;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Tells the user that DevKit or the language server refused a Translations refactoring: the main window's InfoBar
/// (one at a time, with "Show output"), the status bar and the Runic Translations Output pane. Shared by
/// <see cref="RenameRefusals"/> and <see cref="NewFolders"/>, so a second refusal replaces the first one's InfoBar.
/// </summary>
internal sealed class RefusalReport
{
    private IVsInfoBarUIElement? shown;

    public async Task ReportAsync(string message)
    {
        var pane = await RunicOutput.TranslationsAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane.OutputStringThreadSafe(message + Environment.NewLine);
        try
        {
            if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
            {
                bar.IsFrozen(out int frozen);
                if (frozen == 0) bar.SetText(message);
            }
            shown?.Close(); shown = null;
            if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell
                || Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory
                || ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object value)) || value is not IVsInfoBarHost host) return;
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(message) }, new IVsInfoBarActionItem[] { new InfoBarHyperlink(ServerStartFailure.ShowOutput) },
                KnownMonikers.StatusWarning, isCloseButtonVisible: true);
            var element = factory.CreateInfoBar(model);
            element.Advise(new Events(this, pane), out _);
            host.AddInfoBar(element);
            shown = element;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The Output pane has the message already.
            ActivityLog.TryLogWarning(RunicPackage.Title, "Could not show the Translations refusal: " + error.Message);
        }
    }

    /// <summary>A line in the Runic Translations Output pane only.</summary>
    public static void Note(string message) => _ = RunicSdkState.Background.RunAsync(async () =>
    {
        var pane = await RunicOutput.TranslationsAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane.OutputStringThreadSafe(message + Environment.NewLine);
    });

    private sealed class Events : IVsInfoBarUIEvents
    {
        private readonly RefusalReport owner;
        private readonly IVsOutputWindowPane pane;
        public Events(RefusalReport owner, IVsOutputWindowPane pane) { this.owner = owner; this.pane = pane; }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement) { if (owner.shown == infoBarUIElement) owner.shown = null; }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = RunicSdkState.Background.RunAsync(() => RunicOutput.ShowAsync(pane));
        }
    }
}
