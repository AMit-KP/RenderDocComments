using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using RenderDocComments.DocCommentRenderer.TagBadges;
using Task = System.Threading.Tasks.Task;

namespace RenderDocComments
{
    /// <summary>
    /// Scans solution files for conventional comment tags (TODO, FIXME, etc.) with real-time live typing updates,
    /// solution event tracking, and comment boundary parsing matching CommentTagBadgeTagger.
    /// </summary>
    internal sealed class CommentTagsScanner : IVsRunningDocTableEvents, IVsSolutionEvents, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly CommentTagsTreeViewModel _viewModel;
        private readonly DTE2 _dte;
        private readonly IVsSolution _solution;
        private readonly RunningDocumentTable _rdt;
        private uint _rdtCookie;
        private uint _solutionCookie;
        private WindowEvents _windowEvents;

        public event EventHandler<FileNodeViewModel> ActiveDocumentFound;

        private ITextDocumentFactoryService _textDocumentFactoryService;
        private IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;

        private CancellationTokenSource _scanCts;
        private readonly Dictionary<string, List<TagOccurrence>> _fileOccurrences =
            new Dictionary<string, List<TagOccurrence>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<ITextBuffer, string> _trackedBuffers =
            new Dictionary<ITextBuffer, string>();

        private readonly Dictionary<string, ITextBuffer> _openBuffersByPath =
            new Dictionary<string, ITextBuffer>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, DispatcherTimer> _debounceTimers =
            new Dictionary<string, DispatcherTimer>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex TagRegex = new Regex(
            @"\b(TODO|FIXME|HACK|NOTE|BUG|REVIEW|OPTIMIZE|TEMP|WARNING|WARN|" +
            @"DEPRECATED|CHANGED|SAFETY|INVARIANT|ASSUME|MAGIC)\b",
            RegexOptions.Compiled);

        private static readonly Regex TrailingCloser = new Regex(
            @"\s*\*/\s*$", RegexOptions.Compiled);

        private static readonly char[] NewlineChars = { '\r', '\n' };

        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".vb", ".fs", ".cpp", ".c", ".h", ".hpp", ".hxx", ".cxx", ".inl"
        };

        private enum BufferLanguage { CSharp, VBNet, FSharp, Cpp }

        private struct CommentRange
        {
            public int Start;
            public int End;
            public int PrefixLen;
            public CommentRange(int start, int end, int prefixLen)
            {
                Start = start;
                End = end;
                PrefixLen = prefixLen;
            }
        }

        public class TagOccurrence
        {
            public string CanonicalTag { get; set; }
            public int LineNumber { get; set; }
            public string CleanText { get; set; }
            public string FilePath { get; set; }
        }

        public CommentTagsScanner(IServiceProvider serviceProvider, CommentTagsTreeViewModel viewModel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _serviceProvider = serviceProvider ?? ServiceProvider.GlobalProvider;
            _viewModel = viewModel;

            try
            {
                _dte = _serviceProvider.GetService(typeof(DTE)) as DTE2
                    ?? ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE2;
                _solution = _serviceProvider.GetService(typeof(SVsSolution)) as IVsSolution
                    ?? ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution;

                if (_solution != null)
                {
                    _solution.AdviseSolutionEvents(this, out _solutionCookie);
                }

                var sp = _serviceProvider ?? ServiceProvider.GlobalProvider;
                _rdt = new RunningDocumentTable(sp);
                _rdtCookie = _rdt.Advise(this);

                // MEF Services for live buffer tracking
                var componentModel = sp.GetService(typeof(SComponentModel)) as IComponentModel
                    ?? ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;

                if (componentModel != null)
                {
                    _textDocumentFactoryService = componentModel.GetService<ITextDocumentFactoryService>();
                    _editorAdaptersFactoryService = componentModel.GetService<IVsEditorAdaptersFactoryService>();

                    if (_textDocumentFactoryService != null)
                    {
                        _textDocumentFactoryService.TextDocumentCreated += OnTextDocumentCreated;
                        _textDocumentFactoryService.TextDocumentDisposed += OnTextDocumentDisposed;
                    }
                }

                if (_dte?.Events != null)
                {
                    _windowEvents = _dte.Events.WindowEvents;
                    if (_windowEvents != null)
                    {
                        _windowEvents.WindowActivated += OnWindowActivated;
                    }
                }

                TrackCurrentlyOpenDocuments();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommentTagsScanner] Service initialization warning: {ex.Message}");
            }

            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        private void TrackCurrentlyOpenDocuments()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_rdt == null) return;

            try
            {
                foreach (var docInfo in _rdt)
                {
                    string moniker = docInfo.Moniker;
                    if (!string.IsNullOrEmpty(moniker) && IsEligibleFile(moniker, null))
                    {
                        if (docInfo.DocData is IVsTextBuffer vsBuffer && _editorAdaptersFactoryService != null)
                        {
                            var buffer = _editorAdaptersFactoryService.GetDataBuffer(vsBuffer);
                            if (buffer != null)
                            {
                                RegisterBuffer(buffer, moniker);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void OnTextDocumentCreated(object sender, TextDocumentEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.TextDocument?.TextBuffer != null && !string.IsNullOrEmpty(e.TextDocument.FilePath))
            {
                RegisterBuffer(e.TextDocument.TextBuffer, e.TextDocument.FilePath);
            }
        }

        private void OnTextDocumentDisposed(object sender, TextDocumentEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.TextDocument?.TextBuffer != null)
            {
                UnregisterBuffer(e.TextDocument.TextBuffer);
            }
        }

        private void RegisterBuffer(ITextBuffer buffer, string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (buffer == null || string.IsNullOrWhiteSpace(filePath) || !IsEligibleFile(filePath, null))
                return;

            lock (_trackedBuffers)
            {
                if (_trackedBuffers.ContainsKey(buffer))
                    return;

                _trackedBuffers[buffer] = filePath;
                _openBuffersByPath[filePath] = buffer;
            }

            buffer.Changed += OnBufferChanged;

            // Immediately scan the open buffer in memory
            RescanBuffer(buffer, filePath);
        }

        private void UnregisterBuffer(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string filePath = null;
            lock (_trackedBuffers)
            {
                if (_trackedBuffers.TryGetValue(buffer, out filePath))
                {
                    _trackedBuffers.Remove(buffer);
                    _openBuffersByPath.Remove(filePath);
                }
            }

            if (buffer != null)
            {
                buffer.Changed -= OnBufferChanged;
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                lock (_debounceTimers)
                {
                    if (_debounceTimers.TryGetValue(filePath, out var timer))
                    {
                        timer.Stop();
                        _debounceTimers.Remove(filePath);
                    }
                }
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (!(sender is ITextBuffer buffer)) return;

            string filePath;
            lock (_trackedBuffers)
            {
                if (!_trackedBuffers.TryGetValue(buffer, out filePath))
                    return;
            }

            // Debounce live typing updates (250ms)
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                lock (_debounceTimers)
                {
                    if (_debounceTimers.TryGetValue(filePath, out var existingTimer))
                    {
                        existingTimer.Stop();
                    }
                    else
                    {
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                        timer.Tick += (s, args) =>
                        {
                            timer.Stop();
                            lock (_debounceTimers) { _debounceTimers.Remove(filePath); }
                            RescanBuffer(buffer, filePath);
                        };
                        _debounceTimers[filePath] = timer;
                        existingTimer = timer;
                    }

                    existingTimer.Start();
                }
            });
        }

        private void RescanBuffer(ITextBuffer buffer, string filePath)
        {
            if (buffer == null || string.IsNullOrWhiteSpace(filePath)) return;

            string snapshotText;
            try
            {
                snapshotText = buffer.CurrentSnapshot.GetText();
            }
            catch
            {
                return;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                List<TagOccurrence> list = null;
                await Task.Run(() =>
                {
                    list = ScanText(snapshotText, filePath);
                });

                lock (_fileOccurrences)
                {
                    if (list != null && list.Count > 0)
                        _fileOccurrences[filePath] = list;
                    else
                        _fileOccurrences.Remove(filePath);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RebuildTreeFromOccurrences();
                _viewModel.StatusMessage = $"Updated. {_viewModel.TotalCount} tags found.";
            });
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _viewModel.RefreshColors();
            });
        }

        /// <summary>
        /// Triggers a full asynchronous scan of all project items in the open solution.
        /// </summary>
        public void StartFullScan()
        {
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _viewModel.IsScanning = true;
                _viewModel.StatusMessage = "Scanning solution for comment tags...";

                var filesToScan = new List<string>();
                string solutionDir = null;

                try
                {
                    TrackCurrentlyOpenDocuments();

                    if (_dte?.Solution?.IsOpen == true)
                    {
                        string solutionFile = _dte.Solution.FullName;
                        if (!string.IsNullOrEmpty(solutionFile))
                        {
                            solutionDir = Path.GetDirectoryName(solutionFile);
                        }

                        CollectSolutionFiles(_dte.Solution.Projects, filesToScan, solutionDir);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CommentTagsScanner] Error collecting files: {ex.Message}");
                }

                await Task.Run(() =>
                {
                    if (token.IsCancellationRequested) return;

                    var occurrencesByFile = new Dictionary<string, List<TagOccurrence>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var file in filesToScan.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (token.IsCancellationRequested) return;

                        try
                        {
                            string textToScan = null;

                            // If file is open in editor, use the live in-memory snapshot
                            ITextBuffer openBuffer = null;
                            lock (_trackedBuffers)
                            {
                                _openBuffersByPath.TryGetValue(file, out openBuffer);
                            }

                            if (openBuffer != null)
                            {
                                textToScan = openBuffer.CurrentSnapshot.GetText();
                            }
                            else if (File.Exists(file))
                            {
                                textToScan = File.ReadAllText(file);
                            }

                            if (textToScan != null)
                            {
                                var list = ScanText(textToScan, file);
                                if (list.Count > 0)
                                {
                                    occurrencesByFile[file] = list;
                                }
                            }
                        }
                        catch { /* skip unreadable files */ }
                    }

                    lock (_fileOccurrences)
                    {
                        _fileOccurrences.Clear();
                        foreach (var kvp in occurrencesByFile)
                        {
                            _fileOccurrences[kvp.Key] = kvp.Value;
                        }
                    }
                }, token);

                if (token.IsCancellationRequested) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RebuildTreeFromOccurrences();
                _viewModel.IsScanning = false;
                _viewModel.StatusMessage = $"Scan complete. {_viewModel.TotalCount} tags found.";
            });
        }

        /// <summary>
        /// Rescans a single file and updates the tree view.
        /// </summary>
        public void RescanSingleFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !IsEligibleFile(filePath, null))
                return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                ITextBuffer openBuffer = null;
                lock (_trackedBuffers)
                {
                    _openBuffersByPath.TryGetValue(filePath, out openBuffer);
                }

                if (openBuffer != null)
                {
                    RescanBuffer(openBuffer, filePath);
                    return;
                }

                List<TagOccurrence> list = null;
                await Task.Run(() =>
                {
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            string text = File.ReadAllText(filePath);
                            list = ScanText(text, filePath);
                        }
                        catch { }
                    }

                    lock (_fileOccurrences)
                    {
                        if (list != null && list.Count > 0)
                            _fileOccurrences[filePath] = list;
                        else
                            _fileOccurrences.Remove(filePath);
                    }
                });

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RebuildTreeFromOccurrences();
            });
        }

        private static bool IsEligibleFile(string filePath, string solutionDir)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            string ext = Path.GetExtension(filePath);
            if (!SupportedExtensions.Contains(ext)) return false;

            string normalized = filePath.Replace('/', '\\');

            // Exclude temporary, appdata, packages, obj, bin, and generated paths
            if (normalized.IndexOf(@"\AppData\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\Temp\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\.vs\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\.git\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\node_modules\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\packages\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\TestResults\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            // Exclude generated code files
            string fileName = Path.GetFileName(filePath);
            if (fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // If solutionDir is available, ensure the file is within the solution folder tree
            if (!string.IsNullOrEmpty(solutionDir))
            {
                string normSol = solutionDir.TrimEnd('\\') + "\\";
                if (!normalized.StartsWith(normSol, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static BufferLanguage GetLanguage(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            if (string.Equals(ext, ".vb", StringComparison.OrdinalIgnoreCase))
                return BufferLanguage.VBNet;
            if (string.Equals(ext, ".fs", StringComparison.OrdinalIgnoreCase))
                return BufferLanguage.FSharp;
            if (string.Equals(ext, ".cpp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".c", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".hpp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".hxx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".cxx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".inl", StringComparison.OrdinalIgnoreCase))
                return BufferLanguage.Cpp;

            return BufferLanguage.CSharp;
        }

        private static List<TagOccurrence> ScanText(string fullText, string filePath)
        {
            var results = new List<TagOccurrence>();
            if (string.IsNullOrEmpty(fullText)) return results;

            var opts = RenderDocOptions.Instance;
            var lang = GetLanguage(filePath);

            var lineStarts = ComputeLineStarts(fullText);
            var ranges = new List<CommentRange>();
            CollectCommentRanges(fullText, lineStarts, lang, ranges);

            foreach (var range in ranges)
            {
                int len = range.End - range.Start;
                if (len <= 0 || range.Start + len > fullText.Length) continue;

                string text = fullText.Substring(range.Start, len);

                foreach (Match m in TagRegex.Matches(text))
                {
                    // 1. Anchoring check: match must only be preceded by whitespace or block decorator '*' on its comment line
                    if (!IsAnchored(text, range.PrefixLen, m.Index)) continue;

                    // 2. Normalization check
                    if (!TagBadgeCatalog.TryNormalize(m.Value, out var canonical)) continue;

                    // 3. RenderDocOptions enabled check
                    if (!opts.EffectiveTagEnabled(canonical)) continue;

                    int absPos = range.Start + m.Index;
                    int lineNumber = GetLineNumberFromPosition(lineStarts, absPos);

                    string cleanTail = ExtractTail(text, m.Index + m.Length);
                    if (string.IsNullOrWhiteSpace(cleanTail))
                        cleanTail = "(no description)";

                    results.Add(new TagOccurrence
                    {
                        CanonicalTag = canonical,
                        LineNumber = lineNumber,
                        CleanText = cleanTail,
                        FilePath = filePath
                    });
                }
            }

            return results;
        }

        private static bool IsAnchored(string text, int contentStart, int matchIndex)
        {
            int lineStart = contentStart;
            for (int i = matchIndex - 1; i >= contentStart; i--)
            {
                if (text[i] == '\n') { lineStart = i + 1; break; }
            }
            for (int i = lineStart; i < matchIndex; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || c == '*') continue;
                return false;
            }
            return true;
        }

        private static string ExtractTail(string rangeText, int tailStart)
        {
            if (tailStart >= rangeText.Length) return string.Empty;
            var tail = rangeText.Substring(tailStart);

            int nl = tail.IndexOfAny(NewlineChars);
            if (nl >= 0) tail = tail.Substring(0, nl);

            tail = TrailingCloser.Replace(tail, string.Empty);
            tail = tail.TrimStart(':', ' ', '\t');

            if (tail.Length > 200)
                tail = tail.Substring(0, 200) + "…";
            return tail;
        }

        private static List<int> ComputeLineStarts(string text)
        {
            var list = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    list.Add(i + 1);
                }
            }
            return list;
        }

        private static int GetLineNumberFromPosition(List<int> lineStarts, int position)
        {
            int idx = lineStarts.BinarySearch(position);
            if (idx >= 0) return idx + 1;
            return ~idx;
        }

        private static void CollectCommentRanges(
            string text, List<int> lineStarts, BufferLanguage lang, List<CommentRange> ranges)
        {
            bool isVb = lang == BufferLanguage.VBNet;
            bool isFSharp = lang == BufferLanguage.FSharp;

            bool inBlock = false;
            bool inDocBlock = false;
            char close1 = '/';
            char close2 = '/';
            int blockStartAbs = -1;
            int blockPrefixLen = 2;

            int lineCount = lineStarts.Count;
            for (int ln = 0; ln < lineCount; ln++)
            {
                int lineStartPos = lineStarts[ln];
                int lineEndPos = (ln + 1 < lineCount) ? lineStarts[ln + 1] - 1 : text.Length;
                if (lineEndPos > lineStartPos && text[lineEndPos - 1] == '\r') lineEndPos--;

                int lineLength = lineEndPos - lineStartPos;
                if (lineLength <= 0) continue;

                string t = text.Substring(lineStartPos, lineLength);
                int n = t.Length;
                bool inString = false;

                int i = 0;
                while (i < n)
                {
                    char c = t[i];

                    // ── Inside a block comment: only look for the closer ──────────
                    if (inBlock || inDocBlock)
                    {
                        if (i + 1 < n && t[i] == close1 && t[i + 1] == close2)
                        {
                            if (inBlock)
                            {
                                ranges.Add(new CommentRange(
                                    blockStartAbs,
                                    lineStartPos + i + 2,
                                    blockPrefixLen));
                            }
                            inBlock = false;
                            inDocBlock = false;
                            i += 2;
                            continue;
                        }
                        i++;
                        continue;
                    }

                    // ── String literal toggle (double quotes, escape-aware) ───────
                    if (c == '"')
                    {
                        if (!(inString && i > 0 && t[i - 1] == '\\'))
                            inString = !inString;
                        i++;
                        continue;
                    }
                    if (inString) { i++; continue; }

                    if (isVb)
                    {
                        // ── VB: apostrophe starts a comment (''' is XML-doc) ──────
                        if (c == '\'')
                        {
                            if (i + 2 < n && t[i + 1] == '\'' && t[i + 2] == '\'')
                                break; // doc comment — skip rest of line entirely
                            ranges.Add(new CommentRange(lineStartPos + i, lineEndPos, 1));
                            break;
                        }
                        i++;
                        continue;
                    }

                    // ── Slash languages: C#, F#, C++ ──────────────────────────────
                    if (c == '/' && i + 1 < n)
                    {
                        char d = t[i + 1];

                        if (d == '/')
                        {
                            bool isDoc = i + 2 < n && (t[i + 2] == '/' || t[i + 2] == '!');
                            if (isDoc) break; // /// //// //! — skip line
                            ranges.Add(new CommentRange(lineStartPos + i, lineEndPos, 2));
                            break;
                        }

                        if (d == '*')
                        {
                            bool isDoc = i + 2 < n && (t[i + 2] == '*' || t[i + 2] == '!');

                            if (isDoc)
                            {
                                inDocBlock = true; close1 = '*'; close2 = '/';
                            }
                            else
                            {
                                inBlock = true; close1 = '*'; close2 = '/';
                                blockStartAbs = lineStartPos + i;
                            }
                            i += 2;
                            continue;
                        }
                    }

                    if (isFSharp && c == '(' && i + 1 < n && t[i + 1] == '*'
                        && !IsLinterAnnotation(t, i))
                    {
                        inBlock = true; close1 = '*'; close2 = ')';
                        blockStartAbs = lineStartPos + i;
                        i += 2;
                        continue;
                    }

                    i++;
                }
            }
        }

        private static bool IsLinterAnnotation(string t, int openParenIndex)
        {
            if (openParenIndex + 2 >= t.Length) return false;
            return t[openParenIndex + 2] == '$';
        }

        private void OnWindowActivated(Window gotFocus, Window lostFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string path = gotFocus?.Document?.FullName;
                if (!string.IsNullOrEmpty(path))
                {
                    HighlightActiveDocument(path);
                }
            }
            catch { }
        }

        public void HighlightActiveDocument(string docPath = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(docPath))
            {
                try
                {
                    docPath = _dte?.ActiveDocument?.FullName;
                }
                catch { }
            }

            // Reset selection, active state, and collapse all files in Files view
            foreach (var f in _viewModel.Files)
            {
                f.IsExpanded = false;
                f.IsSelected = false;
                f.IsActiveFile = false;
            }

            if (string.IsNullOrEmpty(docPath))
            {
                ActiveDocumentFound?.Invoke(this, null);
                return;
            }

            var fileNode = _viewModel.FindFileNode(docPath);
            if (fileNode != null)
            {
                fileNode.IsExpanded = true;
                fileNode.IsSelected = true;
                fileNode.IsActiveFile = true;
                ActiveDocumentFound?.Invoke(this, fileNode);
            }
            else
            {
                ActiveDocumentFound?.Invoke(this, null);
            }
        }

        private void RebuildTreeFromOccurrences()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var expandedTagFiles = new HashSet<string>(
                _viewModel.Tags.SelectMany(t => t.Files).Where(f => f.IsExpanded).Select(f => f.FilePath),
                StringComparer.OrdinalIgnoreCase);

            var allOccurrences = new List<TagOccurrence>();
            lock (_fileOccurrences)
            {
                foreach (var list in _fileOccurrences.Values)
                {
                    allOccurrences.AddRange(list);
                }
            }

            // 1. Tags View (Tag -> File -> Occurrences)
            var byTag = allOccurrences
                .GroupBy(o => o.CanonicalTag, StringComparer.Ordinal)
                .OrderBy(g => GetTagOrder(g.Key))
                .ToList();

            _viewModel.Tags.Clear();
            int total = 0;

            foreach (var tagGroup in byTag)
            {
                if (!tagGroup.Any()) continue;

                // TagNodeViewModel restores expansion state from RenderDocOptions.Instance.CollapsedTags
                var tagNode = new TagNodeViewModel(tagGroup.Key)
                {
                    Count = tagGroup.Count()
                };
                total += tagNode.Count;

                var byFile = tagGroup
                    .GroupBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => Path.GetFileName(g.Key));

                foreach (var fileGroup in byFile)
                {
                    var fileNode = new FileNodeViewModel(fileGroup.Key)
                    {
                        Count = fileGroup.Count(),
                        IsExpanded = expandedTagFiles.Contains(fileGroup.Key)
                    };

                    foreach (var item in fileGroup.OrderBy(o => o.LineNumber))
                    {
                        fileNode.Comments.Add(new CommentItemNodeViewModel(
                            item.LineNumber,
                            item.CleanText,
                            item.FilePath,
                            item.CanonicalTag));
                    }

                    tagNode.Files.Add(fileNode);
                }

                _viewModel.Tags.Add(tagNode);
            }

            // 2. Files View (File -> Occurrences arranged by LineNumber)
            var byFilePath = allOccurrences
                .GroupBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => Path.GetFileName(g.Key));

            _viewModel.Files.Clear();

            foreach (var fileGroup in byFilePath)
            {
                var fileNode = new FileNodeViewModel(fileGroup.Key)
                {
                    Count = fileGroup.Count(),
                    IsExpanded = false // Keep all files collapsed by default in Files view
                };

                foreach (var item in fileGroup.OrderBy(o => o.LineNumber))
                {
                    fileNode.Comments.Add(new CommentItemNodeViewModel(
                        item.LineNumber,
                        item.CleanText,
                        item.FilePath,
                        item.CanonicalTag));
                }

                _viewModel.Files.Add(fileNode);
            }

            _viewModel.TotalCount = total;

            // Highlight active document in Files view if available
            HighlightActiveDocument();
        }

        private static int GetTagOrder(string tagName)
        {
            for (int i = 0; i < TagBadgeCatalog.Tags.Count; i++)
            {
                if (string.Equals(TagBadgeCatalog.Tags[i].Name, tagName, StringComparison.Ordinal))
                    return i;
            }
            return 999;
        }

        private void CollectSolutionFiles(Projects projects, List<string> files, string solutionDir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (projects == null) return;

            foreach (Project project in projects)
            {
                CollectProjectFiles(project?.ProjectItems, files, solutionDir);
            }
        }

        private void CollectProjectFiles(ProjectItems items, List<string> files, string solutionDir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return;

            foreach (ProjectItem item in items)
            {
                try
                {
                    for (short i = 1; i <= item.FileCount; i++)
                    {
                        string fileName = item.FileNames[i];
                        if (IsEligibleFile(fileName, solutionDir))
                        {
                            files.Add(fileName);
                        }
                    }
                }
                catch { /* some items don't have file names */ }

                if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                {
                    CollectProjectFiles(item.ProjectItems, files, solutionDir);
                }

                if (item.SubProject != null)
                {
                    CollectProjectFiles(item.SubProject.ProjectItems, files, solutionDir);
                }
            }
        }

        #region IVsRunningDocTableEvents Implementation

        public int OnAfterSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var docInfo = _rdt.GetDocumentInfo(docCookie);
                if (!string.IsNullOrEmpty(docInfo.Moniker))
                {
                    RescanSingleFile(docInfo.Moniker);
                }
            }
            catch { }
            return VSConstants.S_OK;
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var docInfo = _rdt.GetDocumentInfo(docCookie);
                if (!string.IsNullOrEmpty(docInfo.Moniker) && IsEligibleFile(docInfo.Moniker, null))
                {
                    if (docInfo.DocData is IVsTextBuffer vsBuffer && _editorAdaptersFactoryService != null)
                    {
                        var buffer = _editorAdaptersFactoryService.GetDataBuffer(vsBuffer);
                        if (buffer != null)
                        {
                            RegisterBuffer(buffer, docInfo.Moniker);
                        }
                    }
                }
            }
            catch { }
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;

        #endregion

        #region IVsSolutionEvents Implementation

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StartFullScan();
            return VSConstants.S_OK;
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock (_fileOccurrences)
            {
                _fileOccurrences.Clear();
            }
            _viewModel.Tags.Clear();
            _viewModel.Files.Clear();
            _viewModel.TotalCount = 0;
            _viewModel.StatusMessage = "No solution open.";
            return VSConstants.S_OK;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StartFullScan();
            return VSConstants.S_OK;
        }

        public int OnAfterCloseProject(IVsHierarchy pHierarchy, int fRemoved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StartFullScan();
            return VSConstants.S_OK;
        }

        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;

        #endregion

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _scanCts?.Cancel();
            _scanCts?.Dispose();

            if (_windowEvents != null)
            {
                _windowEvents.WindowActivated -= OnWindowActivated;
                _windowEvents = null;
            }

            if (_rdtCookie != 0)
            {
                _rdt?.Unadvise(_rdtCookie);
                _rdtCookie = 0;
            }

            if (_solutionCookie != 0)
            {
                _solution?.UnadviseSolutionEvents(_solutionCookie);
                _solutionCookie = 0;
            }

            if (_textDocumentFactoryService != null)
            {
                _textDocumentFactoryService.TextDocumentCreated -= OnTextDocumentCreated;
                _textDocumentFactoryService.TextDocumentDisposed -= OnTextDocumentDisposed;
            }

            lock (_trackedBuffers)
            {
                foreach (var buffer in _trackedBuffers.Keys)
                {
                    buffer.Changed -= OnBufferChanged;
                }
                _trackedBuffers.Clear();
                _openBuffersByPath.Clear();
            }

            lock (_debounceTimers)
            {
                foreach (var timer in _debounceTimers.Values)
                {
                    timer.Stop();
                }
                _debounceTimers.Clear();
            }

            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}
