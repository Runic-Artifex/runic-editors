using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Activation;

/// <summary>
/// Reads each opened solution's files with <see cref="RunicWorkspace.Detect"/> and activates
/// <see cref="RunicActivation.WorkspaceContext"/> for a Runic one, which loads <see cref="RunicPackage"/>. It covers what
/// the rule context cannot see: Translations workspaces without a C# project and Runic SDK projects of SDKs without the
/// <c>RunicApplication</c> capability. It registers no commands, windows or services.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid(RunicActivation.ProbePackage)]
public sealed class RunicWorkspaceProbe : AsyncPackage
{
    private int generation;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        KnownUIContexts.SolutionExistsAndFullyLoadedContext.UIContextChanged += (_, args) =>
            JoinableTaskFactory.RunAsync(() => ProbeAsync(args.Activated)).FileAndForget("RunicDevKit/WorkspaceProbe");
        await ProbeAsync(KnownUIContexts.SolutionExistsAndFullyLoadedContext.IsActive);
    }

    private async Task ProbeAsync(bool solutionLoaded)
    {
        int current = Interlocked.Increment(ref generation);
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        var context = UIContext.FromUIContextGuid(new Guid(RunicActivation.WorkspaceContext));
        string? directory = null;
        if (solutionLoaded && await GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution
            && ErrorHandler.Succeeded(solution.GetSolutionInfo(out string solutionDirectory, out _, out _)))
            directory = solutionDirectory;
        if (string.IsNullOrEmpty(directory)) { context.IsActive = false; return; }

        await TaskScheduler.Default;
        bool runic;
        try { runic = RunicWorkspace.Detect(directory!).IsRunic; }
        catch (Exception error)
        {
            ActivityLog.TryLogWarning(RunicPackage.Title, "Could not read the solution directory: " + error.Message);
            runic = false;
        }
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        // A later solution event decides.
        if (current == generation) context.IsActive = runic;
    }
}
