using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>One action button or context-menu item of a section.</summary>
internal sealed class SectionAction
{
    public SectionAction(string title, Func<TreeRow?, CancellationToken, Task> run, Func<TreeRow, bool>? appliesTo = null)
    {
        Title = title; Run = run; AppliesTo = appliesTo;
    }

    public string Title { get; }
    public Func<TreeRow?, CancellationToken, Task> Run { get; }
    /// <summary>Rows whose context menu offers the action; null for the empty state's button only.</summary>
    public Func<TreeRow, bool>? AppliesTo { get; }
}

/// <summary>
/// A section's content: the rows of Core's <see cref="SdkSections"/> in a themed <see cref="TreeView"/>, or, without
/// rows, the welcome text and a button (VS Code's view welcome content). It rebuilds when <see cref="RunicSdkState"/> changes.
/// </summary>
internal sealed class SdkTreeView : ContentControl
{
    private readonly Func<IReadOnlyList<SdkProjectState>, IReadOnlyList<TreeRow>> rows;
    private readonly string welcome;
    private readonly SectionAction emptyAction;
    private readonly IReadOnlyList<SectionAction> rowActions;
    private readonly Action<TreeRow> activate;

    public SdkTreeView(string name, Func<IReadOnlyList<SdkProjectState>, IReadOnlyList<TreeRow>> rows, string welcome,
        SectionAction emptyAction, IReadOnlyList<SectionAction> rowActions, Action<TreeRow> activate)
    {
        this.rows = rows; this.welcome = welcome; this.emptyAction = emptyAction; this.rowActions = rowActions; this.activate = activate;
        AutomationProperties.SetName(this, name);
        // A container, not a Tab stop: Tab goes from the section header to its tree or button (row 16 found an unnamed stop here).
        Focusable = false;
        IsTabStop = false;
        Margin = new Thickness(12, 2, 2, 2);
        Loaded += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RunicSdkState.Instance.Changed += OnChanged;
            Rebuild();
            Run(emptyAction.Title, (_, cancellationToken) => RunicSdkState.Instance.EnsureScannedAsync(cancellationToken), null);
        };
        Unloaded += (_, _) => RunicSdkState.Instance.Changed -= OnChanged;
    }

    private void OnChanged(object sender, EventArgs args) => _ = RunicSdkState.Background.RunAsync(async () =>
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Rebuild();
    });

    private void Rebuild()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var state = RunicSdkState.Instance;
        var projects = state.Projects;
        if (state.Scanned && projects.Count == 0) { Content = Text(SdkSections.NoSdkProject); return; }
        var items = rows(projects);
        if (items.Count == 0)
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(welcome));
            var button = new Button { Content = emptyAction.Title, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            button.Click += (_, _) => { ThreadHelper.ThrowIfNotOnUIThread(); Run(emptyAction.Title, emptyAction.Run, null); };
            panel.Children.Add(button);
            Content = panel;
            return;
        }
        var tree = ThemedTree.Create(AutomationProperties.GetName(this));
        foreach (var row in items) tree.Items.Add(Item(row));
        Content = tree;
    }

    private TreeViewItem Item(TreeRow row)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // No foreground of its own: the item inherits the tree's, and the template's selected text brush wins when selected.
        var item = new TreeViewItem { Header = ThemedTree.Header(Moniker(row.Icon), row.Label, row.Description), IsExpanded = row.Expanded, Tag = row };
        AutomationProperties.SetName(item, row.ToString());
        if (row.Tooltip != null) item.ToolTip = row.Tooltip;
        foreach (var child in row.Children) item.Items.Add(Item(child));

        var menu = new ContextMenu();
        foreach (var action in rowActions)
        {
            if (action.AppliesTo?.Invoke(row) != true) continue;
            var menuItem = new MenuItem { Header = action.Title };
            menuItem.Click += (_, _) => { ThreadHelper.ThrowIfNotOnUIThread(); Run(action.Title, action.Run, row); };
            menu.Items.Add(menuItem);
        }
        if (menu.Items.Count > 0) item.ContextMenu = menu;
        item.MouseDoubleClick += (sender, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // The event bubbles through the parent items; only the item under the pointer acts.
            if (!IsHeaderOf(item, args.OriginalSource)) return;
            if (row.Action == RowAction.None) return;
            args.Handled = true;
            Activate(row);
        };
        item.KeyDown += (_, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (args.Key != Key.Enter || !item.IsSelected || row.Action == RowAction.None) return;
            args.Handled = true;
            Activate(row);
        };
        return item;
    }

    private static bool IsHeaderOf(TreeViewItem item, object source)
    {
        for (var element = source as DependencyObject; element != null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
            if (element is TreeViewItem owner) return ReferenceEquals(owner, item);
        return false;
    }

    private void Activate(TreeRow row)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { activate(row); }
        catch (Exception error) when (error is not OutOfMemoryException) { ShowError(error.Message); }
    }

    private void Run(string title, Func<TreeRow?, CancellationToken, Task> action, TreeRow? row) => _ = RunicSdkState.Background.RunAsync(async () =>
    {
        try { await action(row, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            SdkOutput.WriteLine(title + ": " + error.Message);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ShowError(error.Message);
        }
    });

    private static void ShowError(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, message, RunicPackage.Title,
            OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    private static TextBlock Text(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        return block;
    }

    private static ImageMoniker Moniker(RowIcon icon) => icon switch
    {
        RowIcon.Project => KnownMonikers.CSProjectNode,
        RowIcon.ViewModel => KnownMonikers.ClassPublic,
        RowIcon.Property => KnownMonikers.PropertyPublic,
        RowIcon.Boolean => KnownMonikers.FieldPublic,
        RowIcon.Method => KnownMonikers.MethodPublic,
        RowIcon.Event => KnownMonikers.EventPublic,
        RowIcon.Interface => KnownMonikers.InterfacePublic,
        RowIcon.Field => KnownMonikers.FieldPublic,
        RowIcon.Healthy or RowIcon.Pass => KnownMonikers.StatusOK,
        RowIcon.NotHealthy or RowIcon.Error => KnownMonikers.StatusError,
        RowIcon.Warning => KnownMonikers.StatusWarning,
        RowIcon.Running => KnownMonikers.Loading,
        RowIcon.NotRun => KnownMonikers.StatusNotStarted,
        RowIcon.Fix => KnownMonikers.IntellisenseLightBulb,
        RowIcon.DevReady => KnownMonikers.StatusRunning,
        RowIcon.DevStarting => KnownMonikers.Loading,
        RowIcon.DevStopped => KnownMonikers.StatusStopped,
        RowIcon.Globe => KnownMonikers.OpenWebSite,
        RowIcon.Watcher => KnownMonikers.Refresh,
        RowIcon.Window => KnownMonikers.Application,
        RowIcon.IdeWindow => KnownMonikers.Run,
        RowIcon.Page => KnownMonikers.HTMLFile,
        RowIcon.DebugEndpoint => KnownMonikers.RemoteDebugger,
        RowIcon.Folder => KnownMonikers.FolderClosed,
        _ => default,
    };
}
