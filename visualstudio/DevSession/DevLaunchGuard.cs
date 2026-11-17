using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// The pre-F5 guard (PARITY §2.2, D070.6). Visual Studio has no <c>preLaunchTask</c>: when F5 or Ctrl+F5 builds a
/// startup project that is a Runic SDK project and no <c>dotnet runic dev --no-window</c> (owner <c>ide</c>) runs, the
/// guard asks first: Yes starts <c>dev --no-window</c> (or restarts a <c>dev</c> this extension started with its own
/// Window), cancels this launch, waits until <c>dev</c> is ready and starts the same launch again, as VS Code's
/// <c>runic</c> debug type waits for <c>[dev] Ready</c> before it builds; No continues without it; Cancel cancels the
/// launch. VS Code asks nothing and remembers nothing, so the guard asks each time (<see cref="DevGuard"/>).
/// </summary>
internal sealed class DevLaunchGuard : IVsUpdateSolutionEvents2, IVsUpdateSolutionEvents4
{
    private const uint LaunchDebug = (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_LAUNCHDEBUG;
    private const uint Launch = (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_LAUNCH;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(10);

    private static DevLaunchGuard? instance;
    private readonly RunicPackage package;
    private uint solutionAction;
    private bool handled;

    private DevLaunchGuard(RunicPackage package) { this.package = package; }

    public static async Task AdviseAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (instance != null) return;
        if (await package.GetServiceAsync(typeof(SVsSolutionBuildManager)) is not IVsSolutionBuildManager2 builds)
        {
            SdkOutput.WriteLine("The solution build manager is unavailable; F5 will not offer to start dotnet runic dev --no-window.");
            return;
        }
        instance = new DevLaunchGuard(package);
        ErrorHandler.ThrowOnFailure(builds.AdviseUpdateSolutionEvents(instance, out uint cookie));
        package.DisposalToken.Register(() => _ = RunicSdkState.Background.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            builds.UnadviseUpdateSolutionEvents(cookie);
        }));
    }

    public int UpdateSolution_Begin(ref int pfCancelUpdate)
    {
        handled = false;
        return VSConstants.S_OK;
    }

    /// <summary>The solution-level action of this update; it carries the launch flags of F5 and Ctrl+F5.</summary>
    public void UpdateSolution_BeginUpdateAction(uint dwAction) => solutionAction = dwAction;

    public void UpdateSolution_EndUpdateAction(uint dwAction) => solutionAction = 0;

    public int UpdateProjectCfg_Begin(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, ref int pfCancel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        uint action = dwAction | solutionAction;
        bool debug = (action & LaunchDebug) != 0;
        if (handled || (!debug && (action & Launch) == 0)) return VSConstants.S_OK;
        try
        {
            if (pHierProj is not IVsProject project || ErrorHandler.Failed(project.GetMkDocument((uint)VSConstants.VSITEMID.Root, out string file)) || string.IsNullOrEmpty(file)) return VSConstants.S_OK;
            if (!RunicSdkState.Instance.Projects.Any(state => string.Equals(state.Project, file, StringComparison.OrdinalIgnoreCase))) return VSConstants.S_OK;
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (!DevSessionService.StartupProjects(dte).Contains(file, StringComparer.OrdinalIgnoreCase)) return VSConstants.S_OK;
            handled = true;
            if (Ask(file, debug)) pfCancel = 1;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SdkOutput.WriteLine("The F5 check for dotnet runic dev failed: " + error.Message);
        }
        return VSConstants.S_OK;
    }

    /// <summary>Asks, and returns true when this launch is cancelled (Cancel, or Yes, which relaunches once <c>dev</c> is ready).</summary>
    private bool Ask(string project, bool debug)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var service = DevSessionService.Instance;
        var decision = DevGuard.Decide(service.Refresh(project));
        if (decision is DevGuardDecision.Launch or DevGuardDecision.LaunchWhileStarting) return false;
        bool startedHere = service.StartedHere(project);
        int answer = VsShellUtilities.ShowMessageBox(package, DevGuard.Question(project, decision, startedHere), RunicPackage.Title,
            OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNOCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        if (answer == (int)VSConstants.MessageBoxResult.IDCANCEL) return true;
        if (answer != (int)VSConstants.MessageBoxResult.IDYES) return false;
        // dev runs its own Window and was started elsewhere: Yes, like No, continues.
        if (decision == DevGuardDecision.AskToRestart && !startedHere) return false;
        _ = package.JoinableTaskFactory.RunAsync(() => StartThenRelaunchAsync(project, debug, package.DisposalToken));
        return true;
    }

    private async Task StartThenRelaunchAsync(string project, bool debug, CancellationToken cancellationToken)
    {
        var service = DevSessionService.Instance;
        try
        {
            await SetStatusAsync($"Runic: starting dotnet runic dev --no-window for {Path.GetFileName(project)}…");
            // The user already chose to restart: no second question.
            await service.StartAsync(project, noWindow: true, _ => Task.FromResult(true), cancellationToken);
            await service.WaitForAsync(project, snapshot => snapshot.IsReadyForIde, ReadyTimeout,
                snapshot => service.StartedHere(project) || snapshot.IsActive ? null : "dotnet runic dev stopped; see the Runic SDK Output pane and the Error List.",
                cancellationToken);
            await SetStatusAsync($"Runic: dotnet runic dev --no-window is ready; starting {Path.GetFileName(project)}.");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) throw new InvalidOperationException("Visual Studio automation (DTE) is unavailable; start the launch again.");
            dte.ExecuteCommand(debug ? "Debug.Start" : "Debug.StartWithoutDebugging");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            SdkOutput.WriteLine("F5: " + error.Message);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await SetStatusAsync("");
            VsShellUtilities.ShowMessageBox(package, error.Message, RunicPackage.Title,
                OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    private static async Task SetStatusAsync(string text)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar bar)
        {
            bar.IsFrozen(out int frozen);
            if (frozen == 0) bar.SetText(text);
        }
    }

    public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) => VSConstants.S_OK;
    public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
    public int UpdateSolution_Cancel() => VSConstants.S_OK;
    public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;
    public int UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel) => VSConstants.S_OK;
    public void UpdateSolution_QueryDelayFirstUpdateAction(out int pfDelay) => pfDelay = 0;
    public void UpdateSolution_BeginFirstUpdateAction() { }
    public void UpdateSolution_EndLastUpdateAction() { }
    public void OnActiveProjectCfgChangeBatchBegin() { }
    public void OnActiveProjectCfgChangeBatchEnd() { }
}
