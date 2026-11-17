using System;
using System.Linq;
using System.Windows;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow.Sections;

/// <summary>
/// VS Code's Doctor view: the result of <c>dotnet runic doctor</c> per Runic SDK project, failures first, each check with
/// its remediation. The same findings go to the Error List (<see cref="DoctorErrorList"/>).
/// </summary>
internal sealed class DoctorSection : RunicToolWindowSection
{
    public override RunicSection Section => RunicSection.Doctor;

    public override FrameworkElement CreateContent(RunicSectionInfo info)
    {
        string run = RunicCommandCatalog.Get(RunicCommandIds.RunDoctor).Title;
        return new SdkTreeView(info.Title, SdkSections.Doctor, SdkSections.DoctorWelcome,
            new SectionAction(run, (_, cancellationToken) => RunicSdkState.Instance.RunDoctorAsync(null, cancellationToken)),
            new[] { new SectionAction(run, (row, cancellationToken) => RunicSdkState.Instance.RunDoctorAsync(Targets(row), cancellationToken), _ => true) },
            row =>
            {
                if (row.Action == RowAction.RunDoctor)
                    _ = RunicSdkState.Background.RunAsync(() => RunicSdkState.Instance.RunDoctorAsync(Targets(row), default));
            });
    }

    /// <summary>The row's project, as VS Code's doctor items run their own project.</summary>
    private static SdkProjectState[]? Targets(TreeRow? row)
        => row?.Project is { } project ? RunicSdkState.Instance.Projects.Where(state => string.Equals(state.Project, project, StringComparison.OrdinalIgnoreCase)).ToArray() : null;
}
