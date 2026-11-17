using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.DevSession;

/// <summary>
/// Open the Development Server (VS Code: <c>runic.dev.openBrowser</c>): the ready session's development server, else
/// its first page's address, in the default browser.
/// </summary>
internal sealed class OpenDevServerCommand : RunicCommandHandler
{
    public override int Id => RunicCommandIds.OpenDevServer;

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        var service = DevSessionService.Instance;
        string project = await service.PickAsync(null, cancellationToken);
        var snapshot = service.Refresh(project);
        string? url = snapshot.State == DevState.Ready ? snapshot.Session?.ServerUrl : null;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"dotnet runic dev serves no development server for {Path.GetFileName(project)} ({snapshot.Label}).");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        VsShellUtilities.OpenSystemBrowser(uri.AbsoluteUri);
    }
}
