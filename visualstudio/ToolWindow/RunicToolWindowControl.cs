using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// One expander per <see cref="ToolWindowSections.All"/> entry, filled by that section's
/// <see cref="RunicToolWindowSection"/> (found in this assembly, one per section), so slices change their section
/// file only. The Runic SDK sections show only in a solution with a Runic SDK project
/// (<see cref="ToolWindowSections.IsVisible"/>); a solution with only Runic Translations sees the Translations section alone.
/// </summary>
internal sealed class RunicToolWindowControl : ScrollViewer
{
    private readonly List<(RunicSectionInfo Info, Expander Expander)> expanders = new();

    public RunicToolWindowControl()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // A container, not a Tab stop: Tab goes from the toolbar to the first section header.
        IsTabStop = false;
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
            expanders.Add((info, expander));
        }
        Content = panel;
        UpdateSections();
        Loaded += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RunicSdkState.Instance.Changed += OnChanged;
            UpdateSections();
            _ = RunicSdkState.Background.RunAsync(async () =>
            {
                try { await RunicSdkState.Instance.EnsureScannedAsync(default); }
                catch (Exception error) when (error is not OperationCanceledException) { SdkOutput.WriteLine("Could not find the Runic SDK projects: " + error.Message); }
            });
        };
        Unloaded += (_, _) => RunicSdkState.Instance.Changed -= OnChanged;
    }

    private void OnChanged(object sender, EventArgs args) => _ = RunicSdkState.Background.RunAsync(async () =>
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        UpdateSections();
    });

    private void UpdateSections()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var state = RunicSdkState.Instance;
        bool scanned = state.Scanned;
        int count = state.Projects.Count;
        foreach (var (info, expander) in expanders)
            expander.Visibility = ToolWindowSections.IsVisible(info, scanned, count) ? Visibility.Visible : Visibility.Collapsed;
    }
}
