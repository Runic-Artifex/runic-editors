using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow.Sections;

/// <summary>
/// VS Code's ViewModels view: each ViewModel of the development source map (<c>runic-bridge/sources.json</c>) with its
/// Bridge members. Double-click, Enter or Open C# Member opens the member's C# source.
/// </summary>
internal sealed class ViewModelsSection : RunicToolWindowSection
{
    public const string OpenMember = "Open C# Member";

    public override RunicSection Section => RunicSection.ViewModels;

    public override FrameworkElement CreateContent(RunicSectionInfo info) => new SdkTreeView(info.Title, SdkSections.ViewModels, SdkSections.ViewModelsWelcome,
        new SectionAction(RunicCommandCatalog.Get(RunicCommandIds.RefreshViewModels).Title, (_, cancellationToken) => RunicSdkState.Instance.RefreshViewModelsAsync(cancellationToken)),
        new[]
        {
            new SectionAction(OpenMember, async (row, cancellationToken) =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                SourceNavigation.Open(row?.Location);
            }, row => row.Action == RowAction.OpenLocation),
            new SectionAction(RunicCommandCatalog.Get(RunicCommandIds.RefreshViewModels).Title, (_, cancellationToken) => RunicSdkState.Instance.RefreshViewModelsAsync(cancellationToken), _ => true),
        },
        row =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (row.Action == RowAction.OpenLocation) SourceNavigation.Open(row.Location);
        });
}
