using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Visual Studio's counterparts of VS Code's quick pick and input box for the Translations commands: a themed modal
/// dialog with a filter box over labelled choices (description and detail below each), or a validated text field.
/// </summary>
internal static class ChoiceDialog
{
    /// <summary>Shows the choices, below <paramref name="prompt"/> when given; null when the user cancels.</summary>
    public static Choice<T>? Pick<T>(string title, IReadOnlyList<Choice<T>> choices, string? prompt = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = NewWindow(title);
        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(filter, "Filter");
        var list = new ListBox { MinHeight = 160, MaxHeight = 420 };
        AutomationProperties.SetName(list, title);
        list.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        list.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        void Fill()
        {
            string text = filter.Text.Trim();
            list.Items.Clear();
            foreach (var choice in choices.Where(choice => text.Length == 0 || Matches(choice.Label, text) || Matches(choice.Description, text) || Matches(choice.Detail, text)))
                list.Items.Add(Item(choice));
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        filter.TextChanged += (_, _) => Fill();
        Fill();
        Choice<T>? result = null;
        void Accept()
        {
            if (list.SelectedItem is ListBoxItem { Tag: Choice<T> selected }) { result = selected; window.DialogResult = true; }
        }
        filter.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1); args.Handled = true; }
            else if (args.Key == Key.Up && list.Items.Count > 0) { list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0); args.Handled = true; }
            else if (args.Key == Key.Enter) { Accept(); args.Handled = true; }
        };
        list.MouseDoubleClick += (_, _) => Accept();
        list.KeyDown += (_, args) => { if (args.Key == Key.Enter) { Accept(); args.Handled = true; } };
        var panel = new DockPanel { Margin = new Thickness(12) };
        var buttons = Buttons(window, Accept);
        if (prompt != null)
        {
            var text = Text(prompt);
            text.Margin = new Thickness(0, 0, 0, 6);
            DockPanel.SetDock(text, Dock.Top);
            panel.Children.Add(text);
        }
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(filter);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        window.Content = panel;
        window.Loaded += (_, _) => filter.Focus();
        return window.ShowModal() == true ? result : null;
    }

    /// <summary>Asks for a line of text; <paramref name="validate"/> returns an error message or null. Null when the user cancels.</summary>
    public static string? Ask(string title, string prompt, Func<string, string?> validate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = NewWindow(title);
        var label = Text(prompt);
        var input = new TextBox { Margin = new Thickness(0, 6, 0, 4) };
        AutomationProperties.SetName(input, prompt);
        var error = Text("");
        error.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowValidationErrorTextBrushKey);
        string? result = null;
        void Accept()
        {
            string? problem = validate(input.Text);
            if (problem != null) { error.Text = problem; return; }
            result = input.Text; window.DialogResult = true;
        }
        input.KeyDown += (_, args) => { if (args.Key == Key.Enter) { Accept(); args.Handled = true; } };
        input.TextChanged += (_, _) => error.Text = "";
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(error);
        panel.Children.Add(Buttons(window, Accept));
        window.Content = panel;
        window.SizeToContent = SizeToContent.Height;
        window.Loaded += (_, _) => input.Focus();
        return window.ShowModal() == true ? result : null;
    }

    private static bool Matches(string? text, string filter) => text != null && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private static DialogWindow NewWindow(string title)
    {
        var window = new DialogWindow
        {
            Title = $"{RunicPackage.Title} — {title}", Width = 560, Height = 480, MinWidth = 360, MinHeight = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResizeWithGrip,
            HasMinimizeButton = false, HasMaximizeButton = false,
        };
        window.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        window.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return window;
    }

    private static ListBoxItem Item<T>(Choice<T> choice)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new TextBlock { Text = choice.Label, FontWeight = FontWeights.SemiBold });
        if (choice.Description != null)
        {
            var description = new TextBlock { Text = choice.Description, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            description.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            heading.Children.Add(description);
        }
        panel.Children.Add(heading);
        if (choice.Detail != null)
        {
            var detail = new TextBlock { Text = choice.Detail, TextWrapping = TextWrapping.Wrap };
            detail.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            panel.Children.Add(detail);
        }
        var item = new ListBoxItem { Content = panel, Tag = choice };
        AutomationProperties.SetName(item, choice.Detail != null ? $"{choice}; {choice.Detail}" : choice.ToString());
        return item;
    }

    private static StackPanel Buttons(DialogWindow window, Action accept)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = false, MinWidth = 75, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2, 8, 2) };
        ok.Click += (_, _) => accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75, Padding = new Thickness(8, 2, 8, 2) };
        cancel.Click += (_, _) => window.DialogResult = false;
        panel.Children.Add(ok);
        panel.Children.Add(cancel);
        return panel;
    }

    private static TextBlock Text(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return block;
    }
}
