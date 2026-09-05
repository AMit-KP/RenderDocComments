using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using RenderDocComments.DocCommentRenderer.TagBadges;

namespace RenderDocComments
{
    /// <summary>
    /// Base class for ViewModels implementing INotifyPropertyChanged.
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// Represents a single tag occurrence line item under a file in the tree.
    /// </summary>
    public class CommentItemNodeViewModel : ViewModelBase
    {
        private Brush _tagColorBrush;
        private Brush _tagForegroundBrush;

        public int LineNumber { get; set; }
        public string CleanCommentText { get; set; }
        public string FilePath { get; set; }
        public string TagName { get; set; }

        public Brush TagColorBrush
        {
            get => _tagColorBrush;
            private set => SetProperty(ref _tagColorBrush, value);
        }

        public Brush TagForegroundBrush
        {
            get => _tagForegroundBrush;
            private set => SetProperty(ref _tagForegroundBrush, value);
        }

        public string DisplayTitle => $"Line {LineNumber}: {CleanCommentText}";

        public CommentItemNodeViewModel(int lineNumber, string cleanCommentText, string filePath, string tagName = null)
        {
            LineNumber = lineNumber;
            CleanCommentText = cleanCommentText;
            FilePath = filePath;
            TagName = tagName;
            UpdateColors();
        }

        public void UpdateColors()
        {
            if (string.IsNullOrEmpty(TagName)) return;

            Color color = RenderDocOptions.Instance.EffectiveTagColor(TagName);
            var bgBrush = new SolidColorBrush(color);
            bgBrush.Freeze();
            TagColorBrush = bgBrush;

            Color fgColor = TagBadgeCatalog.GetAdaptiveForeground(color);
            var fgBrush = new SolidColorBrush(fgColor);
            fgBrush.Freeze();
            TagForegroundBrush = fgBrush;
        }
    }

    /// <summary>
    /// Represents a file node containing one or more tag occurrences.
    /// </summary>
    public class FileNodeViewModel : ViewModelBase
    {
        private bool _isExpanded;
        private bool _isSelected;
        private bool _isActiveFile;
        private int _count;

        public string FileName { get; set; }
        public string FilePath { get; set; }

        public int Count
        {
            get => _count;
            set => SetProperty(ref _count, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsActiveFile
        {
            get => _isActiveFile;
            set => SetProperty(ref _isActiveFile, value);
        }

        public ObservableCollection<CommentItemNodeViewModel> Comments { get; } = new ObservableCollection<CommentItemNodeViewModel>();

        public FileNodeViewModel(string filePath)
        {
            FilePath = filePath;
            FileName = System.IO.Path.GetFileName(filePath);
        }
    }

    /// <summary>
    /// Represents a root comment tag node (e.g. TODO, FIXME, HACK) containing file groupings.
    /// </summary>
    public class TagNodeViewModel : ViewModelBase
    {
        private bool _isExpanded = true;
        private int _count;
        private Brush _tagColorBrush;
        private Brush _tagForegroundBrush;

        public string TagName { get; }

        public int Count
        {
            get => _count;
            set => SetProperty(ref _count, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    RenderDocOptions.Instance.SetTagCollapsed(TagName, !value);
                }
            }
        }

        public Brush TagColorBrush
        {
            get => _tagColorBrush;
            private set => SetProperty(ref _tagColorBrush, value);
        }

        public Brush TagForegroundBrush
        {
            get => _tagForegroundBrush;
            private set => SetProperty(ref _tagForegroundBrush, value);
        }

        public ObservableCollection<FileNodeViewModel> Files { get; } = new ObservableCollection<FileNodeViewModel>();

        public TagNodeViewModel(string tagName)
        {
            TagName = tagName;
            _isExpanded = !RenderDocOptions.Instance.IsTagCollapsed(tagName);
            UpdateColors();
        }

        public void UpdateColors()
        {
            Color color = RenderDocOptions.Instance.EffectiveTagColor(TagName);
            var bgBrush = new SolidColorBrush(color);
            bgBrush.Freeze();
            TagColorBrush = bgBrush;

            Color fgColor = TagBadgeCatalog.GetAdaptiveForeground(color);
            var fgBrush = new SolidColorBrush(fgColor);
            fgBrush.Freeze();
            TagForegroundBrush = fgBrush;
        }
    }

    /// <summary>
    /// Main ViewModel backing the Comment Tags Explorer tool window tree.
    /// </summary>
    public class CommentTagsTreeViewModel : ViewModelBase
    {
        private bool _isScanning;
        private string _statusMessage = "Ready";
        private int _totalCount;
        private int _selectedTabIndex;

        public ObservableCollection<TagNodeViewModel> Tags { get; } = new ObservableCollection<TagNodeViewModel>();
        public ObservableCollection<FileNodeViewModel> Files { get; } = new ObservableCollection<FileNodeViewModel>();

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => SetProperty(ref _selectedTabIndex, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set => SetProperty(ref _isScanning, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public int TotalCount
        {
            get => _totalCount;
            set => SetProperty(ref _totalCount, value);
        }

        public void ExpandAll()
        {
            if (SelectedTabIndex == 0)
            {
                foreach (var tag in Tags)
                {
                    tag.IsExpanded = true;
                    RenderDocOptions.Instance.SetTagCollapsed(tag.TagName, false);
                    foreach (var file in tag.Files)
                    {
                        file.IsExpanded = true;
                    }
                }
            }
            else
            {
                foreach (var file in Files)
                {
                    file.IsExpanded = true;
                }
            }
        }

        public void CollapseAll()
        {
            if (SelectedTabIndex == 0)
            {
                foreach (var tag in Tags)
                {
                    tag.IsExpanded = false;
                    RenderDocOptions.Instance.SetTagCollapsed(tag.TagName, true);
                    foreach (var file in tag.Files)
                    {
                        file.IsExpanded = false;
                    }
                }
            }
            else
            {
                foreach (var file in Files)
                {
                    file.IsExpanded = false;
                }
            }
        }

        public void RefreshColors()
        {
            foreach (var tag in Tags)
            {
                tag.UpdateColors();
            }
            foreach (var file in Files)
            {
                foreach (var comment in file.Comments)
                {
                    comment.UpdateColors();
                }
            }
        }

        public FileNodeViewModel FindFileNode(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            return Files.FirstOrDefault(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Simple relay command for WPF MVVM binding.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => _execute();

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}

