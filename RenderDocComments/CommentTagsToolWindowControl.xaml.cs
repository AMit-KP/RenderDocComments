using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;

namespace RenderDocComments
{
    /// <summary>
    /// Interaction logic for CommentTagsToolWindowControl.xaml.
    /// </summary>
    public partial class CommentTagsToolWindowControl : UserControl, IDisposable
    {
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

            // Restore last active view from options
            string savedView = RenderDocOptions.Instance.CommentExplorerView;
            if (string.Equals(savedView, "Files", StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.SelectedTabIndex = 1;
                FilesTabRadio.IsChecked = true;
                TagsTabRadio.IsChecked = false;
                TagsTreeView.Visibility = Visibility.Collapsed;
                FilesTreeView.Visibility = Visibility.Visible;
            }
            else
            {
                _viewModel.SelectedTabIndex = 0;
                TagsTabRadio.IsChecked = true;
                FilesTabRadio.IsChecked = false;
                TagsTreeView.Visibility = Visibility.Visible;
                FilesTreeView.Visibility = Visibility.Collapsed;
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
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                if (_viewModel.SelectedTabIndex == 1 && fileNode != null)
                {
                    ScrollToFileNode(fileNode);
                }
            }));
        }

        private void ScrollToFileNode(FileNodeViewModel fileNode)
        {
            if (fileNode == null) return;
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
        }

        private void OnTabRadioClicked(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (FilesTabRadio.IsChecked == true)
            {
                _viewModel.SelectedTabIndex = 1;
                TagsTreeView.Visibility = Visibility.Collapsed;
                FilesTreeView.Visibility = Visibility.Visible;
                RenderDocOptions.Instance.CommentExplorerView = "Files";
                RenderDocOptions.Instance.Save(_serviceProvider);
                _scanner?.HighlightActiveDocument();
            }
            else
            {
                _viewModel.SelectedTabIndex = 0;
                TagsTreeView.Visibility = Visibility.Visible;
                FilesTreeView.Visibility = Visibility.Collapsed;
                RenderDocOptions.Instance.CommentExplorerView = "Tags";
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
            if (e.Key == Key.Enter)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
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
            if (_scanner != null)
            {
                _scanner.ActiveDocumentFound -= OnActiveDocumentFound;
                _scanner.Dispose();
            }
        }
    }
}
