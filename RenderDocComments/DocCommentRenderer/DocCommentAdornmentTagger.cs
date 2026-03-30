using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace RenderDocComments.DocCommentRenderer
{
    internal sealed class DocCommentAdornmentTagger
        : ITagger<IntraTextAdornmentTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly IWpfTextView _view;

        private ITextSnapshot _cachedSnapshot;
        private int _cachedSettingsGen = -1;
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> _cachedTags;

        private static int _settingsGeneration = 0;
        private bool _forceEmpty = false;
        private int _caretLine = -1;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public DocCommentAdornmentTagger(ITextBuffer buffer, IWpfTextView view)
        {
            _buffer = buffer;
            _view = view;

            _buffer.Changed += OnBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        // ── GetTags ───────────────────────────────────────────────────────────────

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(
            NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            if (_forceEmpty) yield break;
            if (!RenderDocOptions.Instance.RenderEnabled) yield break;

            var snapshot = spans[0].Snapshot;
            var tags = GetOrBuildTags(snapshot);

            foreach (var tag in tags)
            {
                if (RenderDocOptions.Instance.EffectiveGlyphToggle)
                {
                    if (DocCommentToggleState.IsHidden(new SnapshotSpan(snapshot, tag.Span)))
                        continue;
                }
                else
                {
                    if (_caretLine >= 0)
                    {
                        int s = snapshot.GetLineNumberFromPosition(tag.Span.Start);
                        int e = snapshot.GetLineNumberFromPosition(tag.Span.End);
                        if (_caretLine >= s && _caretLine <= e) continue;
                    }
                }

                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(tag.Span)))
                    yield return tag;
            }
        }

        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> GetOrBuildTags(ITextSnapshot snapshot)
        {
            if (_cachedSnapshot == snapshot &&
                _cachedSettingsGen == _settingsGeneration &&
                _cachedTags != null)
                return _cachedTags;

            _cachedSnapshot = snapshot;
            _cachedSettingsGen = _settingsGeneration;
            _cachedTags = BuildTags(snapshot);
            return _cachedTags;
        }

        // ── Doc-comment line detection ─────────────────────────────────────────────
        //
        // Recognises all three C# / VB / F# styles and all C++ Doxygen styles:
        //   C# / F# / VB:   ///  (three slashes)
        //   C++ line:        ///  (same)  or  //!  (QDoc / Doxygen alternative)
        //   C++ block open:  /**  or  /*!  on its own line (or with text on the same line)
        //   C++ block body:  lines starting with optional whitespace + *  (but not */)
        //   C++ block close: */ — marks the END of a block; included in the block
        //
        // The regex matches the FIRST line of a block and every interior/continuation line.
        // The block collector below drives the outer loop so it can span /** … */ across
        // multiple lines.

        // C# / F# — simple  ///
        private static readonly Regex CsDocLineRegex =
            new Regex(@"^\s*///", RegexOptions.Compiled);

        // C++ line-comment variant: ///  or  //!
        private static readonly Regex CppLineDocRegex =
            new Regex(@"^\s*(?:///|//!)", RegexOptions.Compiled);

        // C++ block opener: line containing  /**  or  /*!
        private static readonly Regex CppBlockOpenRegex =
            new Regex(@"^\s*/\*[*!]", RegexOptions.Compiled);

        // C++ block body / closer:  optional whitespace, then * (may include leading spaces)
        // This also matches */, which signals the block end.
        private static readonly Regex CppBlockBodyRegex =
            new Regex(@"^\s*\*", RegexOptions.Compiled);

        // ── C# member-name extractor ───────────────────────────────────────────────
        private static readonly Regex CsMemberNameRegex =
            new Regex(
                @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|" +
                @"abstract|sealed|async|extern|readonly|new|partial|unsafe)\s+)*" +
                @"(?:[\w<>\[\],\s\*\?]+?\s+)?(?<mem>\w+)\s*(?:<[^>]*>)?\s*(?:\(|{|=>|;)",
                RegexOptions.Compiled);

        // ── C++ member-name extractor ──────────────────────────────────────────────
        //
        // Handles free functions, member functions, constructors/destructors,
        // operators, class/struct/enum declarations, typedefs, using aliases,
        // global variables, template declarations, and constexpr / inline / static.
        private static readonly Regex CppMemberNameRegex =
            new Regex(
                // Optional template prefix
                @"^\s*(?:template\s*<[^>]*>\s*)?" +
                // Optional C++ specifiers (can appear in any order, multiple times)
                @"(?:(?:inline|static|virtual|explicit|extern|constexpr|consteval|constinit|" +
                @"override|final|__forceinline|__inline|__cdecl|__stdcall|__fastcall|" +
                @"[[nodiscard]]|__attribute__\s*\([^)]*\)|noexcept(?:\([^)]*\))?)\s*)*" +
                // Return type or keyword (class/struct/enum/typedef/using)
                @"(?:" +
                @"(?:class|struct|union|enum(?:\s+class)?|typedef|using)\s+" +
                @"|(?:[\w:<>\[\]\*&,\s]+?\s+)?" +   // return type (optional)
                @")" +
                // Destructor  ~Name
                @"(?:~)?(?<mem>\w+)" +
                // Function call / scope (to avoid matching closing braces as names)
                @"\s*(?:<[^>]*>)?\s*(?:\(|:|;|{|=\s*(?:default|delete|\d|{))",
                RegexOptions.Compiled);

        // ── Determine language for a buffer ───────────────────────────────────────

        private static bool IsCppBuffer(ITextBuffer buffer)
        {
            try
            {
                return buffer.ContentType.IsOfType("C/C++");
            }
            catch { return false; }
        }

        // ── BuildTags (three-pass) ────────────────────────────────────────────────

        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            int lineCount = snapshot.LineCount;
            bool isCpp = IsCppBuffer(_buffer);

            // Resolve the source file path / directory for <include> and cross-file search.
            string filePath = null;
            string fileDir = null;
            try
            {
                if (_buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument td))
                {
                    filePath = td.FilePath;
                    fileDir = Path.GetDirectoryName(filePath);
                }
            }
            catch { }

            // ── Pass 1: collect doc blocks + the member name after each ───────────
            var allBlocks = new List<(string raw, string memberName, SnapshotSpan span, string firstLine)>();

            int i = 0;
            while (i < lineCount)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                var lineText = line.GetText();

                if (isCpp)
                {
                    // ── C++ block comments  /** … */  or  /*! … */ ─────────────────
                    if (CppBlockOpenRegex.IsMatch(lineText))
                    {
                        var blockLines = new List<ITextSnapshotLine> { line };
                        // Collect until we find a line containing */
                        bool closed = lineText.Contains("*/");
                        i++;
                        while (!closed && i < lineCount)
                        {
                            var bodyLine = snapshot.GetLineFromLineNumber(i);
                            blockLines.Add(bodyLine);
                            if (bodyLine.GetText().Contains("*/"))
                                closed = true;
                            i++;
                        }

                        var memberName = PeekMemberName(snapshot, i, lineCount, isCpp);
                        var rawBlock = string.Join("\n", blockLines.Select(l => l.GetText()));
                        var blockSpan = new SnapshotSpan(snapshot,
                            Span.FromBounds(blockLines[0].Start,
                                            blockLines[blockLines.Count - 1].End));
                        allBlocks.Add((rawBlock, memberName, blockSpan, blockLines[0].GetText()));
                        continue;
                    }

                    // ── C++ line comments  ///  or  //! ───────────────────────────
                    if (CppLineDocRegex.IsMatch(lineText))
                    {
                        var blockLines = new List<ITextSnapshotLine>();
                        while (i < lineCount && CppLineDocRegex.IsMatch(
                                   snapshot.GetLineFromLineNumber(i).GetText()))
                            blockLines.Add(snapshot.GetLineFromLineNumber(i++));

                        var memberName = PeekMemberName(snapshot, i, lineCount, isCpp);
                        var rawBlock = string.Join("\n", blockLines.Select(l => l.GetText()));
                        var blockSpan = new SnapshotSpan(snapshot,
                            Span.FromBounds(blockLines[0].Start,
                                            blockLines[blockLines.Count - 1].End));
                        allBlocks.Add((rawBlock, memberName, blockSpan, blockLines[0].GetText()));
                        continue;
                    }

                    i++;
                }
                else
                {
                    // ── C# / F# / VB line comments  /// ───────────────────────────
                    if (!CsDocLineRegex.IsMatch(lineText)) { i++; continue; }

                    var blockLines = new List<ITextSnapshotLine>();
                    while (i < lineCount && CsDocLineRegex.IsMatch(
                               snapshot.GetLineFromLineNumber(i).GetText()))
                        blockLines.Add(snapshot.GetLineFromLineNumber(i++));

                    var memberName = PeekMemberName(snapshot, i, lineCount, isCpp);
                    var rawBlock = string.Join("\n", blockLines.Select(l => l.GetText()));
                    var blockSpan = new SnapshotSpan(snapshot,
                        Span.FromBounds(blockLines[0].Start,
                                        blockLines[blockLines.Count - 1].End));
                    allBlocks.Add((rawBlock, memberName, blockSpan, blockLines[0].GetText()));
                }
            }

            // ── Pass 2: parse every block; build name → doc lookup ────────────────
            var parsedByName = new Dictionary<string, ParsedDocComment>(StringComparer.Ordinal);
            var parsedList = new List<ParsedDocComment>(allBlocks.Count);
            var lang = isCpp ? DocCommentLanguage.Cpp : DocCommentLanguage.CSharp;

            foreach (var (raw, memberName, _, _) in allBlocks)
            {
                var parsed = DocCommentParser.Parse(raw, lang);
                parsedList.Add(parsed);
                if (parsed != null && parsed.IsValid && !string.IsNullOrEmpty(memberName))
                    if (!parsedByName.ContainsKey(memberName))
                        parsedByName[memberName] = parsed;
            }

            // ── Pass 3: resolve and emit tags ─────────────────────────────────────
            for (int idx = 0; idx < allBlocks.Count; idx++)
            {
                var (_, ownName, blockSpan, firstLine) = allBlocks[idx];
                var parsed = parsedList[idx];
                if (parsed == null || !parsed.IsValid) continue;

                // inheritdoc is a C#-only concept; skip resolution for C++.
                if (!isCpp && parsed.InheritDoc != null)
                    parsed = ResolveInheritDoc(parsed, ownName, parsedByName, fileDir, depth: 0);

                if (!isCpp && parsed.Include != null)
                    parsed = ResolveInclude(parsed, fileDir);

                if (parsed == null || !parsed.IsValid) continue;

                var tag = new DocCommentAdornmentTag(parsed, _view, MeasureIndent(firstLine));
                result.Add(new TagSpan<IntraTextAdornmentTag>(blockSpan, tag));
            }

            return result;
        }

        // ── Peek past blank / attribute / decorator lines to find the member name ──

        private static string PeekMemberName(
            ITextSnapshot snapshot, int startLine, int lineCount, bool isCpp)
        {
            string memberName = string.Empty;
            for (int peek = startLine; peek < lineCount; peek++)
            {
                var t = snapshot.GetLineFromLineNumber(peek).GetText();
                if (string.IsNullOrWhiteSpace(t)) continue;
                var trimmed = t.TrimStart();
                // Skip C# attributes  [Attribute]  and C++ preprocessor  #define
                if (trimmed.StartsWith("[") || trimmed.StartsWith("#")) continue;
                var m = isCpp
                    ? CppMemberNameRegex.Match(t)
                    : CsMemberNameRegex.Match(t);
                if (m.Success) memberName = m.Groups["mem"].Value;
                break;
            }
            return memberName;
        }

        // ── <inheritdoc> resolution ───────────────────────────────────────────────
        //
        // Resolution order for the source doc:
        //   1. In-file name dictionary  (same file, e.g. interface above the class).
        //   2. All other .cs files in the same directory tree (via DTE solution files
        //      or, if DTE is unavailable, a raw filesystem scan).
        //   3. Companion XML doc files in bin/obj output directories.
        //
        // In all cases the inheritor's own non-empty fields win; source fills blanks.

        private static ParsedDocComment ResolveInheritDoc(
            ParsedDocComment inheritor,
            string ownMemberName,
            Dictionary<string, ParsedDocComment> parsedByName,
            string fileDir,
            int depth)
        {
            if (depth > 5 || inheritor?.InheritDoc == null) return inheritor;

            var cref = inheritor.InheritDoc.Cref;
            string targetName = string.IsNullOrEmpty(cref)
                ? ownMemberName
                : DocCommentParser.SimplifyCref(cref);

            if (string.IsNullOrEmpty(targetName)) return inheritor;

            // 1 — in-file
            ParsedDocComment source = null;
            if (parsedByName.TryGetValue(targetName, out var inFile) &&
                !ReferenceEquals(inFile, inheritor))
                source = inFile;

            // 2 — other .cs files in the solution / directory
            if (source == null)
                source = FindInCsFiles(fileDir, targetName);

            // 3 — compiled XML doc files
            if (source == null)
                source = FindInXmlDocFiles(fileDir, targetName, cref);

            if (source == null) return inheritor;  // unresolvable — show fallback

            // Recurse if the found source itself inherits.
            if (source.InheritDoc != null)
                source = ResolveInheritDoc(source, targetName, parsedByName, fileDir, depth + 1);

            if (source == null || !source.IsValid) return inheritor;

            return Merge(inheritor, source, clearInheritDoc: true);
        }

        // ── Cross-file search through .cs source files ────────────────────────────

        /// <summary>
        /// Scans all *.cs files reachable from the solution (via DTE) or from the
        /// source file's directory tree (fallback) for a /// block whose following
        /// declaration has the given <paramref name="targetName"/>.
        /// </summary>
        private static ParsedDocComment FindInCsFiles(string fileDir, string targetName)
        {
            var csFiles = GetSolutionCsFiles();

            if (csFiles == null || csFiles.Count == 0)
            {
                if (fileDir == null) return null;
                try
                {
                    string root = fileDir;
                    for (int up = 0; up < 5; up++)
                    {
                        if (Directory.GetFiles(root, "*.sln").Length > 0 ||
                            Directory.GetFiles(root, "*.csproj").Length > 0)
                            break;
                        var parent = Path.GetDirectoryName(root);
                        if (parent == null) break;
                        root = parent;
                    }
                    csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                                       .ToList();
                }
                catch { return null; }
            }

            foreach (var csFile in csFiles)
            {
                try
                {
                    var doc = ScanCsFileForMember(csFile, targetName);
                    if (doc != null) return doc;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Returns the list of all .cs file paths open in the current VS solution,
        /// using DTE.  Returns null if DTE is unavailable.
        /// </summary>
        private static List<string> GetSolutionCsFiles()
        {
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package
                    .GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                if (dte?.Solution == null) return null;

                var files = new List<string>();
                CollectProjectItems(dte.Solution.Projects, files);
                return files;
            }
            catch { return null; }
        }

        private static void CollectProjectItems(EnvDTE.Projects projects, List<string> files)
        {
            if (projects == null) return;
            foreach (EnvDTE.Project project in projects)
            {
                try { CollectItems(project.ProjectItems, files); }
                catch { }
            }
        }

        private static void CollectItems(EnvDTE.ProjectItems items, List<string> files)
        {
            if (items == null) return;
            foreach (EnvDTE.ProjectItem item in items)
            {
                try
                {
                    for (short f = 1; f <= item.FileCount; f++)
                    {
                        var path = item.FileNames[f];
                        if (!string.IsNullOrEmpty(path) &&
                            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            files.Add(path);
                    }
                    CollectItems(item.ProjectItems, files);
                }
                catch { }
            }
        }

        private static readonly Regex _csDocLine =
            new Regex(@"^\s*///", RegexOptions.Compiled);

        private static readonly Regex _csMember =
            new Regex(
                @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|" +
                @"abstract|sealed|async|extern|readonly|new|partial|unsafe)\s+)*" +
                @"(?:[\w<>\[\],\s\*\?]+?\s+)?(?<mem>\w+)\s*(?:<[^>]*>)?\s*(?:\(|{|=>|;)",
                RegexOptions.Compiled);

        /// <summary>
        /// Reads a .cs file from disk and returns the parsed doc comment of the
        /// member named <paramref name="targetName"/>, or null if not found.
        /// </summary>
        private static ParsedDocComment ScanCsFileForMember(string filePath, string targetName)
        {
            string[] lines;
            try { lines = File.ReadAllLines(filePath); }
            catch { return null; }

            int lineCount = lines.Length;
            int i = 0;

            while (i < lineCount)
            {
                if (!_csDocLine.IsMatch(lines[i])) { i++; continue; }

                var blockLines = new List<string>();
                while (i < lineCount && _csDocLine.IsMatch(lines[i]))
                    blockLines.Add(lines[i++]);

                string memberName = string.Empty;
                for (int peek = i; peek < lineCount; peek++)
                {
                    var t = lines[peek];
                    if (string.IsNullOrWhiteSpace(t) || t.TrimStart().StartsWith("[")) continue;
                    var m = _csMember.Match(t);
                    if (m.Success) memberName = m.Groups["mem"].Value;
                    break;
                }

                if (!string.Equals(memberName, targetName, StringComparison.Ordinal))
                    continue;

                var rawBlock = string.Join("\n", blockLines);
                var parsed = DocCommentParser.Parse(rawBlock, DocCommentLanguage.CSharp);
                if (parsed != null && parsed.IsValid &&
                    !string.IsNullOrWhiteSpace(parsed.Summary))
                    return parsed;
            }

            return null;
        }

        // ── Cross-file search through C++ source / header files ───────────────────
        //
        // Scans *.h, *.hpp, *.hxx, *.cpp, *.cxx, *.cc files reachable from the
        // project root (via DTE or filesystem walk) for a /** / /*! / /// / //!
        // block whose following declaration matches targetName.
        // C++ projects do not have compiled XML doc files, so only source scanning
        // is provided; the XML fallback is still attempted for mixed projects.

        private static readonly string[] _cppExtensions =
        {
            "*.h", "*.hpp", "*.hxx", "*.h++",
            "*.cpp", "*.cxx", "*.cc", "*.c++", "*.c"
        };

        private static readonly Regex _cppLineDoc =
            new Regex(@"^\s*(?:///|//!)", RegexOptions.Compiled);

        private static readonly Regex _cppBlockOpen =
            new Regex(@"^\s*/\*[*!]", RegexOptions.Compiled);

        private static readonly Regex _cppMemberScan =
            new Regex(
                @"^\s*(?:template\s*<[^>]*>\s*)?" +
                @"(?:(?:inline|static|virtual|explicit|extern|constexpr|consteval|constinit|" +
                @"override|final|__forceinline|__inline|__cdecl|__stdcall|__fastcall)\s*)*" +
                @"(?:(?:class|struct|union|enum(?:\s+class)?|typedef|using)\s+" +
                @"|(?:[\w:<>\[\]\*&,\s]+?\s+)?)?" +
                @"(?:~)?(?<mem>\w+)" +
                @"\s*(?:<[^>]*>)?\s*(?:\(|:|;|{|=\s*(?:default|delete|\d|{))",
                RegexOptions.Compiled);

        /// <summary>
        /// Finds all C++ source / header files in the solution or project root.
        /// Uses DTE when available; falls back to a filesystem scan.
        /// </summary>
        private static List<string> GetSolutionCppFiles(string fileDir)
        {
            // Try DTE first.
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package
                    .GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                if (dte?.Solution != null)
                {
                    var files = new List<string>();
                    CollectCppProjectItems(dte.Solution.Projects, files);
                    if (files.Count > 0) return files;
                }
            }
            catch { }

            // Filesystem fallback.
            if (fileDir == null) return null;
            try
            {
                string root = fileDir;
                for (int up = 0; up < 6; up++)
                {
                    if (Directory.GetFiles(root, "*.sln").Length > 0
                        || Directory.GetFiles(root, "*.vcxproj").Length > 0
                        || Directory.GetFiles(root, "CMakeLists.txt").Length > 0)
                        break;
                    var parent = Path.GetDirectoryName(root);
                    if (parent == null) break;
                    root = parent;
                }

                var result = new List<string>();
                foreach (var ext in _cppExtensions)
                    try { result.AddRange(Directory.GetFiles(root, ext, SearchOption.AllDirectories)); }
                    catch { }
                return result;
            }
            catch { return null; }
        }

        private static void CollectCppProjectItems(EnvDTE.Projects projects, List<string> files)
        {
            if (projects == null) return;
            foreach (EnvDTE.Project project in projects)
            {
                try { CollectCppItems(project.ProjectItems, files); }
                catch { }
            }
        }

        private static readonly HashSet<string> _cppFileExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".h", ".hpp", ".hxx", ".h++", ".cpp", ".cxx", ".cc", ".c++", ".c" };

        private static void CollectCppItems(EnvDTE.ProjectItems items, List<string> files)
        {
            if (items == null) return;
            foreach (EnvDTE.ProjectItem item in items)
            {
                try
                {
                    for (short f = 1; f <= item.FileCount; f++)
                    {
                        var path = item.FileNames[f];
                        if (!string.IsNullOrEmpty(path) &&
                            _cppFileExts.Contains(Path.GetExtension(path)))
                            files.Add(path);
                    }
                    CollectCppItems(item.ProjectItems, files);
                }
                catch { }
            }
        }

        /// <summary>
        /// Reads a C++ source/header file and returns the parsed doc comment for
        /// the member named <paramref name="targetName"/>, or null if not found.
        /// </summary>
        private static ParsedDocComment ScanCppFileForMember(string filePath, string targetName)
        {
            string[] lines;
            try { lines = File.ReadAllLines(filePath); }
            catch { return null; }

            int lineCount = lines.Length;
            int i = 0;

            while (i < lineCount)
            {
                var line = lines[i];

                // ── Block comment opener  /** … */  or  /*! … */ ──────────────
                if (_cppBlockOpen.IsMatch(line))
                {
                    var blockLines = new List<string> { line };
                    bool closed = line.Contains("*/");
                    i++;
                    while (!closed && i < lineCount)
                    {
                        blockLines.Add(lines[i]);
                        if (lines[i].Contains("*/")) closed = true;
                        i++;
                    }

                    // Peek past blank lines for the member declaration.
                    string memberName = string.Empty;
                    for (int peek = i; peek < lineCount; peek++)
                    {
                        var t = lines[peek];
                        if (string.IsNullOrWhiteSpace(t) || t.TrimStart().StartsWith("#")) continue;
                        var m = _cppMemberScan.Match(t);
                        if (m.Success) memberName = m.Groups["mem"].Value;
                        break;
                    }

                    if (!string.Equals(memberName, targetName, StringComparison.Ordinal))
                        continue;

                    var rawBlock = string.Join("\n", blockLines);
                    var parsed = DocCommentParser.Parse(rawBlock, DocCommentLanguage.Cpp);
                    if (parsed != null && parsed.IsValid &&
                        !string.IsNullOrWhiteSpace(parsed.Summary))
                        return parsed;
                    continue;
                }

                // ── Line comments  ///  or  //! ───────────────────────────────
                if (_cppLineDoc.IsMatch(line))
                {
                    var blockLines = new List<string>();
                    while (i < lineCount && _cppLineDoc.IsMatch(lines[i]))
                        blockLines.Add(lines[i++]);

                    string memberName = string.Empty;
                    for (int peek = i; peek < lineCount; peek++)
                    {
                        var t = lines[peek];
                        if (string.IsNullOrWhiteSpace(t) || t.TrimStart().StartsWith("#")) continue;
                        var m = _cppMemberScan.Match(t);
                        if (m.Success) memberName = m.Groups["mem"].Value;
                        break;
                    }

                    if (!string.Equals(memberName, targetName, StringComparison.Ordinal))
                        continue;

                    var rawBlock = string.Join("\n", blockLines);
                    var parsed = DocCommentParser.Parse(rawBlock, DocCommentLanguage.Cpp);
                    if (parsed != null && parsed.IsValid &&
                        !string.IsNullOrWhiteSpace(parsed.Summary))
                        return parsed;
                    continue;
                }

                i++;
            }

            return null;
        }

        // ── Compiled XML doc file fallback ────────────────────────────────────────

        private static ParsedDocComment FindInXmlDocFiles(
            string fileDir, string targetSimpleName, string fullCref)
        {
            if (fileDir == null) return null;

            var dirs = new List<string> { fileDir };
            try
            {
                var parent = Path.GetDirectoryName(fileDir);
                if (parent != null)
                    dirs.AddRange(new[]
                    {
                        Path.Combine(parent, "bin", "Debug"),
                        Path.Combine(parent, "bin", "Release"),
                        Path.Combine(fileDir,  "bin", "Debug"),
                        Path.Combine(fileDir,  "bin", "Release"),
                    });
            }
            catch { }

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> xmlFiles;
                try { xmlFiles = Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var xmlFile in xmlFiles)
                {
                    try
                    {
                        var xdoc = XDocument.Load(xmlFile);
                        foreach (var member in xdoc.Descendants("member"))
                        {
                            var nameAttr = member.Attribute("name")?.Value ?? string.Empty;
                            bool matched =
                                nameAttr.EndsWith("." + targetSimpleName, StringComparison.Ordinal) ||
                                nameAttr.EndsWith("." + targetSimpleName + "(", StringComparison.Ordinal) ||
                                (!string.IsNullOrEmpty(fullCref) &&
                                 nameAttr.EndsWith(DocCommentParser.StripPrefix(fullCref),
                                     StringComparison.Ordinal));

                            if (!matched) continue;

                            var innerXml = string.Concat(member.Nodes().Select(n => n.ToString()));
                            var fakeBlock = string.Join("\n",
                                innerXml.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                                        .Select(l => "/// " + l));
                            var parsed = DocCommentParser.Parse(fakeBlock, DocCommentLanguage.CSharp);
                            if (parsed != null && parsed.IsValid &&
                                !string.IsNullOrWhiteSpace(parsed.Summary))
                                return parsed;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        // ── <include> resolution ──────────────────────────────────────────────────

        private static ParsedDocComment ResolveInclude(ParsedDocComment doc, string fileDir)
        {
            if (doc?.Include == null) return doc;
            try
            {
                var fullPath = Path.IsPathRooted(doc.Include.File)
                    ? doc.Include.File
                    : (fileDir != null
                        ? Path.Combine(fileDir, doc.Include.File)
                        : doc.Include.File);

                if (!File.Exists(fullPath)) return doc;

                var xdoc = XDocument.Load(fullPath);

                IEnumerable<XNode> nodes;
                if (string.IsNullOrEmpty(doc.Include.Path))
                {
                    nodes = xdoc.Root?.Nodes() ?? Enumerable.Empty<XNode>();
                }
                else
                {
                    var elements = xdoc.XPathSelectElements(doc.Include.Path).ToList();
                    if (elements.Count > 0)
                    {
                        nodes = (elements.Count == 1 &&
                                 elements[0].Name.LocalName == "member")
                            ? (IEnumerable<XNode>)elements[0].Nodes()
                            : elements.Cast<XNode>();
                    }
                    else
                    {
                        var single = xdoc.XPathSelectElement(doc.Include.Path);
                        nodes = single?.Nodes() ?? Enumerable.Empty<XNode>();
                    }
                }

                var sb = new StringBuilder();
                foreach (var node in nodes)
                    sb.AppendLine(node.ToString());

                var raw = sb.ToString();
                if (string.IsNullOrWhiteSpace(raw)) return doc;

                var fakeBlock = string.Join("\n",
                    raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                       .Select(l => "/// " + l));

                var source = DocCommentParser.Parse(fakeBlock, DocCommentLanguage.CSharp);
                if (source == null || !source.IsValid) return doc;

                var merged = Merge(doc, source, clearInheritDoc: false);
                merged.Include = null;
                return merged;
            }
            catch { return doc; }
        }

        // ── Merge helper ──────────────────────────────────────────────────────────

        private static ParsedDocComment Merge(
            ParsedDocComment a, ParsedDocComment b, bool clearInheritDoc)
        {
            var m = new ParsedDocComment { IsValid = true };
            m.Summary = Coalesce(a.Summary, b.Summary);
            m.Remarks = Coalesce(a.Remarks, b.Remarks);
            m.Returns = Coalesce(a.Returns, b.Returns);
            m.Example = Coalesce(a.Example, b.Example);
            m.Permission = Coalesce(a.Permission, b.Permission);
            m.PermissionCref = Coalesce(a.PermissionCref, b.PermissionCref);
            m.Params = MergeParams(a.Params, b.Params);
            m.TypeParams = MergeParams(a.TypeParams, b.TypeParams);
            m.Exceptions = Union(a.Exceptions, b.Exceptions, e => e.FullCref + e.Description);
            m.SeeAlsos = Union(a.SeeAlsos, b.SeeAlsos, s => s.Cref + s.Href + s.Label);
            m.CompletionList = Union(a.CompletionList, b.CompletionList, x => x);
            // C++ specific
            m.Brief = Coalesce(a.Brief, b.Brief);
            m.Note = Coalesce(a.Note, b.Note);
            m.Warning = Coalesce(a.Warning, b.Warning);
            m.Attention = Coalesce(a.Attention, b.Attention);
            m.Deprecated = Coalesce(a.Deprecated, b.Deprecated);
            m.Since = Coalesce(a.Since, b.Since);
            m.Version = Coalesce(a.Version, b.Version);
            m.Author = Coalesce(a.Author, b.Author);
            m.Date = Coalesce(a.Date, b.Date);
            m.Copyright = Coalesce(a.Copyright, b.Copyright);
            m.Bug = Coalesce(a.Bug, b.Bug);
            m.Todo = Coalesce(a.Todo, b.Todo);
            m.Pre = Coalesce(a.Pre, b.Pre);
            m.Post = Coalesce(a.Post, b.Post);
            m.Invariant = Coalesce(a.Invariant, b.Invariant);
            m.Remark = Coalesce(a.Remark, b.Remark);
            m.RetVals = Union(a.RetVals, b.RetVals, rv => rv.Name);
            m.SeeEntries = Union(a.SeeEntries, b.SeeEntries, x => x);
            if (clearInheritDoc) m.InheritDoc = null;
            return m;
        }

        private static string Coalesce(string a, string b)
            => string.IsNullOrWhiteSpace(a) ? b : a;

        private static List<ParamEntry> MergeParams(List<ParamEntry> a, List<ParamEntry> b)
        {
            var result = new List<ParamEntry>(a);
            var seen = new HashSet<string>(a.Select(p => p.Name), StringComparer.Ordinal);
            foreach (var p in b) if (seen.Add(p.Name)) result.Add(p);
            return result;
        }

        private static List<T> Union<T>(List<T> a, List<T> b, Func<T, string> key)
        {
            var result = new List<T>(a);
            var seen = new HashSet<string>(a.Select(key));
            foreach (var item in b) if (seen.Add(key(item))) result.Add(item);
            return result;
        }

        // ── Indent measurement ────────────────────────────────────────────────────

        private double MeasureIndent(string lineText)
        {
            int spaces = 0;
            foreach (char c in lineText)
            {
                if (c == ' ') { spaces++; continue; }
                if (c == '\t') { spaces += 4; continue; }
                break;
            }
            try
            {
                var cw = _view.FormattedLineSource?.ColumnWidth;
                if (cw.HasValue && cw.Value > 0) return spaces * cw.Value;
            }
            catch { }
            return spaces * 7.2;
        }

        // ── Event handlers ────────────────────────────────────────────────────────

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _cachedSnapshot = null;
            _cachedTags = null;
            var snap = e.After;
            TagsChanged?.Invoke(this,
                new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (RenderDocOptions.Instance.EffectiveGlyphToggle) return;

            int newLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
            if (newLine == _caretLine) return;
            int old = _caretLine;
            _caretLine = newLine;

            var snap = _buffer.CurrentSnapshot;
            var cached = _cachedTags;

            void Invalidate(int ln)
            {
                if (ln < 0 || ln >= snap.LineCount) return;
                if (cached != null)
                {
                    foreach (var ts in cached)
                    {
                        int s = snap.GetLineNumberFromPosition(ts.Span.Start);
                        int en = snap.GetLineNumberFromPosition(ts.Span.End);
                        if (ln >= s && ln <= en)
                        {
                            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(ts.Span));
                            return;
                        }
                    }
                }
                var l = snap.GetLineFromLineNumber(ln);
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                    new SnapshotSpan(snap, l.Start, l.LengthIncludingLineBreak)));
            }

            Invalidate(old);
            Invalidate(newLine);
        }

        /// <summary>
        /// Settings changed (options saved, or theme changed with auto-refresh on).
        /// Bump the generation counter so GetOrBuildTags treats the current cache as
        /// stale and rebuilds every tag with the new settings.
        /// </summary>
        private void OnSettingsChanged(object sender, EventArgs e)
        {
            System.Threading.Interlocked.Increment(ref _settingsGeneration);
            _cachedSnapshot = null;
            _cachedTags = null;

            var snap = _buffer.CurrentSnapshot;
            _forceEmpty = true;
            TagsChanged?.Invoke(this,
                new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));

            _view.VisualElement.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Normal,
                new Action(() =>
                {
                    _forceEmpty = false;
                    var snap2 = _buffer.CurrentSnapshot;
                    TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                        new SnapshotSpan(snap2, 0, snap2.Length)));
                }));
        }

        // ── IDisposable ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            _buffer.Changed -= OnBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}