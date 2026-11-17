using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// One expander per <see cref="ToolWindowSections.All"/> entry, filled by that section's
/// <see cref="RunicToolWindowSection"/> (found in this assembly, one per section), so slices change their section
/// file only.
/// </summary>
internal sealed class RunicToolWindowControl : ScrollViewer
{
    public RunicToolWindowControl()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        var sections = typeof(RunicToolWindowSection).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(RunicToolWindowSection).IsAssignableFrom(type))
            .Select(type => (RunicToolWindowSection)Activator.CreateInstance(type))
            .ToDictionary(section => section.Section);
        var panel = new StackPanel { Margin = new Thickness(4) };
        foreach (var info in ToolWindowSections.All)
        {
            var expander = new Expander { Header = info.Title, IsExpanded = true, Margin = new Thickness(0, 0, 0, 6) };
            expander.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            AutomationProperties.SetName(expander, info.Title);
            expander.Content = sections.TryGetValue(info.Section, out var section) ? section.CreateContent(info) : RunicToolWindowSection.Placeholder(info);
            panel.Children.Add(expander);
        }
        Content = panel;
    }
}
