using System.Collections.Generic;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>The sections of the Runic tool window, in display order (PARITY §2.2: VS Code's four Runic views).</summary>
public enum RunicSection
{
    DevSession,
    ViewModels,
    Doctor,
    Translations,
}

public sealed class RunicSectionInfo
{
    internal RunicSectionInfo(RunicSection section, string title, bool sdkOnly, RunicSlice slice)
    {
        Section = section; Title = title; SdkOnly = sdkOnly; Slice = slice;
    }

    public RunicSection Section { get; }
    public string Title { get; }
    /// <summary>Shown only for a solution with a Runic SDK project; Translations-only solutions see the Translations section.</summary>
    public bool SdkOnly { get; }
    /// <summary>The slice that fills the section; until then it shows <see cref="ToolWindowSections.Placeholder"/>.</summary>
    public RunicSlice Slice { get; }
}

public static class ToolWindowSections
{
    public const string Caption = "Runic";

    public static IReadOnlyList<RunicSectionInfo> All { get; } = new[]
    {
        new RunicSectionInfo(RunicSection.DevSession, "Dev Session", sdkOnly: true, RunicSlice.Implemented),
        new RunicSectionInfo(RunicSection.ViewModels, "ViewModels", sdkOnly: true, RunicSlice.Implemented),
        new RunicSectionInfo(RunicSection.Doctor, "Doctor", sdkOnly: true, RunicSlice.Implemented),
        new RunicSectionInfo(RunicSection.Translations, "Translations", sdkOnly: false, RunicSlice.Implemented),
    };

    /// <summary>Whether the section shows, by <see cref="RunicActivation.ShowsSdkFeatures"/>.</summary>
    public static bool IsVisible(RunicSectionInfo section, bool scanned, int sdkProjects)
        => !section.SdkOnly || RunicActivation.ShowsSdkFeatures(scanned, sdkProjects);

    public static string Placeholder(RunicSectionInfo section) => $"{section.Title} is not available yet in this build of Runic DevKit.";
}
