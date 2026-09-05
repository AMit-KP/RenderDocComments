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

            try
            {
                _scanner = new CommentTagsScanner(_serviceProvider, _viewModel);
                _scanner.StartFullScan();
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = "Initialization error: " + ex.Message;
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
            _scanner.StartFullScan();
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
            if (TagsTreeView.SelectedItem is CommentItemNodeViewModel commentItem)
            {
                CommentNavigator.NavigateToLine(_serviceProvider, commentItem.FilePath, commentItem.LineNumber);
            }
        }

        public void Dispose()
        {
            _scanner?.Dispose();
        }
    }
}
