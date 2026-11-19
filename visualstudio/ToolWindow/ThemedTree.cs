using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// The tool window's section trees in Visual Studio's tree colours. WPF's <see cref="TreeViewItem"/> template draws a
/// selected row with the Windows <see cref="SystemColors"/> highlight brushes, which ignore the Visual Studio theme (an
/// unfocused selection was near-white under white text in the dark theme). Each tree maps those keys to the theme's
/// <see cref="TreeViewColors"/> selected-active and selected-inactive brushes, again when the theme changes. Rows set no
/// foreground of their own, so the template's selected text brush reaches the label, and the description is the same
/// brush, dimmed, instead of a fixed grey. The template's unselected text is <see cref="SystemColors.ControlTextBrushKey"/>
/// (black whatever the theme), so that key maps to the theme's tree text too: without it every row was black on the
/// dark theme's background.
/// </summary>
internal static class ThemedTree
{
    /// <summary>The Windows brushes the <see cref="TreeViewItem"/> template draws with, and the theme's brush for each.</summary>
    private static readonly (object System, object Theme)[] Colors =
    {
        (SystemColors.ControlTextBrushKey, TreeViewColors.BackgroundTextBrushKey),
        (SystemColors.HighlightBrushKey, TreeViewColors.SelectedItemActiveBrushKey),
        (SystemColors.HighlightTextBrushKey, TreeViewColors.SelectedItemActiveTextBrushKey),
        (SystemColors.InactiveSelectionHighlightBrushKey, TreeViewColors.SelectedItemInactiveBrushKey),
        (SystemColors.InactiveSelectionHighlightTextBrushKey, TreeViewColors.SelectedItemInactiveTextBrushKey),
    };

    /// <summary>An empty tree named <paramref name="name"/> for UI Automation (Narrator).</summary>
    public static TreeView Create(string name)
    {
        var tree = new TreeView { BorderThickness = new Thickness(0) };
        tree.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        tree.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        AutomationProperties.SetName(tree, name);
        ApplyColors(tree);
        ThemeChangedEventHandler changed = _ => ApplyColors(tree);
        tree.Loaded += (_, _) => { VSColorTheme.ThemeChanged -= changed; VSColorTheme.ThemeChanged += changed; ApplyColors(tree); };
        tree.Unloaded += (_, _) => VSColorTheme.ThemeChanged -= changed;
        return tree;
    }

    /// <summary>A row: the icon, the label and the dimmed description, all in the row's (selection-aware) foreground.</summary>
    public static StackPanel Header(ImageMoniker moniker, string label, string? description)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (moniker.Guid != System.Guid.Empty) header.Children.Add(new CrispImage { Moniker = moniker, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
        header.Children.Add(new TextBlock { Text = label });
        if (description != null) header.Children.Add(new TextBlock { Text = description, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.75 });
        return header;
    }

    private static void ApplyColors(TreeView tree)
    {
        foreach (var (system, theme) in Colors)
        {
            if (tree.TryFindResource(theme) is Brush brush) tree.Resources[system] = brush;
            else tree.Resources.Remove(system);
        }
    }
}
