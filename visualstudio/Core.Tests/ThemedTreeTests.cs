using System;
using System.IO;
using System.Text.RegularExpressions;
using TUnit.Core;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>
/// The tool window's trees map every Windows brush the WPF <c>TreeViewItem</c> template draws text and selection with to
/// a Visual Studio theme brush (<c>ToolWindow/ThemedTree.cs</c>). An unmapped text key is black in the dark theme (the
/// 0.22 checklist, row 1). The Visual Studio project builds only on Windows, so this reads its source.
/// </summary>
public class ThemedTreeTests
{
    private static string Source()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "visualstudio", "ToolWindow", "ThemedTree.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new InvalidOperationException("visualstudio/ToolWindow/ThemedTree.cs not found above " + AppContext.BaseDirectory);
    }

    [Test]
    [Arguments("ControlTextBrushKey", "BackgroundTextBrushKey")]
    [Arguments("HighlightBrushKey", "SelectedItemActiveBrushKey")]
    [Arguments("HighlightTextBrushKey", "SelectedItemActiveTextBrushKey")]
    [Arguments("InactiveSelectionHighlightBrushKey", "SelectedItemInactiveBrushKey")]
    [Arguments("InactiveSelectionHighlightTextBrushKey", "SelectedItemInactiveTextBrushKey")]
    public void Each_template_brush_maps_to_the_theme(string system, string theme)
        => Check.True(Regex.IsMatch(Source(), $@"\(SystemColors\.{system},\s*TreeViewColors\.{theme}\)"), $"ThemedTree maps SystemColors.{system} to TreeViewColors.{theme}");

    [Test]
    public void Rows_and_descriptions_take_the_mapped_foreground()
    {
        string source = Source();
        // A fixed Foreground on a row or its description would bypass the mapping (and the selected text brush).
        Check.True(!Regex.IsMatch(source, @"Foreground\s*=") && !source.Contains("Brushes."), "no fixed foreground in ThemedTree");
        Check.True(source.Contains("VSColorTheme.ThemeChanged += changed"), "the mapping is refreshed when the theme changes");
    }
}
