using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Migration;
using RunicArtifex.DevKit.VisualStudio.Settings;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio;

/// <summary>
/// The Runic DevKit package. It registers the whole command table (<c>Commands.vsct</c>, one handler per command,
/// <see cref="CommandRegistrar"/>), the Runic tool window and Tools → Options → Runic DevKit; features live in their
/// handler and section files, so later slices do not edit this class.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
// Loads in the background only for a Runic solution (Core/RunicActivation.cs): the rule context (a RunicApplication
// project, a Runic Translations build, an MF2 editor), or the workspace context that Activation/RunicWorkspaceProbe
// sets from the solution's files. The language client (MEF) still starts on the rmf2 content type by itself.
[ProvideUIContextRule(RunicActivation.RuleContext, RunicActivation.RuleName, RunicActivation.RuleExpression,
    new[] { RunicActivation.SdkProjectTerm, RunicActivation.TranslationsBuildTerm, RunicActivation.MessageEditorTerm },
    new[] { RunicActivation.SdkProjectValue, RunicActivation.TranslationsBuildValue, RunicActivation.MessageEditorValue })]
[ProvideAutoLoad(RunicActivation.RuleContext, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(RunicActivation.WorkspaceContext, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(typeof(RunicToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
[ProvideOptionPage(typeof(RunicOptionsPage), RunicSettings.OptionsCategory, RunicSettings.OptionsPage, 0, 0, true)]
// New GUIDs with the Runic DevKit identity (0.22): the former Runic MF2 Translations package, which may still be
// installed next to this one, keeps 38646832-a96f-4fd1-b33a-108353a854da and its command set.
[Guid(PackageGuid)]
public sealed class RunicPackage : AsyncPackage
{
    public const string PackageGuid = "be2a06be-886d-4e5b-ac97-5853699532bb";
    public const string Title = "Runic DevKit";

    /// <summary>Tools → Options → Runic DevKit → General.</summary>
    internal RunicOptionsPage Options => (RunicOptionsPage)GetDialogPage(typeof(RunicOptionsPage));

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        _ = JoinableTaskFactory.RunAsync(() => FormerExtensionNotice.ShowIfInstalledAsync(this, DisposalToken));
        await CommandRegistrar.RegisterAsync(this, cancellationToken);
        // Follows the dev session files and advises the pre-F5 guard (DevSession/).
        _ = JoinableTaskFactory.RunAsync(() => DevSession.DevSessionService.Instance.InitializeAsync(this, DisposalToken));
        // "Break all processes" that a Visual Studio ended with while a Runic debugging session had it off (Debugging/).
        _ = JoinableTaskFactory.RunAsync(Debugging.BreakAllProcesses.RecoverAsync);
    }
}
