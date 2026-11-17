using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.DevSession;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow.Sections;

/// <summary>
/// VS Code's Dev Session view: per Runic SDK project the <c>dotnet runic dev</c> state and, while it runs, the
/// development server, the Window's owner or process, the pages, the <c>debugEndpoints</c> and the generated clients
/// (<see cref="DevSessionRows"/>). Double-click or Enter opens a row's server, page, session file or folder; a row's
/// context menu starts or stops its project's <c>dev</c> and opens its session file.
/// </summary>
internal sealed class DevSessionSection : RunicToolWindowSection
{
    public override RunicSection Section => RunicSection.DevSession;

    public override FrameworkElement CreateContent(RunicSectionInfo info)
    {
        var service = DevSessionService.Instance;
        string start = RunicCommandCatalog.Get(RunicCommandIds.StartDev).Title;
        string startNoWindow = RunicCommandCatalog.Get(RunicCommandIds.StartDevNoWindow).Title;
        string stop = RunicCommandCatalog.Get(RunicCommandIds.StopDev).Title;
        string openFile = RunicCommandCatalog.Get(RunicCommandIds.OpenSessionFile).Title;
        return new SdkTreeView(info.Title, projects => DevSessionRows.Build(projects, service.Snapshot), DevSessionRows.Welcome,
            new SectionAction(start, StartPickedAsync),
            new[]
            {
                new SectionAction(start, (row, cancellationToken) => service.StartAsync(row!.Project!, false, StartDevCommand.ConfirmAsync, cancellationToken), row => row.Project != null && !service.Snapshot(row.Project).IsActive),
                new SectionAction(startNoWindow, (row, cancellationToken) => service.StartAsync(row!.Project!, true, StartDevCommand.ConfirmAsync, cancellationToken), row => row.Project != null && !service.Snapshot(row.Project).IsActive),
                new SectionAction(stop, (row, cancellationToken) => service.StopAsync(row!.Project!, cancellationToken), row => row.Project != null && (service.Snapshot(row.Project).IsActive || service.StartedHere(row.Project))),
                new SectionAction(openFile, OpenSessionFileAsync,
                    row => row.Project != null && service.Snapshot(row.Project).Session != null),
            },
            Activate);
    }

    private static async Task StartPickedAsync(TreeRow? row, CancellationToken cancellationToken)
    {
        var service = DevSessionService.Instance;
        string project = await service.PickAsync(row?.Project, cancellationToken);
        await service.StartAsync(project, false, StartDevCommand.ConfirmAsync, cancellationToken);
    }

    private static async Task OpenSessionFileAsync(TreeRow? row, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Activate(new TreeRow("", RowIcon.None, row!.Project) { Action = RowAction.OpenFile, Target = DevSessions.PathFor(row.Project!) });
    }

    private static void Activate(TreeRow row)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        switch (row.Action)
        {
            case RowAction.OpenUrl:
                if (Uri.TryCreate(row.Target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    VsShellUtilities.OpenSystemBrowser(uri.AbsoluteUri);
                break;
            case RowAction.OpenFile:
                if (row.Target == null || !File.Exists(row.Target)) throw new InvalidOperationException($"{row.Target} does not exist.");
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, row.Target);
                break;
            case RowAction.OpenFolder:
                if (row.Target == null || !Directory.Exists(row.Target)) throw new InvalidOperationException($"{row.Target} does not exist yet.");
                Process.Start(new ProcessStartInfo("explorer.exe", CommandLine.Quote(row.Target)) { UseShellExecute = true })?.Dispose();
                break;
        }
    }
}
