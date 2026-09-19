using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using RenderDocComments.Licensing;
using RenderDocComments.Options;

namespace RenderDocComments.CommentTagsExplorer
{
    /// <summary>
    /// Interaction logic for CommentTagsToolWindowControl.xaml.
    /// </summary>
    public partial class CommentTagsToolWindowControl : UserControl, IDisposable
    {
        /// <summary>
        /// Tooltip shown on the Files tab while Premium is locked.
        /// </summary>
        private const string FilesPremiumToolTip =
            "Files view is a Premium feature. Activate Premium to unlock it (Render Doc Options → Get Premium).";

        private IServiceProvider _serviceProvider;
        private readonly CommentTagsTreeViewModel _viewModel;
        private CommentTagsScanner _scanner;
        private bool _isInitialized = false;

        public CommentTagsToolWindowControl() : this(null)
        {
        }

        public CommentTagsToolWindowControl(IServiceProvider serviceProvider)
        {
            InitializeComponent();

            _serviceProvider = serviceProvider;
            _viewModel = new CommentTagsTreeViewModel();
            DataContext = _viewModel;

            Loaded += OnLoaded;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        /// <summary>
        /// Re-evaluates the Premium gating of the Files tab whenever the tool window becomes visible.
        /// </summary>
        /// <param name="sender">
        /// The control whose visibility changed (unused).
        /// </param>
        /// <param name="e">
        /// The dependency property change arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>Licence activation and deactivation do not always raise
        /// <see cref="SettingsChangedBroadcast.SettingsChanged"/> (the purchase and options windows only
        /// persist the settings), so refreshing here guarantees the Files tab always matches the live
        /// licence state — including after startup re-validation.</para>
        /// </remarks>
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) ApplyPremiumState();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) return;
            _isInitialized = true;

            ThreadHelper.ThrowIfNotOnUIThread();
            if (_serviceProvider == null)
            {
                _serviceProvider = ServiceProvider.GlobalProvider;
            }

            // The Files view is Premium-only, so gate the tab before restoring the saved view.
            ApplyPremiumState();

            // React to Premium activation/deactivation performed in the Options dialog.
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;

            // Restore last active view from options ("Files" is honoured only when Premium is unlocked)
            bool savedFilesView = string.Equals(RenderDocOptions.Instance.CommentExplorerView, "Files",
                StringComparison.OrdinalIgnoreCase);

            if (savedFilesView && LicenseManager.PremiumUnlocked)
            {
                ActivateFilesTab(persist: false);
            }
            else
            {
                ActivateTagsTab(persist: false);
            }

            try
            {
                _scanner = new CommentTagsScanner(_serviceProvider, _viewModel);
                _scanner.ActiveDocumentFound += OnActiveDocumentFound;
                _scanner.StartFullScan();
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = "Initialization error: " + ex.Message;
            }
        }

        private void OnActiveDocumentFound(object sender, FileNodeViewModel fileNode)
        {
            // Defer to a Background-priority dispatcher callback so layout settles before scrolling.
            _ = RenderDocCommentsPackage.SharedJoinableTaskFactory.RunAsync(async () =>
            {
                await RenderDocCommentsPackage.SharedJoinableTaskFactory
                    .WithPriority(Dispatcher, System.Windows.Threading.DispatcherPriority.Background)
                    .SwitchToMainThreadAsync();

                if (_viewModel.SelectedTabIndex == 1 && fileNode != null)
                {
                    ScrollToFileNode(fileNode);
                }
            });
        }

        private void ScrollToFileNode(FileNodeViewModel fileNode)
        {
            if (fileNode == null) return;
            // VSTHRD001/VSTHRD110: Dispatcher.BeginInvoke is appropriate for WPF UI threading in this context.
            // The result is intentionally not observed as this is a fire-and-forget UI update.
#pragma warning disable VSTHRD001, VSTHRD110
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    var container = FilesTreeView.ItemContainerGenerator.ContainerFromItem(fileNode) as TreeViewItem;
                    if (container != null)
                    {
                        container.BringIntoView();
                    }
                }
                catch { }
            }));
#pragma warning restore VSTHRD001, VSTHRD110
        }

        private void OnTabRadioClicked(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (FilesTabRadio.IsChecked == true)
            {
                // The Files view is Premium-only. A disabled RadioButton cannot raise Click,
                // but guard anyway so the view can never be reached without a licence.
                if (!LicenseManager.PremiumUnlocked)
                {
                    ActivateTagsTab(persist: false);
                    return;
                }

                ActivateFilesTab(persist: true);
                _scanner?.HighlightActiveDocument();
            }
            else
            {
                ActivateTagsTab(persist: true);
            }
        }

        /// <summary>
        /// Enables or disables the Premium-only Files tab and toggles its padlock icon and<br/>
        /// "PREMIUM" pill to match the current licence state.
        /// </summary>
        /// <remarks>
        /// <para>When Premium is locked the tab is disabled (dimmed, arrow cursor, no hover<br/>
        /// highlight), a padlock icon and a Premium pill are shown, and a tooltip explains how<br/>
        /// to unlock it. If the Files view was active when Premium got deactivated, the control<br/>
        /// falls back to the Tags view.</para>
        /// <para>This method runs during initialization and whenever<br/>
        /// <see cref="SettingsChangedBroadcast.SettingsChanged"/> is raised (for example after<br/>
        /// activating or deactivating a licence in the Options dialog).</para>
        /// </remarks>
        private void ApplyPremiumState()
        {
            bool premium = LicenseManager.PremiumUnlocked;

            FilesTabRadio.IsEnabled = premium;
            FilesTabRadio.ToolTip = premium ? null : FilesPremiumToolTip;

            var lockVisibility = premium ? Visibility.Collapsed : Visibility.Visible;
            FilesTabLockIcon.Visibility = lockVisibility;
            FilesTabPremiumPill.Visibility = lockVisibility;

            if (!premium && FilesTabRadio.IsChecked == true)
            {
                // Premium was deactivated while the Files view was open — fall back to Tags.
                ActivateTagsTab(persist: false);
            }
        }

        /// <summary>
        /// Handles the <see cref="SettingsChangedBroadcast.SettingsChanged"/> event by re-evaluating<br/>
        /// the Premium gating of the Files tab on the UI thread.
        /// </summary>
        /// <param name="sender">
        /// The event source (unused — the broadcast raises with a <c>null</c> sender).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        private void OnSettingsChanged(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ApplyPremiumState();
            });
        }

        /// <summary>
        /// Shows the (free) Tags view, updating the segmented control, tree visibility,<br/>
        /// and ViewModel tab index to match.
        /// </summary>
        /// <param name="persist">
        /// When <c>true</c>, the selection is persisted to <see cref="RenderDocOptions.CommentExplorerView"/>.
        /// </param>
        private void ActivateTagsTab(bool persist)
        {
            _viewModel.SelectedTabIndex = 0;
            TagsTabRadio.IsChecked = true;
            FilesTabRadio.IsChecked = false;
            TagsTreeView.Visibility = Visibility.Visible;
            FilesTreeView.Visibility = Visibility.Collapsed;

            if (persist)
            {
                RenderDocOptions.Instance.CommentExplorerView = "Tags";
                RenderDocOptions.Instance.Save(_serviceProvider);
            }
        }

        /// <summary>
        /// Shows the Premium-only Files view, updating the segmented control, tree visibility,<br/>
        /// and ViewModel tab index to match.
        /// </summary>
        /// <param name="persist">
        /// When <c>true</c>, the selection is persisted to <see cref="RenderDocOptions.CommentExplorerView"/>.
        /// </param>
        private void ActivateFilesTab(bool persist)
        {
            _viewModel.SelectedTabIndex = 1;
            FilesTabRadio.IsChecked = true;
            TagsTabRadio.IsChecked = false;
            TagsTreeView.Visibility = Visibility.Collapsed;
            FilesTreeView.Visibility = Visibility.Visible;

            if (persist)
            {
                RenderDocOptions.Instance.CommentExplorerView = "Files";
                RenderDocOptions.Instance.Save(_serviceProvider);
            }
        }

        private void OnExpandAllClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.ExpandAll();
        }

        private void OnCollapseAllClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.CollapseAll();
        }

        private void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _scanner?.StartFullScan();
        }

        private void OnTreeViewDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            NavigateSelectedItem();
        }

        private void OnTreeViewKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == Key.Enter)
            {
                NavigateSelectedItem();
                e.Handled = true;
            }
        }

        private void NavigateSelectedItem()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var treeView = _viewModel.SelectedTabIndex == 0 ? TagsTreeView : FilesTreeView;
            if (treeView.SelectedItem is CommentItemNodeViewModel commentItem)
            {
                CommentNavigator.NavigateToLine(_serviceProvider, commentItem.FilePath, commentItem.LineNumber);
            }
            else if (treeView.SelectedItem is FileNodeViewModel fileNode)
            {
                CommentNavigator.NavigateToLine(_serviceProvider, fileNode.FilePath, 1);
            }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
            IsVisibleChanged -= OnIsVisibleChanged;
            if (_scanner != null)
            {
                _scanner.ActiveDocumentFound -= OnActiveDocumentFound;
                _scanner.Dispose();
            }
        }
    }
}
