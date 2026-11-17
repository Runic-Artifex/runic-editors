using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Migration;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio;

/// <summary>
/// The Runic DevKit package. It registers the whole command table (<c>Commands.vsct</c>, one handler per command,
/// <see cref="CommandRegistrar"/>) and the Runic tool window; features live in their handler and section files, so
/// later slices do not edit this class.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
// Loads in the background once a solution is open, to tell users of the former identity how to uninstall it.
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(typeof(RunicToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
// New GUIDs with the Runic DevKit identity (0.22): the former Runic MF2 Translations package, which may still be
// installed next to this one, keeps 38646832-a96f-4fd1-b33a-108353a854da and its command set.
[Guid(PackageGuid)]
public sealed class RunicPackage : AsyncPackage
{
    public const string PackageGuid = "be2a06be-886d-4e5b-ac97-5853699532bb";
    public const string Title = "Runic DevKit";

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        _ = JoinableTaskFactory.RunAsync(() => FormerExtensionNotice.ShowIfInstalledAsync(this, DisposalToken));
        await CommandRegistrar.RegisterAsync(this, cancellationToken);
    }
}
