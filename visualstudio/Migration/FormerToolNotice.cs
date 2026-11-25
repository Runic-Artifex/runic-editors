using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Output;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Migration;

/// <summary>
/// The rename (D079.13, D080.7): when the solution's tool manifests still list <c>dotnet-runic</c> or
/// <c>dotnet-runic-translations</c>, an InfoBar offers the move to <c>RunicArtifex.Tool</c> once per session (VS Code:
/// <c>offerMigration</c>). It runs <c>dotnet rax migrate</c> (dry run, then on confirmation) only where a manifest already
/// lists <c>RunicArtifex.Tool</c>, whose <c>migrate</c> ships with the rename; otherwise it links the migration guide.
/// </summary>
internal static class FormerToolNotice
{
    private const string Guide = "Open the migration guide";
    private const string Migrate = "Migrate to Runic Artifex";
    private static int shown;

    public static async Task ShowIfNeededAsync(AsyncPackage package, CancellationToken cancellationToken)
    {
        try
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await package.GetServiceAsync(typeof(SVsSolution)) is not IVsSolution solution
                || ErrorHandler.Failed(solution.GetSolutionInfo(out string directory, out _, out _)) || string.IsNullOrEmpty(directory)) return;
            await TaskScheduler.Default;
            var offer = MigrationOffer.For(RunicWorkspace.Detect(directory), directory);
            if (offer == null || Interlocked.Exchange(ref shown, 1) == 1) return;
            SdkOutput.WriteLine(offer.Message + " (" + string.Join(", ", offer.Manifests) + ")");
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await package.GetServiceAsync(typeof(SVsShell)) is not IVsShell shell
                || await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) return;
            if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object value)) || value is not IVsInfoBarHost host) return;
            var actions = offer.MigrateDirectory != null
                ? new IVsInfoBarActionItem[] { new InfoBarButton(Migrate), new InfoBarHyperlink(Guide) }
                : new IVsInfoBarActionItem[] { new InfoBarHyperlink(Guide) };
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(offer.Message) }, actions, KnownMonikers.StatusInformation, isCloseButtonVisible: true);
            var element = factory.CreateInfoBar(model);
            element.Advise(new Events(package, offer), out _);
            host.AddInfoBar(element);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The offer is advice; never fail the package over it.
            ActivityLog.TryLogWarning(RunicPackage.Title, "Could not check the solution's tool manifests: " + error.Message);
        }
    }

    /// <summary><c>dotnet rax migrate --dry-run --json</c>, the report in the Runic SDK pane, then the run on confirmation.</summary>
    private static async Task MigrateAsync(AsyncPackage package, string directory)
    {
        string dotnet = await RunicSdkState.DotnetAsync(package.DisposalToken);
        await TaskScheduler.Default;
        async Task<string?> RunAsync(bool dryRun)
        {
            var arguments = RunicTools.MigrateArguments(dryRun);
            SdkOutput.WriteLine("> " + dotnet + " " + string.Join(" ", arguments) + "  (in " + directory + ")");
            var result = await ToolProcess.RunAsync(dotnet, arguments, directory, TimeSpan.FromMinutes(5), package.DisposalToken);
            if (result.Output.Trim().Length > 0) SdkOutput.WriteLine(result.Output.Trim());
            if (result.Error.Trim().Length > 0) SdkOutput.WriteLine(result.Error.Trim());
            // Exit code 2: something is only reported, not rewritten (D079.10); the report is still on standard output.
            return result.ExitCode == 0 || (result.ExitCode == 2 && result.Output.Trim().Length > 0) ? result.Output : null;
        }
        string? plan = await RunAsync(dryRun: true);
        _ = package.JoinableTaskFactory.RunAsync(async () => await RunicOutput.ShowAsync(await RunicOutput.SdkAsync()));
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (plan == null)
        {
            VsShellUtilities.ShowMessageBox(package, "dotnet rax migrate --dry-run failed; the Runic SDK output pane has its output.", RunicPackage.Title,
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            return;
        }
        if (VsShellUtilities.ShowMessageBox(package, "rax migrate --dry-run: " + MigrationOffer.Summary(plan) + ". The full report is in the Runic SDK output pane. Rewrite the project now?",
                RunicPackage.Title, OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) != (int)VSConstants.MessageBoxResult.IDOK) return;
        await TaskScheduler.Default;
        string? done = await RunAsync(dryRun: false);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        VsShellUtilities.ShowMessageBox(package, done != null ? "rax migrate: " + MigrationOffer.Summary(done) + ". Review the changes, then reload the solution." : "dotnet rax migrate failed; the Runic SDK output pane has its output.",
            RunicPackage.Title, done != null ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    private sealed class Events : IVsInfoBarUIEvents
    {
        private readonly AsyncPackage package;
        private readonly MigrationOffer offer;
        public Events(AsyncPackage package, MigrationOffer offer) { this.package = package; this.offer = offer; }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement) { }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (actionItem.Text == Guide)
            {
                VsShellUtilities.OpenSystemBrowser(RunicTools.MigrationGuide);
                return;
            }
            if (actionItem.Text != Migrate || offer.MigrateDirectory is not { } directory) return;
            infoBarUIElement.Close();
            package.JoinableTaskFactory.RunAsync(() => MigrateAsync(package, directory)).FileAndForget("RunicDevKit/Migrate");
        }
    }
}
