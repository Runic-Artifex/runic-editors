using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow.Sections;

/// <summary>Placeholder for the Translations section; VS-6 fills it.</summary>
internal sealed class TranslationsSection : RunicToolWindowSection
{
    public override RunicSection Section => RunicSection.Translations;
}
