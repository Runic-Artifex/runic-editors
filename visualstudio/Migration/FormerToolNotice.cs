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
/// <c>offerMigration</c>), but only where <c>RunicArtifex.Tool</c>, whose <c>migrate</c> ships with the rename, is
/// available: a manifest lists it, or <c>dotnet rax --version</c> / <c>rax --version</c> runs. Before the rename ships
/// nothing shows. It runs <c>rax migrate</c> as a dry run first, then on confirmation.
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
            string dotnet = await RunicSdkState.DotnetAsync(cancellationToken);
            await TaskScheduler.Default;
            var manifests = MigrationOffer.SolutionManifests(RunicWorkspace.Detect(directory), directory);
            MigrateRunner? resolved = null;
            if (MigrationOffer.ProbeDirectory(manifests) is { } probe)
                resolved = await ResolvesAsync(dotnet, probe, globalRax: false, cancellationToken) ? new MigrateRunner(probe, globalRax: false)
                    : await ResolvesAsync(dotnet, probe, globalRax: true, cancellationToken) ? new MigrateRunner(probe, globalRax: true) : null;
            var offer = MigrationOffer.For(manifests, resolved);
            if (offer == null || Interlocked.Exchange(ref shown, 1) == 1) return;
            SdkOutput.WriteLine(offer.Message + " (" + string.Join(", ", offer.Manifests) + ")");
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await package.GetServiceAsync(typeof(SVsShell)) is not IVsShell shell
                || await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) return;
            if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object value)) || value is not IVsInfoBarHost host) return;
            var actions = new IVsInfoBarActionItem[] { new InfoBarButton(Migrate), new InfoBarHyperlink(Guide) };
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

    /// <summary>Whether <c>dotnet rax --version</c> (or a global <c>rax --version</c>) runs in <paramref name="directory"/>.</summary>
    private static async Task<bool> ResolvesAsync(string dotnet, string directory, bool globalRax, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ToolProcess.RunAsync(globalRax ? "rax" : dotnet, RunicTools.RaxVersionArguments(globalRax), directory, TimeSpan.FromSeconds(15), cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary><c>rax migrate --dry-run --json</c>, the report in the Runic SDK pane, then the run on confirmation.</summary>
    private static async Task MigrateAsync(AsyncPackage package, MigrateRunner runner)
    {
        string dotnet = await RunicSdkState.DotnetAsync(package.DisposalToken);
        string program = runner.GlobalRax ? "rax" : dotnet, directory = runner.Directory;
        await TaskScheduler.Default;
        async Task<string?> RunAsync(bool dryRun)
        {
            var arguments = RunicTools.MigrateArguments(dryRun, runner.GlobalRax);
            SdkOutput.WriteLine("> " + program + " " + string.Join(" ", arguments) + "  (in " + directory + ")");
            ToolProcessResult result;
            try { result = await ToolProcess.RunAsync(program, arguments, directory, TimeSpan.FromMinutes(5), package.DisposalToken); }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { SdkOutput.WriteLine(error.Message); return null; }
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
            VsShellUtilities.ShowMessageBox(package, "rax migrate --dry-run failed; the Runic SDK output pane has its output.", RunicPackage.Title,
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            return;
        }
        if (VsShellUtilities.ShowMessageBox(package, "rax migrate --dry-run: " + MigrationOffer.Summary(plan) + ". The full report is in the Runic SDK output pane. Rewrite the project now?",
                RunicPackage.Title, OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) != (int)VSConstants.MessageBoxResult.IDOK) return;
        await TaskScheduler.Default;
        string? done = await RunAsync(dryRun: false);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        VsShellUtilities.ShowMessageBox(package, done != null ? "rax migrate: " + MigrationOffer.Summary(done) + ". Review the changes, then reload the solution." : "rax migrate failed; the Runic SDK output pane has its output.",
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
            if (actionItem.Text != Migrate) return;
            infoBarUIElement.Close();
            package.JoinableTaskFactory.RunAsync(() => MigrateAsync(package, offer.Runner)).FileAndForget("RunicDevKit/Migrate");
        }
    }
}
