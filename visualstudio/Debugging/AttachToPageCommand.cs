using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.DevSession;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;
using RunicArtifex.DevKit.VisualStudio.Translations;

namespace RunicArtifex.DevKit.VisualStudio.Debugging;

/// <summary>
/// Attach to the Page (JavaScript) (VS Code: <c>runic.dev.attachPage</c>): the JavaScript engine over the CDP transport
/// (SPIKE-VS-0 §3) to a running page. The endpoints are the session file's <c>debugEndpoints</c> (WebView2's, and since
/// 0.22 a CS-WebUI Window's Chromium) and those of Windows Debug the Window started; with several, the user picks one.
/// </summary>
internal sealed class AttachToPageCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.AttachToPage;

    public override void BeforeQueryStatus(OleMenuCommand command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        command.Visible = true;
        command.Enabled = RunicSdkState.Instance.Projects.Count > 0;
    }

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        var service = DevSessionService.Instance;
        string project = await service.PickAsync(null, cancellationToken);
        var snapshot = service.Refresh(project);
        var endpoints = CdpEndpoints.Distinct(PageAttach.Launched(project).Concat(snapshot.IsActive ? CdpEndpoints.FromSession(snapshot.Session) : Enumerable.Empty<PageEndpoint>()));
        SdkOutput.WriteLine($"Attach to the Page: {(endpoints.Count == 0 ? "no endpoint" : string.Join(", ", endpoints.Select(item => $"{item.Engine} {item.Url} ({item.Source})")))} for {Path.GetFileName(project)}.");
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            string reasons = string.Join(" ", (snapshot.IsActive ? snapshot.Session?.UnavailableDebugEndpoints : null)?.Select(item => $"{item.Engine} {item.Url}: {item.Reason}.") ?? Enumerable.Empty<string>());
            VsShellUtilities.ShowMessageBox(package,
                $"No page of {Path.GetFileName(project)} has a DevTools Protocol endpoint. Start it with Debug the Window, or run dotnet runic dev with its Window (Runic DevKit passes --debug-endpoint to the 0.22 tool for CS-WebUI; for WebView2 set WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222).{(reasons.Length > 0 ? " Unavailable: " + reasons : "")}",
                RunicPackage.Title, OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            return;
        }
        var endpoint = endpoints[0];
        if (endpoints.Count > 1)
        {
            var choice = ChoiceDialog.Pick("Attach to the Page", endpoints.Select(item => new Choice<PageEndpoint>(item.Url, item.Engine, item.Source, item)).ToList(), "Which page's DevTools endpoint?");
            if (choice == null) return;
            endpoint = choice.Value;
        }
        if (!await PageAttach.AnswersAsync(endpoint.Url, cancellationToken))
            throw new System.InvalidOperationException($"The DevTools endpoint {endpoint.Url} does not answer; is the Window still running?");
        try
        {
            await BreakAllProcesses.SuspendForAsync(endpoint.Engine, "Attach to the Page");
            await PageAttach.SetStatusAsync($"Runic: attaching the JavaScript debugger to {endpoint.Url}…");
            var pages = await PageAttach.TryAttachAsync(endpoint.Url, endpoint.WindowProcessId, cancellationToken, listing => SdkOutput.WriteLine("Attach to the Page: " + listing));
            if (pages.Count == 0) throw new System.InvalidOperationException($"The DevTools endpoint {endpoint.Url} shows no page yet; try again once the Window shows its page.");
            SdkOutput.WriteLine($"Attached the JavaScript debugger to {string.Join(", ", pages)} ({endpoint.Url}).");
        }
        finally
        {
            await PageAttach.SetStatusAsync("");
            await BreakAllProcesses.RestoreIfNotDebuggingAsync(); // the attach failed before a session started
        }
    }
}
