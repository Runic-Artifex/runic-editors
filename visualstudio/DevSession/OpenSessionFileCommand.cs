using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>Open the dev Session File (VS Code: <c>runic.dev.openSessionFile</c>): the project's <c>obj/runic/dev-session.json</c>.</summary>
internal sealed class OpenSessionFileCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.OpenSessionFile;

    public override void BeforeQueryStatus(OleMenuCommand command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        command.Visible = true;
        command.Enabled = DevSessionService.Instance.CommandState().CanOpenSessionFile;
    }

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        var service = DevSessionService.Instance;
        string project = await service.PickAsync(null, cancellationToken);
        string path = service.SessionPath(project);
        if (!File.Exists(path))
            throw new InvalidOperationException($"{Path.GetFileName(project)} has no dev session file yet ({path}); start dotnet runic dev first.");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        VsShellUtilities.OpenDocument(package, path);
    }
}
