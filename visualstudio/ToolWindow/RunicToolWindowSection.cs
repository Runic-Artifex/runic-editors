using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>The content of one section of the Runic tool window. Until its slice lands, a section shows a placeholder.</summary>
internal abstract class RunicToolWindowSection
{
    public abstract RunicSection Section { get; }

    public virtual FrameworkElement CreateContent(RunicSectionInfo info) => Placeholder(info);

    public static FrameworkElement Placeholder(RunicSectionInfo info)
    {
        var text = new TextBlock { Text = ToolWindowSections.Placeholder(info), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 4, 4, 4) };
        text.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return text;
    }
}
