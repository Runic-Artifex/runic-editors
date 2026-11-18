using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Output;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;
using StreamJsonRpc;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Shows why the language server refused F2 (runic-editors#32). The server answers a rename it cannot carry out with a
/// JSON-RPC error and its reason (RequestFailed in protocol 2); Visual Studio's language client passes the error to
/// this middle layer and then closes the rename box without a message. The layer reports the refusal that
/// <see cref="RenameRefusal"/> decides on in the main window's InfoBar (one at a time, with "Show output"), the status
/// bar and the Runic Translations Output pane, and lets the error continue to Visual Studio unchanged.
/// </summary>
internal sealed class RenameRefusals : ILanguageClientMiddleLayer2<JToken>
{
    private IVsInfoBarUIElement? shown;

    public bool CanHandle(string methodName) => RenameRefusal.Follows(methodName);

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification) => sendNotification(methodParam);

    public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
    {
        try { return await sendRequest(methodParam); }
        catch (Exception error) when (Describe(methodName, error) is { } message)
        {
            _ = RunicSdkState.Background.RunAsync(() => ReportAsync(message));
            throw;
        }
    }

    /// <summary>The message for the error, from the server's code and reason (the innermost RemoteInvocationException).</summary>
    private static string? Describe(string method, Exception error)
    {
        try
        {
            bool cancelled = false;
            for (Exception? current = error; current != null; current = current.InnerException)
            {
                if (current is RemoteInvocationException remote) return RenameRefusal.Message(method, remote.ErrorCode, remote.Message);
                if (current is OperationCanceledException) cancelled = true;
            }
            return RenameRefusal.Message(method, null, error.Message, cancelled);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("Runic: rename refusal: " + failure.Message);
            return null;
        }
    }

    private async Task ReportAsync(string message)
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
            // One refusal InfoBar at a time: a second F2 replaces the first one's.
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
            // The Output pane has the refusal already.
            ActivityLog.TryLogWarning(RunicPackage.Title, "Could not show the rename refusal: " + error.Message);
        }
    }

    private sealed class Events : IVsInfoBarUIEvents
    {
        private readonly RenameRefusals owner;
        private readonly IVsOutputWindowPane pane;
        public Events(RenameRefusals owner, IVsOutputWindowPane pane) { this.owner = owner; this.pane = pane; }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement) { if (owner.shown == infoBarUIElement) owner.shown = null; }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = RunicSdkState.Background.RunAsync(() => RunicOutput.ShowAsync(pane));
        }
    }
}
