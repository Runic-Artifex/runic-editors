using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Commands;

/// <summary>
/// The handler of one command of <c>Commands.vsct</c>. <see cref="CommandRegistrar"/> finds every concrete subclass
/// in this assembly, so a slice implements a command by changing its handler file only; it never edits the
/// <c>.vsct</c> or <see cref="RunicPackage"/>. Each command has exactly one handler (Core.Tests checks this).
/// </summary>
internal abstract class RunicCommandHandler
{
    /// <summary>The command's <see cref="RunicCommandIds"/> constant.</summary>
    public abstract int Id { get; }

    /// <summary>False for a stub: the command stays hidden until its slice implements it.</summary>
    public virtual bool IsAvailable => true;

    /// <summary>Runs on the UI thread before a menu or toolbar shows the command.</summary>
    public virtual void BeforeQueryStatus(OleMenuCommand command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        command.Visible = command.Enabled = IsAvailable;
    }

    public abstract Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken);
}

/// <summary>A command whose slice has not landed: hidden, and if run anyway (Command window), says so.</summary>
internal abstract class NotAvailableYetHandler : RunicCommandHandler
{
    public sealed override bool IsAvailable => false;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        VsShellUtilities.ShowMessageBox(package, RunicCommandCatalog.NotAvailableYet(Id), RunicPackage.Title,
            OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
