using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// Start dotnet runic dev (Tools → Runic, the tool window's toolbar; VS Code: <c>runic.dev.start</c>): the development
/// server and the Window, as a child process whose output goes to the "Runic SDK" Output pane and whose diagnostics go to
/// the Error List.
/// </summary>
internal sealed class StartDevCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.StartDev;

    public override Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
        => StartAsync(package, noWindow: false, cancellationToken);

    /// <summary>Shared with <see cref="StartDevNoWindowCommand"/>.</summary>
    internal static async Task StartAsync(RunicPackage package, bool noWindow, CancellationToken cancellationToken)
    {
        var service = DevSessionService.Instance;
        string project = await service.PickAsync(null, cancellationToken);
        await service.StartAsync(project, noWindow, message => ConfirmAsync(package, message), cancellationToken);
        await package.ShowToolWindowAsync(typeof(RunicToolWindow), 0, create: true, cancellationToken);
    }

    internal static async Task<bool> ConfirmAsync(RunicPackage package, string message)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return VsShellUtilities.ShowMessageBox(package, message, RunicPackage.Title, OLEMSGICON.OLEMSGICON_WARNING,
            OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) == (int)Microsoft.VisualStudio.VSConstants.MessageBoxResult.IDOK;
    }
}
