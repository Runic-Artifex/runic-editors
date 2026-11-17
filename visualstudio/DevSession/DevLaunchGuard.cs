using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using OleConstants = Microsoft.VisualStudio.OLE.Interop.Constants;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// The pre-F5 guard (PARITY §2.2, D070.6). Visual Studio has no <c>preLaunchTask</c>: when F5 or Ctrl+F5 starts a
/// startup project that is a Runic SDK project and no <c>dotnet runic dev --no-window</c> (owner <c>ide</c>) runs, the
/// guard asks first: Yes starts <c>dev --no-window</c> (or restarts a <c>dev</c> this extension started with its own
/// Window), cancels this launch, waits until <c>dev</c> is ready and starts the same launch again, as VS Code's
/// <c>runic</c> debug type waits for <c>[dev] Ready</c> before it builds; No continues without it; Cancel cancels the
/// launch. VS Code asks nothing and remembers nothing, so the guard asks each time (<see cref="DevGuard"/>).
/// </summary>
/// <remarks>
/// The guard is a priority command target (<see cref="IVsRegisterPriorityCommandTarget"/>) for Debug.Start and
/// Debug.StartWithoutDebugging, so it sees every launch by key, toolbar, menu or <c>DTE.ExecuteCommand</c> before Visual
/// Studio builds. Until 0.22 it listened to the solution build's <c>UpdateProjectCfg_Begin</c> launch flags, which
/// Visual Studio 18.12 never raised for these launches (runic-editors#26), and a launch whose fast up-to-date check
/// skips the build raises no build event at all.
/// </remarks>
internal sealed class DevLaunchGuard : IOleCommandTarget
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(10);

    private static DevLaunchGuard? instance;
    private readonly RunicPackage package;

    private DevLaunchGuard(RunicPackage package) { this.package = package; }

    public static async Task AdviseAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (instance != null) return;
        if (await package.GetServiceAsync(typeof(SVsRegisterPriorityCommandTarget)) is not IVsRegisterPriorityCommandTarget targets)
        {
            SdkOutput.WriteLine("Visual Studio's command routing is unavailable; F5 will not offer to start dotnet runic dev --no-window.");
            return;
        }
        instance = new DevLaunchGuard(package);
        ErrorHandler.ThrowOnFailure(targets.RegisterPriorityCommandTarget(0, instance, out uint cookie));
        package.DisposalToken.Register(() => _ = RunicSdkState.Background.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            targets.UnregisterPriorityCommandTarget(cookie);
        }));
    }

    /// <summary>The guard changes no command state.</summary>
    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) => (int)OleConstants.OLECMDERR_E_NOTSUPPORTED;

    /// <summary>
    /// Runs before Visual Studio's own handler. Returning <see cref="OleConstants.OLECMDERR_E_NOTSUPPORTED"/> passes the
    /// command on; <see cref="VSConstants.S_OK"/> means the guard handled it, which cancels this launch.
    /// </summary>
    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        const int passOn = (int)OleConstants.OLECMDERR_E_NOTSUPPORTED;
        if (DevGuard.LaunchKind(pguidCmdGroup, nCmdID) is not { } debug) return passOn;
        try
        {
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            string? project = DevGuard.Target(DevSessionService.StartupProjects(dte).ToList(),
                RunicSdkState.Instance.Projects.Select(state => state.Project).ToList(), InDesignMode());
            if (project != null && Ask(project, debug)) return VSConstants.S_OK;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SdkOutput.WriteLine("The F5 check for dotnet runic dev failed: " + error.Message);
        }
        return passOn;
    }

    /// <summary>F5 in break mode continues the session; the guard only checks a launch from design mode.</summary>
    private static bool InDesignMode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsShellDebugger)) is not IVsDebugger debugger) return true;
        var mode = new DBGMODE[1];
        return ErrorHandler.Failed(debugger.GetMode(mode)) || (mode[0] & ~DBGMODE.DBGMODE_EncMask) == DBGMODE.DBGMODE_Design;
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
}
