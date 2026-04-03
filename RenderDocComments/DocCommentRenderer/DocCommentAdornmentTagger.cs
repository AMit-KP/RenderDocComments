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
        // Language       Comment style          Prefix
        // ─────────────  ─────────────────────  ──────────────────────────────────────
        // C#             XML-doc                ///
        // F#             XML-doc                ///  (identical to C#)
        // VB.NET         XML-doc                '''  (triple apostrophe)
        // C++ (line)     Doxygen / XML-doc      ///  or  //!
        // C++ (block)    Doxygen / XML-doc      /** … */  or  /*! … */

        // C# / F# — triple slash
        private static readonly Regex CsDocLineRegex =
            new Regex(@"^\s*///", RegexOptions.Compiled);

        // VB.NET — triple apostrophe  '''
        private static readonly Regex VbDocLineRegex =
            new Regex(@"^\s*'''", RegexOptions.Compiled);

        // C++ line-comment variant: ///  or  //!
        private static readonly Regex CppLineDocRegex =
            new Regex(@"^\s*(?:///|//!)", RegexOptions.Compiled);

        // C++ block opener: line starting with  /**  or  /*!
        private static readonly Regex CppBlockOpenRegex =
            new Regex(@"^\s*/\*[*!]", RegexOptions.Compiled);

        // C++ block body / closer:  optional whitespace then *
        private static readonly Regex CppBlockBodyRegex =
            new Regex(@"^\s*\*", RegexOptions.Compiled);

        // ── C# member-name extractor ───────────────────────────────────────────────
        private static readonly Regex CsMemberNameRegex =
            new Regex(
                @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|" +
                @"abstract|sealed|async|extern|readonly|new|partial|unsafe)\s+)*" +
                @"(?:[\w<>\[\],\s\*\?]+?\s+)?(?<mem>\w+)\s*(?:<[^>]*>)?\s*(?:\(|{|=>|;)",
                RegexOptions.Compiled);

        // ── VB.NET member-name extractor ───────────────────────────────────────────
        //
        // Handles:  Sub / Function / Property / Class / Interface / Structure /
        //           Enum / Module / Event / Delegate / Operator / Constructor (New)
        // Modifiers: Public/Private/Protected/Friend/Shared/Overridable/Overrides/
        //            MustOverride/NotOverridable/Partial/Overloads/ReadOnly/WriteOnly/
        //            Shadows/Async/Iterator/WithEvents/Default/MustInherit/NotInheritable
        private static readonly Regex VbMemberNameRegex =
            new Regex(
                @"^\s*(?:(?:Public|Private|Protected|Friend|Shared|Overridable|Overrides|" +
                @"MustOverride|NotOverridable|Partial|Overloads|ReadOnly|WriteOnly|Shadows|" +
                @"Async|Iterator|WithEvents|Default|MustInherit|NotInheritable)\s+)*" +
                @"(?:Sub|Function|Property|Class|Interface|Structure|Enum|Module|" +
                @"Event|Delegate|Operator|ReadOnly|WriteOnly)\s+(?<mem>\w+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ── F# member-name extractor ───────────────────────────────────────────────
        //
        // Handles:  let / let rec / let inline / member / type / val / module /
        //           abstract member / static member / override / default
        //           self-identifiers (this. / _.) and backtick names (``my fn``)
        private static readonly Regex FsMemberNameRegex =
            new Regex(
                @"^\s*" +
                // optional leading access modifier
                @"(?:(?:private|internal|public)\s+)*" +
                // primary keyword — compound forms must come before single forms
                @"(?:(?:static\s+member|abstract\s+member|let\s+rec)" +
                @"|(?:let|type|val|module|member|override|default))\s+" +
                // optional secondary modifiers after the keyword
                @"(?:(?:private|internal|public|mutable|inline|rec)\s+)*" +
                // optional self-identifier   this.  _.  x.
                @"(?:(?:\w+|_)\.)*" +
                // the actual member name (backtick or plain identifier)
                @"(?<mem>``[^`]+``|\w+)",
                RegexOptions.Compiled);

        // ── C++ member-name extractor ──────────────────────────────────────────────
        private static readonly Regex CppMemberNameRegex =
            new Regex(
                @"^\s*(?:template\s*<[^>]*>\s*)?" +
                @"(?:(?:inline|static|virtual|explicit|extern|constexpr|consteval|constinit|" +
                @"override|final|__forceinline|__inline|__cdecl|__stdcall|__fastcall|" +
                @"[[nodiscard]]|__attribute__\s*\([^)]*\)|noexcept(?:\([^)]*\))?)\s*)*" +
                @"(?:" +
                @"(?:class|struct|union|enum(?:\s+class)?|typedef|using)\s+" +
                @"|(?:[\w:<>\[\]\*&,\s]+?\s+)?" +
                @")" +
                @"(?:~)?(?<mem>\w+)" +
                @"\s*(?:<[^>]*>)?\s*(?:\(|:|;|{|=\s*(?:default|delete|\d|{))",
                RegexOptions.Compiled);

        // ── Determine language for a buffer ───────────────────────────────────────

        private enum BufferLanguage
        {
            CSharp, VBNet, FSharp, Cpp
        }

        private static BufferLanguage GetLanguage(ITextBuffer buffer)
        {
            try
            {
                var ct = buffer.ContentType;
                if (ct.IsOfType("C/C++")) return BufferLanguage.Cpp;
                if (ct.IsOfType("Basic")) return BufferLanguage.VBNet;
                if (ct.IsOfType("F#") || ct.IsOfType("FSharp")) return BufferLanguage.FSharp;
                return BufferLanguage.CSharp;
            }
            catch { return BufferLanguage.CSharp; }
        }

        // Keep for backward-compat with the cross-file helpers that use it.
        private static bool IsCppBuffer(ITextBuffer buffer)
            => GetLanguage(buffer) == BufferLanguage.Cpp;

        // ── BuildTags (three-pass) ────────────────────────────────────────────────

        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            int lineCount = snapshot.LineCount;
            var bufLang = GetLanguage(_buffer);
            bool isCpp = bufLang == BufferLanguage.Cpp;
            bool isVb = bufLang == BufferLanguage.VBNet;

            // Resolve source file path for <include> and cross-file inheritdoc search.
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

            // Choose the correct line-doc regex for this language.
            Regex lineDocRegex = isVb ? VbDocLineRegex
                               : isCpp ? CppLineDocRegex
                               : CsDocLineRegex;

            // ── Pass 1: collect doc blocks + member name after each ───────────────
            var allBlocks = new List<(string raw, string memberName, SnapshotSpan span, string firstLine)>();

            int i = 0;
            while (i < lineCount)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                var lineText = line.GetText();

                if (isCpp)
                {
                    // ── C++ block comments /** ... */ or /*! ... */ ─────────────────
                    if (CppBlockOpenRegex.IsMatch(lineText))
                    {
                        var blockLines = new List<ITextSnapshotLine> { line };
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
                        var memberName = PeekMemberName(snapshot, i, lineCount, bufLang);
                        var rawBlock = string.Join("\n", blockLines.Select(l => l.GetText()));
                        var blockSpan = new SnapshotSpan(snapshot,
                            Span.FromBounds(blockLines[0].Start,
                                            blockLines[blockLines.Count - 1].End));
                        allBlocks.Add((rawBlock, memberName, blockSpan, blockLines[0].GetText()));
                        continue;
                    }

                    // ── C++ line comments /// or //! ───────────────────────────────
                    if (CppLineDocRegex.IsMatch(lineText))
                    {
                        var blockLines = new List<ITextSnapshotLine>();
                        while (i < lineCount && CppLineDocRegex.IsMatch(
                                   snapshot.GetLineFromLineNumber(i).GetText()))
                            blockLines.Add(snapshot.GetLineFromLineNumber(i++));
                        var memberName = PeekMemberName(snapshot, i, lineCount, bufLang);
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
                    // ── C# / F# (///) or VB.NET triple-apostrophe line comments ────
                    if (!lineDocRegex.IsMatch(lineText)) { i++; continue; }

                    var blockLines = new List<ITextSnapshotLine>();
                    while (i < lineCount && lineDocRegex.IsMatch(
                               snapshot.GetLineFromLineNumber(i).GetText()))
                        blockLines.Add(snapshot.GetLineFromLineNumber(i++));

                    var memberName = PeekMemberName(snapshot, i, lineCount, bufLang);
                    var rawBlock = string.Join("\n", blockLines.Select(l => l.GetText()));
                    var blockSpan = new SnapshotSpan(snapshot,
                        Span.FromBounds(blockLines[0].Start,
                                        blockLines[blockLines.Count - 1].End));
                    allBlocks.Add((rawBlock, memberName, blockSpan, blockLines[0].GetText()));
                }
            }

            // ── Pass 2: parse every block; build name lookup ──────────────────────
            var parsedByName = new Dictionary<string, ParsedDocComment>(StringComparer.Ordinal);
            var parsedList = new List<ParsedDocComment>(allBlocks.Count);
            // VB / F# / C# all use the XML-doc parser. C++ auto-detects XML vs Doxygen.
            var parseLang = isCpp ? DocCommentLanguage.Cpp : DocCommentLanguage.CSharp;

            foreach (var (raw, memberName, _, _) in allBlocks)
            {
                var parsed = DocCommentParser.Parse(raw, parseLang);
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

                // <inheritdoc> / <include> are only meaningful for XML-doc languages.
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

        // ── Peek past blanks / attributes / decorators to find the member name ────

        private static string PeekMemberName(
            ITextSnapshot snapshot, int startLine, int lineCount, BufferLanguage lang)
        {
            string memberName = string.Empty;
            for (int peek = startLine; peek < lineCount; peek++)
            {
                var t = snapshot.GetLineFromLineNumber(peek).GetText();
                if (string.IsNullOrWhiteSpace(t)) continue;
                var trimmed = t.TrimStart();
                // Skip C#/F# [Attribute], VB <Attribute>, C++ #preprocessor lines.
                if (trimmed.StartsWith("[") || trimmed.StartsWith("#")) continue;
                if (lang == BufferLanguage.VBNet && trimmed.StartsWith("<")) continue;
                Match m;
                switch (lang)
                {
                    case BufferLanguage.Cpp: m = CppMemberNameRegex.Match(t); break;
                    case BufferLanguage.VBNet: m = VbMemberNameRegex.Match(t); break;
                    case BufferLanguage.FSharp: m = FsMemberNameRegex.Match(t); break;
                    default: m = CsMemberNameRegex.Match(t); break;
                }
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
        // ── Cross-file search through managed source files (.cs / .vb / .fs) ────────
        //
        // Used by <inheritdoc> resolution to find the target member's doc in other
        // source files.  Handles C#, VB.NET and F# files; picks the right doc-line
        // regex and member-name regex for each extension.

        private static readonly HashSet<string> _managedExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".vb", ".fs", ".fsi" };

        // Doc-line regex by extension (used when scanning files from disk).
        private static Regex DocLineRegexForExt(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".vb": return new Regex(@"^\s*'''", RegexOptions.Compiled);
                default: return new Regex(@"^\s*///", RegexOptions.Compiled);
            }
        }

        // Member-name regex by extension.
        private static Regex MemberRegexForExt(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".vb": return VbMemberNameRegex;
                case ".fs":
                case ".fsi": return FsMemberNameRegex;
                default: return CsMemberNameRegex;
            }
        }

        // Prefix strip pattern by extension (for ParsedDocComment.Parse).
        private static string StripPatternForExt(string ext)
            => ext.ToLowerInvariant() == ".vb" ? @"^\s*'''\s?" : @"^\s*///\s?";

        private static ParsedDocComment FindInCsFiles(string fileDir, string targetName)
        {
            var files = GetSolutionManagedFiles();

            if (files == null || files.Count == 0)
            {
                if (fileDir == null) return null;
                try
                {
                    string root = fileDir;
                    for (int up = 0; up < 5; up++)
                    {
                        if (Directory.GetFiles(root, "*.sln").Length > 0 ||
                            Directory.GetFiles(root, "*.csproj").Length > 0 ||
                            Directory.GetFiles(root, "*.vbproj").Length > 0 ||
                            Directory.GetFiles(root, "*.fsproj").Length > 0)
                            break;
                        var parent = Path.GetDirectoryName(root);
                        if (parent == null) break;
                        root = parent;
                    }
                    files = new List<string>();
                    foreach (var ext in new[] { "*.cs", "*.vb", "*.fs", "*.fsi" })
                        try { files.AddRange(Directory.GetFiles(root, ext, SearchOption.AllDirectories)); }
                        catch { }
                }
                catch { return null; }
            }

            foreach (var file in files)
            {
                try
                {
                    var doc = ScanManagedFileForMember(file, targetName);
                    if (doc != null) return doc;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Returns all .cs / .vb / .fs / .fsi paths in the current VS solution via DTE.
        /// Returns null when DTE is unavailable.
        /// </summary>
        private static List<string> GetSolutionManagedFiles()
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
                            _managedExts.Contains(Path.GetExtension(path)))
                            files.Add(path);
                    }
                    CollectItems(item.ProjectItems, files);
                }
                catch { }
            }
        }

        /// <summary>
        /// Reads a managed source file (.cs / .vb / .fs) and returns the parsed doc
        /// comment for the member named <paramref name="targetName"/>, or null.
        /// </summary>
        private static ParsedDocComment ScanManagedFileForMember(string filePath, string targetName)
        {
            string[] lines;
            try { lines = File.ReadAllLines(filePath); }
            catch { return null; }

            var ext = Path.GetExtension(filePath);
            var docRegex = DocLineRegexForExt(ext);
            var memRegex = MemberRegexForExt(ext);
            var stripPat = StripPatternForExt(ext);

            int lineCount = lines.Length;
            int i = 0;

            while (i < lineCount)
            {
                if (!docRegex.IsMatch(lines[i])) { i++; continue; }

                var blockLines = new List<string>();
                while (i < lineCount && docRegex.IsMatch(lines[i]))
                    blockLines.Add(lines[i++]);

                string memberName = string.Empty;
                for (int peek = i; peek < lineCount; peek++)
                {
                    var t = lines[peek];
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    var tr = t.TrimStart();
                    if (tr.StartsWith("[") || tr.StartsWith("#")) continue;
                    // VB attribute lines start with <
                    if (ext.Equals(".vb", StringComparison.OrdinalIgnoreCase)
                        && tr.StartsWith("<")) continue;
                    var m = memRegex.Match(t);
                    if (m.Success) memberName = m.Groups["mem"].Value;
                    break;
                }

                if (!string.Equals(memberName, targetName, StringComparison.Ordinal))
                    continue;

                // Re-strip using the correct prefix for this language before parsing.
                var stripped = blockLines.Select(l =>
                    System.Text.RegularExpressions.Regex.Replace(l, stripPat, ""));
                var rawBlock = string.Join("\n", stripped.Select(l => "/// " + l));
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