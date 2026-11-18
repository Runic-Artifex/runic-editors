using System;
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
using RunicArtifex.DevKit.VisualStudio.Translations;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow.Sections;

/// <summary>
/// VS Code's Translations view: the coverage of each locale from <c>runic-translations status</c>, per runic.json project
/// of the solution (<see cref="TranslationStatus.Rows"/>), with the threshold and completeness policy in each row's
/// tooltip. Double-click or Enter opens the project's Translation Status document; the context menu and the toolbar
/// refresh it.
/// </summary>
internal sealed class TranslationsSection : RunicToolWindowSection
{
    public override RunicSection Section => RunicSection.Translations;

    public override FrameworkElement CreateContent(RunicSectionInfo info) => new TranslationsStatusView(info.Title);

    private sealed class TranslationsStatusView : ContentControl
    {
        private static readonly string ShowTitle = RunicCommandCatalog.Get(RunicCommandIds.ShowTranslationStatus).Title;
        private static readonly string RefreshTitle = RunicCommandCatalog.Get(RunicCommandIds.RefreshTranslationStatus).Title;

        public TranslationsStatusView(string name)
        {
            AutomationProperties.SetName(this, name);
            // A container, not a Tab stop (see SdkTreeView).
            Focusable = false;
            IsTabStop = false;
            Margin = new Thickness(12, 2, 2, 2);
            Loaded += (_, _) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                TranslationStatusState.Instance.Changed += OnChanged;
                Rebuild();
                Run(RefreshTitle, cancellationToken => TranslationStatusState.Instance.EnsureLoadedAsync(cancellationToken));
            };
            Unloaded += (_, _) => TranslationStatusState.Instance.Changed -= OnChanged;
        }

        private void OnChanged(object sender, EventArgs args) => _ = RunicSdkState.Background.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Rebuild();
        });

        private void Rebuild()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var state = TranslationStatusState.Instance;
            var projects = state.Projects;
            if (state.Scanned && projects.Count == 0) { Content = Text(TranslationStatus.NoProject); return; }
            var rows = TranslationStatus.Rows(projects, state.SolutionDirectory);
            if (rows.Count == 0)
            {
                var panel = new StackPanel();
                panel.Children.Add(Text(TranslationStatus.Welcome));
                var button = new Button { Content = ShowTitle, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
                button.Click += (_, _) => Run(ShowTitle, Show(null));
                panel.Children.Add(button);
                Content = panel;
                return;
            }
            var tree = ThemedTree.Create(AutomationProperties.GetName(this));
            foreach (var row in rows) tree.Items.Add(Item(row));
            Content = tree;
        }

        private TreeViewItem Item(TreeRow row)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = new TreeViewItem { Header = ThemedTree.Header(Moniker(row.Icon), row.Label, row.Description), IsExpanded = row.Expanded, Tag = row };
            AutomationProperties.SetName(item, row.ToString());
            if (row.Tooltip != null) item.ToolTip = row.Tooltip;
            foreach (var child in row.Children) item.Items.Add(Item(child));

            var menu = new ContextMenu();
            var show = new MenuItem { Header = ShowTitle };
            show.Click += (_, _) => Run(ShowTitle, Show(row.Project));
            var refresh = new MenuItem { Header = RefreshTitle };
            refresh.Click += (_, _) => Run(RefreshTitle, cancellationToken => TranslationStatusState.Instance.RefreshAsync(cancellationToken));
            menu.Items.Add(show);
            menu.Items.Add(refresh);
            item.ContextMenu = menu;
            item.MouseDoubleClick += (_, args) =>
            {
                // The event bubbles through the parent items; only the item under the pointer acts.
                if (!IsHeaderOf(item, args.OriginalSource) || row.Children.Count > 0) return;
                args.Handled = true;
                Run(ShowTitle, Show(row.Project));
            };
            item.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter || !item.IsSelected) return;
                args.Handled = true;
                Run(ShowTitle, Show(row.Project));
            };
            return item;
        }

        /// <summary>Show Translation Status for the row's project, or the command's own project choice.</summary>
        private static Func<CancellationToken, Task> Show(string? project) => async cancellationToken =>
        {
            project ??= await TranslationsWorkspace.ChooseProjectAsync(cancellationToken);
            if (project != null) await ShowTranslationStatusCommand.ShowAsync(project, cancellationToken);
        };

        private static bool IsHeaderOf(TreeViewItem item, object source)
        {
            for (var element = source as DependencyObject; element != null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
                if (element is TreeViewItem owner) return ReferenceEquals(owner, item);
            return false;
        }

        private static void Run(string title, Func<CancellationToken, Task> action) => _ = RunicSdkState.Background.RunAsync(async () =>
        {
            try { await action(CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, $"{title}: {error.Message}", RunicPackage.Title,
                    OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        });

        private static TextBlock Text(string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return block;
        }

        private static ImageMoniker Moniker(RowIcon icon) => icon switch
        {
            RowIcon.Project => KnownMonikers.Web,
            RowIcon.Pass => KnownMonikers.StatusOK,
            RowIcon.Warning => KnownMonikers.StatusWarning,
            RowIcon.Error => KnownMonikers.StatusError,
            RowIcon.Running => KnownMonikers.Loading,
            RowIcon.NotRun => KnownMonikers.StatusNotStarted,
            _ => default,
        };
    }
}
