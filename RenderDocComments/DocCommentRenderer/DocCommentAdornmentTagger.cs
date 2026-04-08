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
    /// <summary>
    /// Provides tagging support for documentation comment adornments in the Visual Studio editor.<br/>
    /// This class implements <see cref="ITagger{T}"/> to create <see cref="IntraTextAdornmentTag"/> instances<br/>
    /// that render formatted documentation blocks in place of raw XML comment syntax.
    /// </summary>
    /// <remarks>
    /// The tagger performs a three-pass analysis over the text buffer to identify, parse, and render<br/>
    /// documentation comments for multiple languages including C#, F#, VB.NET, and C++.<br/>
    /// <para>Key responsibilities include:</para>
    /// <list type="bullet">
    /// <item><description>Detecting documentation comment blocks using language-specific regex patterns.</description></item>
    /// <item><description>Extracting member names by peeking past attributes and blank lines.</description></item>
    /// <item><description>Parsing XML documentation structure via <see cref="DocCommentParser"/>.</description></item>
    /// <item><description>Resolving <c>&lt;inheritdoc&gt;</c> and <c>&lt;include&gt;</c> directives across files.</description></item>
    /// <item><description>Creating adornment tags that render <see cref="DocCommentControl"/> instances.</description></item>
    /// <item><description>Managing cache invalidation based on buffer changes, caret position, and settings updates.</description></item>
    /// </list>
    /// <para>The tagger intelligently hides rendered comments when the caret enters the comment region<br/>
    /// (in caret-based mode) or when the user manually toggles visibility (in glyph mode).</para>
    /// </remarks>
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
        private volatile int _caretLine = -1;

        /// <summary>
        /// Event raised when the set of tags has changed for a given snapshot span.<br/>
        /// This event is part of the <see cref="ITagger{T}"/> contract and notifies the<br/>
        /// Visual Studio editor to re-query tags for the affected regions.
        /// </summary>
        /// <remarks>
        /// The event is triggered in the following scenarios:
        /// <list type="bullet">
        /// <item><description>Buffer text changes (content edited).</description></item>
        /// <item><description>Layout changes that modify viewport dimensions.</description></item>
        /// <item><description>Caret position moves into or out of a documentation comment (caret mode).</description></item>
        /// <item><description>Settings change broadcast (options modified, theme updated).</description></item>
        /// </list>
        /// </remarks>
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        /// <summary>
        /// Initializes a new instance of the <see cref="DocCommentAdornmentTagger"/> class.<br/>
        /// Constructs a tagger bound to a specific text buffer and WPF text view, subscribing<br/>
        /// to buffer changes, caret movements, layout updates, view closure, and settings broadcasts.
        /// </summary>
        /// <param name="buffer">
        /// The <see cref="ITextBuffer"/> representing the document being edited.<br/>
        /// Used to access text content, snapshot management, and buffer change notifications.
        /// </param>
        /// <param name="view">
        /// The <see cref="IWpfTextView"/> providing visual context including caret position,<br/>
        /// viewport dimensions, font metrics, and format map for theme colors.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="buffer"/> or <paramref name="view"/> is null.
        /// </exception>
        /// <remarks>
        /// The constructor registers event handlers for:
        /// <list type="bullet">
        /// <item><description><see cref="ITextBuffer.Changed"/> — invalidates tag cache on text edits.</description></item>
        /// <item><description><see cref="ITextCaret.PositionChanged"/> — manages caret-based visibility toggling.</description></item>
        /// <item><description><see cref="IWpfTextView.LayoutChanged"/> — rebuilds tags when viewport resizes.</description></item>
        /// <item><description><see cref="IWpfTextView.Closed"/> — clears cached state on view closure.</description></item>
        /// <item><description><see cref="SettingsChangedBroadcast.SettingsChanged"/> — bumps generation counter for settings updates.</description></item>
        /// </list>
        /// These subscriptions ensure the tagger responds dynamically to editor state changes.
        /// </remarks>
        public DocCommentAdornmentTagger(ITextBuffer buffer, IWpfTextView view)
        {
            _buffer = buffer;
            _view = view;

            _buffer.Changed += OnBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        // ── GetTags ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Retrieves the collection of intra-text adornment tags for the specified snapshot spans.<br/>
        /// This method is called by the Visual Studio editor to obtain tags that should be rendered<br/>
        /// within the visible viewport region.
        /// </summary>
        /// <param name="spans">
        /// A <see cref="NormalizedSnapshotSpanCollection"/> representing the regions of the<br/>
        /// text buffer that need tag information. Typically corresponds to the visible viewport.
        /// </param>
        /// <returns>
        /// An enumerable collection of <see cref="ITagSpan{IntraTextAdornmentTag}"/> instances<br/>
        /// that intersect with the requested spans. Each tag represents a rendered documentation comment.
        /// </returns>
        /// <remarks>
        /// <para>The method applies multiple filtering layers before yielding tags:</para>
        /// <list type="number">
        /// <item><description><b>Empty check:</b> Returns immediately if <paramref name="spans"/> is empty.</description></item>
        /// <item><description><b>Force empty flag:</b> During settings transitions, temporarily suppresses all tags.</description></item>
        /// <item><description><b>Render enabled:</b> Respects the global render toggle from <see cref="RenderDocOptions"/>.</description></item>
        /// <item><description><b>Visibility mode:</b>
        ///   <list type="bullet">
        ///   <item><description><b>Glyph mode:</b> Skips tags where <see cref="DocCommentToggleState.IsHidden"/> indicates the user collapsed the comment.</description></item>
        ///   <item><description><b>Caret mode:</b> Skips tags where the caret line falls within the tag's span, allowing raw XML editing.</description></item>
        ///   </list>
        /// </description></item>
        /// <item><description><b>Intersection test:</b> Only yields tags that overlap with the requested <paramref name="spans"/>.</description></item>
        /// </list>
        /// <para>This filtering ensures that documentation rendering stays out of the user's way<br/>
        /// when editing raw XML while providing polished previews during review.</para>
        /// </remarks>
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

        /// <summary>
        /// Retrieves cached tags for the given snapshot or builds them if the cache is stale.<br/>
        /// This method implements a two-level cache invalidation strategy based on snapshot identity<br/>
        /// and settings generation counter.
        /// </summary>
        /// <param name="snapshot">
        /// The <see cref="ITextSnapshot"/> to retrieve tags for. Snapshots are immutable;<br/>
        /// a new snapshot indicates buffer content has changed.
        /// </param>
        /// <returns>
        /// A read-only list of <see cref="TagSpan{IntraTextAdornmentTag}"/> representing all<br/>
        /// documentation comment adornments in the snapshot.
        /// </returns>
        /// <remarks>
        /// <para>Cache invalidation occurs when:</para>
        /// <list type="bullet">
        /// <item><description><b>Snapshot mismatch:</b> The cached snapshot differs from the requested one (content edited).</description></item>
        /// <item><description><b>Settings generation change:</b> The static <see cref="_settingsGeneration"/> counter has been incremented by <see cref="OnSettingsChanged"/>.</description></item>
        /// </list>
        /// <para>When either condition is detected, <see cref="BuildTags"/> is invoked to reconstruct<br/>
        /// all tags with current buffer content and settings.</para>
        /// </remarks>
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

        /// <summary>
        /// Regular expression for detecting C# / F# XML documentation line comments.<br/>
        /// Matches lines starting with optional whitespace followed by triple forward slashes (<c>///</c>).
        /// </summary>
        /// <remarks>
        /// Pattern: <c>^\s*///</c><br/>
        /// This regex is used for both C# and F# since they share identical XML documentation syntax.
        /// </remarks>
        private static readonly Regex CsDocLineRegex =
            new Regex(@"^\s*///", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for detecting VB.NET XML documentation line comments.<br/>
        /// Matches lines starting with optional whitespace followed by triple apostrophes (<c>'''</c>).
        /// </summary>
        /// <remarks>
        /// Pattern: <c>^\s*'''</c><br/>
        /// VB.NET uses triple apostrophes instead of forward slashes for XML documentation comments.
        /// </remarks>
        private static readonly Regex VbDocLineRegex =
            new Regex(@"^\s*'''", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for detecting C++ Doxygen/XML documentation line comments.<br/>
        /// Matches lines starting with optional whitespace followed by either <c>///</c> or <c>//!</c>.
        /// </summary>
        /// <remarks>
        /// Pattern: <c>^\s*(?:///|//!)</c><br/>
        /// C++ supports both triple-slash (<c>///</c>) and double-slash-exclamation (<c>//!</c>)<br/>
        /// documentation comment styles commonly used with Doxygen.
        /// </remarks>
        private static readonly Regex CppLineDocRegex =
            new Regex(@"^\s*(?:///|//!)", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for detecting C++ block comment openers.<br/>
        /// Matches lines starting with optional whitespace followed by <c>/**</c> or <c>/*!</c>.
        /// </summary>
        /// <remarks>
        /// Pattern: <c>^\s*/\*[*!]</c><br/>
        /// Block comments span multiple lines until the closing <c>*/</c> is encountered.<br/>
        /// The <c>/*!</c> variant is a Doxygen convention to mark documentation blocks.
        /// </remarks>
        private static readonly Regex CppBlockOpenRegex =
            new Regex(@"^\s*/\*[*!]", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for detecting C++ block comment body lines.<br/>
        /// Matches lines starting with optional whitespace followed by an asterisk (<c>*</c>).
        /// </summary>
        /// <remarks>
        /// Pattern: <c>^\s*\*</c><br/>
        /// Used to identify continuation lines within <c>/** ... */</c> block comments.
        /// </remarks>
        private static readonly Regex CppBlockBodyRegex =
            new Regex(@"^\s*\*", RegexOptions.Compiled);

        // ── C# member-name extractor ───────────────────────────────────────────────
        
        /// <summary>
        /// Regular expression for extracting C# member names from declaration lines.<br/>
        /// Captures method, property, field, class, struct, interface, enum, and delegate names.
        /// </summary>
        /// <remarks>
        /// <para>Matches member declarations preceded by optional modifiers including:</para>
        /// <list type="bullet">
        /// <item><description>Access modifiers: <c>public</c>, <c>private</c>, <c>protected</c>, <c>internal</c></description></item>
        /// <item><description>Static/instance: <c>static</c>, <c>extern</c>, <c>readonly</c></description></item>
        /// <item><description>Virtual dispatch: <c>virtual</c>, <c>override</c>, <c>abstract</c>, <c>sealed</c></description></item>
        /// <item><description>Other: <c>async</c>, <c>new</c>, <c>partial</c>, <c>unsafe</c></description></item>
        /// </list>
        /// <para>The named capture group <c>mem</c> contains the extracted member identifier.</para>
        /// </remarks>
        private static readonly Regex CsMemberNameRegex =
            new Regex(
                @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|" +
                @"abstract|sealed|async|extern|readonly|new|partial|unsafe)\s+)*" +
                @"(?:[\w<>\[\],\s\*\?]+?\s+)?(?<mem>\w+)\s*(?:<[^>]*>)?\s*(?:\(|{|=>|;)",
                RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for extracting VB.NET member names from declaration lines.<br/>
        /// Handles a comprehensive set of member types and modifiers specific to VB.NET syntax.
        /// </summary>
        /// <remarks>
        /// <para>Supported member types:</para>
        /// <list type="bullet">
        /// <item><description>Methods: <c>Sub</c>, <c>Function</c></description></item>
        /// <item><description>Properties: <c>Property</c>, <c>ReadOnly</c>, <c>WriteOnly</c></description></item>
        /// <item><description>Types: <c>Class</c>, <c>Interface</c>, <c>Structure</c>, <c>Enum</c>, <c>Module</c></description></item>
        /// <item><description>Delegates: <c>Event</c>, <c>Delegate</c>, <c>Operator</c></description></item>
        /// </list>
        /// <para>Supported modifiers include <c>Public</c>, <c>Private</c>, <c>Protected</c>, <c>Friend</c>,<br/>
        /// <c>Shared</c>, <c>Overridable</c>, <c>Overrides</c>, <c>MustOverride</c>, <c>Partial</c>, <c>Async</c>, etc.</para>
        /// <para>The named capture group <c>mem</c> contains the extracted member identifier.<br/>
        /// Matching is case-insensitive per VB.NET language conventions.</para>
        /// </remarks>
        private static readonly Regex VbMemberNameRegex =
            new Regex(
                @"^\s*(?:(?:Public|Private|Protected|Friend|Shared|Overridable|Overrides|" +
                @"MustOverride|NotOverridable|Partial|Overloads|ReadOnly|WriteOnly|Shadows|" +
                @"Async|Iterator|WithEvents|Default|MustInherit|NotInheritable)\s+)*" +
                @"(?:Sub|Function|Property|Class|Interface|Structure|Enum|Module|" +
                @"Event|Delegate|Operator|ReadOnly|WriteOnly)\s+(?<mem>\w+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Regular expression for extracting F# member names from declaration lines.<br/>
        /// Handles F#-specific constructs including backtick-quoted names and self-identifiers.
        /// </summary>
        /// <remarks>
        /// <para>Supported keywords and patterns:</para>
        /// <list type="bullet">
        /// <item><description>Bindings: <c>let</c>, <c>let rec</c>, <c>let inline</c></description></item>
        /// <item><description>Members: <c>member</c>, <c>static member</c>, <c>abstract member</c></description></item>
        /// <item><description>Types: <c>type</c>, <c>val</c></description></item>
        /// <item><description>Modules: <c>module</c></description></item>
        /// <item><description>Overrides: <c>override</c>, <c>default</c></description></item>
        /// <item><description>Self-identifiers: <c>this.</c>, <c>_.</c>, or custom identifiers</description></item>
        /// <item><description>Backtick names: <c>``my function``</c> syntax</description></item>
        /// </list>
        /// <para>Compound forms like <c>static member</c> are matched before single keywords<br/>
        /// to prevent incorrect partial matches. The named capture group <c>mem</c> contains<br/>
        /// the extracted member identifier, supporting both plain and backtick-quoted names.</para>
        /// </remarks>
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

        /// <summary>
        /// Regular expression for extracting C++ member names from declaration lines.<br/>
        /// Handles C++-specific constructs including templates, specifiers, and attributes.
        /// </summary>
        /// <remarks>
        /// <para>Supported patterns:</para>
        /// <list type="bullet">
        /// <item><description>Template declarations: <c>template&lt;...&gt;</c></description></item>
        /// <item><description>Inline specifiers: <c>inline</c>, <c>__forceinline</c>, <c>__inline</c>, <c>constexpr</c>, <c>consteval</c></description></item>
        /// <item><description>Storage classes: <c>static</c>, <c>extern</c>, <c>explicit</c></description></item>
        /// <item><description>Virtual dispatch: <c>virtual</c>, <c>override</c>, <c>final</c></description></item>
        /// <item><description>Calling conventions: <c>__cdecl</c>, <c>__stdcall</c>, <c>__fastcall</c></description></item>
        /// <item><description>Attributes: <c>[[nodiscard]]</c>, <c>__attribute__(...)</c>, <c>noexcept</c></description></item>
        /// <item><description>Type declarations: <c>class</c>, <c>struct</c>, <c>union</c>, <c>enum</c>, <c>typedef</c>, <c>using</c></description></item>
        /// <item><description>Destructors: tilde prefix <c>~ClassName</c></description></item>
        /// </list>
        /// <para>The named capture group <c>mem</c> contains the extracted member identifier.<br/>
        /// The pattern matches function definitions, constructors, and type declarations.</para>
        /// </remarks>
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

        /// <summary>
        /// Enumeration of supported buffer languages for documentation comment detection.<br/>
        /// Each language has distinct comment syntax and member declaration patterns.
        /// </summary>
        private enum BufferLanguage
        {
            /// <summary>C# language with /// XML documentation comments.</summary>
            CSharp,
            /// <summary>VB.NET language with ''' XML documentation comments.</summary>
            VBNet,
            /// <summary>F# language with /// XML documentation comments.</summary>
            FSharp,
            /// <summary>C++ language with ///, //!, /**, or /*! documentation comments.</summary>
            Cpp
        }

        /// <summary>
        /// Determines the programming language of the specified text buffer based on its content type.<br/>
        /// This method maps Visual Studio content type identifiers to the <see cref="BufferLanguage"/> enum.
        /// </summary>
        /// <param name="buffer">
        /// The <see cref="ITextBuffer"/> whose language should be determined.
        /// </param>
        /// <returns>
        /// The detected <see cref="BufferLanguage"/> value for the buffer.
        /// <para>Return values:</para>
        /// <list type="bullet">
        /// <item><description><see cref="BufferLanguage.Cpp"/> — Content type is "C/C++".</description></item>
        /// <item><description><see cref="BufferLanguage.VBNet"/> — Content type is "Basic".</description></item>
        /// <item><description><see cref="BufferLanguage.FSharp"/> — Content type is "F#" or "FSharp".</description></item>
        /// <item><description><see cref="BufferLanguage.CSharp"/> — Default fallback for all other content types.</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// The method uses <see cref="IContentType.IsOfType"/> to perform hierarchical content type checks.<br/>
        /// Exceptions during content type detection default to <see cref="BufferLanguage.CSharp"/>.
        /// </remarks>
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

        /// <summary>
        /// Determines whether the specified buffer is a C++ source or header file.<br/>
        /// This method is retained for backward compatibility with cross-file helper methods.
        /// </summary>
        /// <param name="buffer">
        /// The <see cref="ITextBuffer"/> to check for C++ content type.
        /// </param>
        /// <returns>
        /// <c>true</c> if the buffer's language is <see cref="BufferLanguage.Cpp"/>; otherwise, <c>false</c>.
        /// </returns>
        /// <seealso cref="GetLanguage"/>
        private static bool IsCppBuffer(ITextBuffer buffer)
            => GetLanguage(buffer) == BufferLanguage.Cpp;

        // ── BuildTags (three-pass) ────────────────────────────────────────────────

        /// <summary>
        /// Performs a comprehensive three-pass analysis of the text snapshot to identify,<br/>
        /// parse, and create adornment tags for all documentation comments in the buffer.
        /// </summary>
        /// <param name="snapshot">
        /// The <see cref="ITextSnapshot"/> to analyze. This is an immutable snapshot of<br/>
        /// the text buffer's content at a point in time.
        /// </param>
        /// <returns>
        /// A read-only list of <see cref="TagSpan{IntraTextAdornmentTag}"/> representing<br/>
        /// all documentation comment regions with their associated adornment tags.
        /// </returns>
        /// <remarks>
        /// <para>The method executes three distinct passes over the buffer:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>Pass 1 — Collection:</b> Scans all lines to identify documentation comment blocks.<br/>
        /// For each block, extracts the raw text, span location, and peeks ahead to find the<br/>
        /// following member declaration name. Handles language-specific comment syntax:
        ///   <list type="bullet">
        ///   <item><description><b>C#/F#:</b> Consecutive <c>///</c> lines.</description></item>
        ///   <item><description><b>VB.NET:</b> Consecutive <c>'''</c> lines.</description></item>
        ///   <item><description><b>C++:</b> Either <c>///</c>/<c>//!</c> line comments or <c>/** ... */</c>/<c>/*! ... */</c> block comments.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Pass 2 — Parsing:</b> Parses each collected block using <see cref="DocCommentParser.Parse"/>.<br/>
        /// Builds a dictionary mapping member names to their parsed documentation for cross-reference<br/>
        /// resolution. Stores both the parsed results and the original block metadata.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Pass 3 — Resolution &amp; Emission:</b> For each parsed documentation comment:
        ///   <list type="bullet">
        ///   <item><description>Resolves <c>&lt;inheritdoc&gt;</c> directives by searching in-file dictionaries, cross-file source, and compiled XML docs.</description></item>
        ///   <item><description>Resolves <c>&lt;include&gt;</c> directives by loading external XML files.</description></item>
        ///   <item><description>Merges inherited/included content with the original documentation.</description></item>
        ///   <item><description>Creates a <see cref="DocCommentAdornmentTag"/> with measured indentation for rendering.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// </list>
        /// <para>The method resolves source file paths to support <c>&lt;include&gt;</c> and cross-file<br/>
        /// <c>&lt;inheritdoc&gt;</c> lookups via the <see cref="ITextDocument"/> property of the buffer.</para>
        /// </remarks>
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

        /// <summary>
        /// Scans forward from the specified line number past blank lines, attributes, and decorators<br/>
        /// to locate and extract the member name from the next valid declaration line.
        /// </summary>
        /// <param name="snapshot">
        /// The <see cref="ITextSnapshot"/> containing the text to scan.
        /// </param>
        /// <param name="startLine">
        /// The line number to start scanning from (typically the line after a documentation block).
        /// </param>
        /// <param name="lineCount">
        /// The total number of lines in the snapshot, used as an upper bound for the scan.
        /// </param>
        /// <param name="lang">
        /// The <see cref="BufferLanguage"/> of the buffer, determining which regex pattern<br/>
        /// to use for member name extraction.
        /// </param>
        /// <returns>
        /// The extracted member name if a declaration is found; otherwise, an empty string.
        /// </returns>
        /// <remarks>
        /// <para>The method skips the following types of lines:</para>
        /// <list type="bullet">
        /// <item><description><b>Blank lines:</b> Lines containing only whitespace.</description></item>
        /// <item><description><b>Attribute lines:</b> Lines starting with <c>[</c> (C#/F#), <c>&lt;</c> (VB.NET), or <c>#</c> (preprocessor/C++).</description></item>
        /// </list>
        /// <para>Once a valid declaration line is found, the method uses the language-specific regex:</para>
        /// <list type="bullet">
        /// <item><description><see cref="CsMemberNameRegex"/> for C# declarations.</description></item>
        /// <item><description><see cref="VbMemberNameRegex"/> for VB.NET declarations (case-insensitive).</description></item>
        /// <item><description><see cref="FsMemberNameRegex"/> for F# declarations.</description></item>
        /// <item><description><see cref="CppMemberNameRegex"/> for C++ declarations.</description></item>
        /// </list>
        /// <para>The regex's named capture group <c>mem</c> provides the member identifier.<br/>
        /// Scanning stops after the first valid declaration line, whether matched or not.</para>
        /// </remarks>
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

        /// <summary>
        /// Resolves <c>&lt;inheritdoc&gt;</c> directives by locating and merging documentation<br/>
        /// from a base member declaration into the inheriting member's parsed documentation.
        /// </summary>
        /// <param name="inheritor">
        /// The <see cref="ParsedDocComment"/> for the member containing the <c>&lt;inheritdoc&gt;</c> tag.<br/>
        /// This is the member that will receive inherited documentation.
        /// </param>
        /// <param name="ownMemberName">
        /// The name of the member that owns the inheritor documentation block.<br/>
        /// Used when the <c>cref</c> attribute is empty or missing.
        /// </param>
        /// <param name="parsedByName">
        /// A dictionary mapping member names to their parsed documentation within the current file.<br/>
        /// Used for fast in-file lookups before cross-file searches.
        /// </param>
        /// <param name="fileDir">
        /// The directory path of the current source file, used as the root for<br/>
        /// cross-file and compiled XML documentation searches.
        /// </param>
        /// <param name="depth">
        /// The current recursion depth to prevent infinite inheritance chains.<br/>
        /// Resolution stops when depth exceeds 5.
        /// </param>
        /// <returns>
        /// A merged <see cref="ParsedDocComment"/> combining the inheritor's own content<br/>
        /// with blanks filled from the inherited source. Returns the original inheritor<br/>
        /// if no valid source is found or if recursion depth exceeds the limit.
        /// </returns>
        /// <remarks>
        /// <para>The method follows a three-tier resolution order:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>In-file lookup:</b> Searches the <paramref name="parsedByName"/> dictionary for the target member<br/>
        /// in the same file. This handles common scenarios like interface implementations<br/>
        /// where the interface definition appears above the class in the same file.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Cross-file source scan:</b> Scans all managed source files (.cs, .vb, .fs, .fsi) in the solution<br/>
        /// via DTE (Development Tools Environment) or filesystem traversal. Uses <see cref="FindInCsFiles"/>
        /// <list type="bullet">
        /// <item><description>Attempts DTE solution file enumeration first.</description></item>
        /// <item><description>Falls back to filesystem walk searching upward for .sln/.csproj/.vbproj/.fsproj files.</description></item>
        /// <item><description>Scans each discovered file using <see cref="ScanManagedFileForMember"/>.</description></item>
        /// </list>
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Compiled XML documentation files:</b> Searches bin/Debug and bin/Release directories for .xml files<br/>
        /// generated by the compiler. Uses <see cref="FindInXmlDocFiles"/> to parse member elements<br/>
        /// matching the target name or cref.
        /// </description>
        /// </item>
        /// </list>
        /// <para>If the found source itself contains <c>&lt;inheritdoc&gt;</c>, the method recurses (up to depth 5)<br/>
        /// to build a complete inheritance chain. The final merge operation uses <see cref="Merge"/> to combine<br/>
        /// documentation, with the inheritor's own non-empty fields taking precedence over inherited values.</para>
        /// </remarks>
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

        /// <summary>
        /// HashSet of managed source file extensions recognized for cross-file <c>&lt;inheritdoc&gt;</c> resolution.<br/>
        /// Includes C#, VB.NET, and F# file extensions.
        /// </summary>
        private static readonly HashSet<string> _managedExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".vb", ".fs", ".fsi" };

        /// <summary>
        /// Returns the documentation line-comment regex pattern for a given source file extension.<br/>
        /// Used when scanning files from disk during cross-file <c>&lt;inheritdoc&gt;</c> resolution.
        /// </summary>
        /// <param name="ext">
        /// The file extension (e.g., <c>".cs"</c>, <c>".vb"</c>, <c>".fs"</c>).
        /// </param>
        /// <returns>
        /// A compiled <see cref="Regex"/> matching documentation comment lines for the specified language:
        /// <list type="bullet">
        /// <item><description><c>".vb"</c> → Matches <c>'''</c> (VB.NET triple apostrophe).</description></item>
        /// <item><description>All others → Matches <c>///</c> (C#, F# triple slash).</description></item>
        /// </list>
        /// </returns>
        private static Regex DocLineRegexForExt(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".vb": return new Regex(@"^\s*'''", RegexOptions.Compiled);
                default: return new Regex(@"^\s*///", RegexOptions.Compiled);
            }
        }

        /// <summary>
        /// Returns the member-name extraction regex pattern for a given source file extension.<br/>
        /// Used during cross-file scanning to identify declaration lines.
        /// </summary>
        /// <param name="ext">
        /// The file extension (e.g., <c>".cs"</c>, <c>".vb"</c>, <c>".fs"</c>).
        /// </param>
        /// <returns>
        /// A compiled <see cref="Regex"/> for extracting member names from the specified language:
        /// <list type="bullet">
        /// <item><description><c>".vb"</c> → <see cref="VbMemberNameRegex"/> (case-insensitive).</description></item>
        /// <item><description><c>".fs"</c>, <c>".fsi"</c> → <see cref="FsMemberNameRegex"/>.</description></item>
        /// <item><description>All others → <see cref="CsMemberNameRegex"/>.</description></item>
        /// </list>
        /// </returns>
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

        /// <summary>
        /// Returns the regex pattern used to strip documentation comment prefixes for a given extension.<br/>
        /// Used during cross-file parsing to normalize comment lines to a common format.
        /// </summary>
        /// <param name="ext">
        /// The file extension (e.g., <c>".cs"</c>, <c>".vb"</c>).
        /// </param>
        /// <returns>
        /// A regex pattern string:
        /// <list type="bullet">
        /// <item><description><c>".vb"</c> → <c>^\s*'''\s?</c> (strips VB.NET triple apostrophe).</description></item>
        /// <item><description>All others → <c>^\s*///\s?</c> (strips triple slash).</description></item>
        /// </list>
        /// </returns>
        private static string StripPatternForExt(string ext)
            => ext.ToLowerInvariant() == ".vb" ? @"^\s*'''\s?" : @"^\s*///\s?";

        /// <summary>
        /// Scans all managed source files (.cs, .vb, .fs, .fsi) reachable from the solution<br/>
        /// (via DTE) or from the source file's directory tree (filesystem fallback) for a<br/>
        /// documentation comment block whose following declaration has the given <paramref name="targetName"/>.
        /// </summary>
        /// <param name="fileDir">
        /// The directory of the current source file, used as the starting point for the filesystem<br/>
        /// fallback walk when DTE solution enumeration is unavailable or returns no files.
        /// </param>
        /// <param name="targetName">
        /// The simplified member name to search for (e.g., <c>"ToString"</c>, <c>"MyProperty"</c>).
        /// </param>
        /// <returns>
        /// The first <see cref="ParsedDocComment"/> found for a member matching <paramref name="targetName"/>,<br/>
        /// or <c>null</c> if no match is found in any scanned file.
        /// </returns>
        /// <remarks>
        /// <para>The method uses a two-tier file discovery strategy:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>DTE solution enumeration:</b> Uses <see cref="GetSolutionManagedFiles"/> to retrieve all managed<br/>
        /// source files from the current Visual Studio solution. This is the preferred method as it<br/>
        /// respects the project's actual file list.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Filesystem fallback:</b> If DTE is unavailable, walks up the directory tree from <paramref name="fileDir"/><br/>
        /// (up to 5 levels) looking for a solution (.sln) or project (.csproj, .vbproj, .fsproj) file.<br/>
        /// Once found, recursively scans all subdirectories for managed source files.
        /// </description>
        /// </item>
        /// </list>
        /// <para>For each discovered file, <see cref="ScanManagedFileForMember"/> is called to search for the target member.</para>
        /// </remarks>
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
        /// Retrieves all managed source file paths (.cs, .vb, .fs, .fsi) from the current Visual<br/>
        /// Studio solution via the DTE (Development Tools Environment) automation model.
        /// </summary>
        /// <returns>
        /// A list of absolute file paths for all managed source files in the solution's projects.<br/>
        /// Returns <c>null</c> if the DTE service is unavailable or the solution is not loaded.
        /// </returns>
        /// <remarks>
        /// <para>The method uses the following approach:</para>
        /// <list type="number">
        /// <item><description>Retrieves the global <see cref="EnvDTE.DTE"/> service via <see cref="Microsoft.VisualStudio.Shell.Package.GetGlobalService"/>.</description></item>
        /// <item><description>Casts it to <see cref="EnvDTE80.DTE2"/> for enhanced automation support.</description></item>
        /// <item><description>Accesses <see cref="EnvDTE80.DTE2.Solution"/> and iterates through all projects.</description></item>
        /// <item><description>For each project, calls <see cref="CollectProjectItems"/> to recursively enumerate project items.</description></item>
        /// </list>
        /// <para>If any step fails (e.g., no solution open, DTE unavailable), the method returns <c>null</c>.</para>
        /// </remarks>
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

        /// <summary>
        /// Recursively collects managed source file paths from a collection of Visual Studio projects.
        /// </summary>
        /// <param name="projects">
        /// The <see cref="EnvDTE.Projects"/> collection to enumerate (typically from <see cref="EnvDTE80.DTE2.Solution"/>).
        /// </param>
        /// <param name="files">
        /// The output list to which discovered file paths are appended.
        /// </param>
        /// <remarks>
        /// Iterates through each project and calls <see cref="CollectItems"/> with the project's<br/>
        /// <see cref="EnvDTE.Project.ProjectItems"/> to recursively enumerate all contained source files.
        /// </remarks>
        private static void CollectProjectItems(EnvDTE.Projects projects, List<string> files)
        {
            if (projects == null) return;
            foreach (EnvDTE.Project project in projects)
            {
                try { CollectItems(project.ProjectItems, files); }
                catch { }
            }
        }

        /// <summary>
        /// Recursively collects managed source file paths from a collection of project items.
        /// </summary>
        /// <param name="items">
        /// The <see cref="EnvDTE.ProjectItems"/> collection to enumerate (from a project or folder).
        /// </param>
        /// <param name="files">
        /// The output list to which discovered file paths are appended.
        /// </param>
        /// <remarks>
        /// <para>For each project item, the method:</para>
        /// <list type="number">
        /// <item><description>Iterates through all file names associated with the item (via <see cref="EnvDTE.ProjectItem.FileNames"/>).</description></item>
        /// <item><description>Checks if the file extension is in <see cref="_managedExts"/> (.cs, .vb, .fs, .fsi).</description></item>
        /// <item><description>Adds matching file paths to the <paramref name="files"/> list.</description></item>
        /// <item><description>Recursively processes nested <see cref="EnvDTE.ProjectItem.ProjectItems"/> (subfolders).</description></item>
        /// </list>
        /// <para>All exceptions during item enumeration are silently caught to prevent failures<br/>
        /// from corrupt or inaccessible project items from breaking the entire scan.</para>
        /// </remarks>
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
        /// Reads a managed source file (.cs, .vb, .fs, .fsi) and searches for a documentation<br/>
        /// comment block whose following declaration matches the specified <paramref name="targetName"/>.
        /// </summary>
        /// <param name="filePath">
        /// The absolute path to the source file to scan.
        /// </param>
        /// <param name="targetName">
        /// The simplified member name to search for (e.g., <c>"ToString"</c>, <c>"MyProperty"</c>).
        /// </param>
        /// <returns>
        /// A <see cref="ParsedDocComment"/> containing the parsed documentation for the first matching<br/>
        /// member found in the file, or <c>null</c> if no match is found or the file cannot be read.
        /// </returns>
        /// <remarks>
        /// <para>The method executes the following scanning algorithm:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>Read file lines:</b> Loads the entire file into memory via <see cref="File.ReadAllLines"/>.<br/>
        /// Returns <c>null</c> immediately if the file is inaccessible.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Select language-specific patterns:</b> Based on the file extension, selects the appropriate<br/>
        /// documentation line regex (<see cref="DocLineRegexForExt"/>), member name regex (<see cref="MemberRegexForExt"/>),<br/>
        /// and comment prefix strip pattern (<see cref="StripPatternForExt"/>).
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Scan for documentation blocks:</b> Iterates through lines, collecting consecutive documentation<br/>
        /// comment lines into a block. For each block found, peeks ahead past blank lines and attribute lines<br/>
        /// to find the following member declaration.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Match member name:</b> If the declaration's extracted member name matches <paramref name="targetName"/>,<br/>
        /// re-strips the comment lines using the language-specific prefix pattern and normalizes them<br/>
        /// to a common <c>///</c> format before parsing with <see cref="DocCommentParser.Parse"/>.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Validate and return:</b> If the parsed result is valid and has non-empty summary text,<br/>
        /// returns it immediately. Otherwise continues scanning the rest of the file.
        /// </description>
        /// </item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Array of C++ source and header file extension patterns used during cross-file<br/>
        /// <c>&lt;inheritdoc&gt;</c> resolution for C++ projects.
        /// </summary>
        /// <remarks>
        /// Includes: <c>*.h</c>, <c>*.hpp</c>, <c>*.hxx</c>, <c>*.h++</c>, <c>*.cpp</c>, <c>*.cxx</c>, <c>*.cc</c>, <c>*.c++</c>, <c>*.c</c>.
        /// </remarks>
        private static readonly string[] _cppExtensions =
        {
            "*.h", "*.hpp", "*.hxx", "*.h++",
            "*.cpp", "*.cxx", "*.cc", "*.c++", "*.c"
        };

        /// <summary>
        /// Regex matching C++ line-based documentation comments (<c>///</c> or <c>//!</c>).
        /// </summary>
        private static readonly Regex _cppLineDoc =
            new Regex(@"^\s*(?:///|//!)", RegexOptions.Compiled);

        /// <summary>
        /// Regex matching C++ block documentation comment openers (<c>/**</c> or <c>/*!</c>).
        /// </summary>
        private static readonly Regex _cppBlockOpen =
            new Regex(@"^\s*/\*[*!]", RegexOptions.Compiled);

        /// <summary>
        /// Regex for extracting C++ member names from declaration lines during cross-file scanning.<br/>
        /// Handles templates, specifiers (inline, static, virtual, explicit, etc.), and type declarations.
        /// </summary>
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
        /// Finds all C++ source and header files in the current Visual Studio solution<br/>
        /// via DTE, falling back to a filesystem walk if DTE is unavailable.
        /// </summary>
        /// <param name="fileDir">
        /// The directory of the current source file, used as the starting point for the<br/>
        /// filesystem fallback walk. The method searches upward (up to 6 levels) for a<br/>
        /// solution (.sln), VC++ project (.vcxproj), or CMakeLists.txt file.
        /// </param>
        /// <returns>
        /// A list of absolute file paths for all discovered C++ source and header files,<br/>
        /// or <c>null</c> if neither DTE nor filesystem discovery succeeds.
        /// </returns>
        /// <remarks>
        /// <para>The method attempts DTE solution enumeration first. If that returns no files,<br/>
        /// it walks up the directory tree from <paramref name="fileDir"/> looking for project indicators:</para>
        /// <list type="bullet">
        /// <item><description><c>*.sln</c> — Visual Studio solution file.</description></item>
        /// <item><description><c>*.vcxproj</c> — Visual C++ project file.</description></item>
        /// <item><description><c>CMakeLists.txt</c> — CMake project file.</description></item>
        /// </list>
        /// <para>Once a root is found, it recursively scans all subdirectories for files matching<br/>
        /// the extensions in <see cref="_cppExtensions"/>.</para>
        /// </remarks>
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

        /// <summary>
        /// Recursively collects C++ source and header file paths from a collection of Visual Studio projects.
        /// </summary>
        /// <param name="projects">
        /// The <see cref="EnvDTE.Projects"/> collection to enumerate (typically from the solution).
        /// </param>
        /// <param name="files">
        /// The output list to which discovered C++ file paths are appended.
        /// </param>
        /// <remarks>
        /// Iterates through each project and calls <see cref="CollectCppItems"/> with the project's<br/>
        /// <see cref="EnvDTE.Project.ProjectItems"/> to recursively enumerate contained C++ files.
        /// </remarks>
        private static void CollectCppProjectItems(EnvDTE.Projects projects, List<string> files)
        {
            if (projects == null) return;
            foreach (EnvDTE.Project project in projects)
            {
                try { CollectCppItems(project.ProjectItems, files); }
                catch { }
            }
        }

        /// <summary>
        /// HashSet of C++ file extensions used to filter project items during DTE-based file discovery.
        /// </summary>
        /// <remarks>
        /// Contains: <c>.h</c>, <c>.hpp</c>, <c>.hxx</c>, <c>.h++</c>, <c>.cpp</c>, <c>.cxx</c>, <c>.cc</c>, <c>.c++</c>, <c>.c</c>.
        /// </remarks>
        private static readonly HashSet<string> _cppFileExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".h", ".hpp", ".hxx", ".h++", ".cpp", ".cxx", ".cc", ".c++", ".c" };

        /// <summary>
        /// Recursively collects C++ source and header file paths from a collection of project items.
        /// </summary>
        /// <param name="items">
        /// The <see cref="EnvDTE.ProjectItems"/> collection to enumerate (from a project or folder).
        /// </param>
        /// <param name="files">
        /// The output list to which discovered C++ file paths are appended.
        /// </param>
        /// <remarks>
        /// <para>For each project item, the method:</para>
        /// <list type="number">
        /// <item><description>Iterates through all file names (via <see cref="EnvDTE.ProjectItem.FileNames"/>).</description></item>
        /// <item><description>Checks if the file extension is in <see cref="_cppFileExts"/>.</description></item>
        /// <item><description>Adds matching file paths to the <paramref name="files"/> list.</description></item>
        /// <item><description>Recursively processes nested <see cref="EnvDTE.ProjectItem.ProjectItems"/> (subfolders).</description></item>
        /// </list>
        /// </remarks>
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
        /// Reads a C++ source or header file and searches for a documentation comment block<br/>
        /// whose following declaration matches the specified <paramref name="targetName"/>.
        /// </summary>
        /// <param name="filePath">
        /// The absolute path to the C++ source or header file to scan.
        /// </param>
        /// <param name="targetName">
        /// The simplified member name to search for (e.g., <c>"ToString"</c>, <c>"MyClass"</c>).
        /// </param>
        /// <returns>
        /// A <see cref="ParsedDocComment"/> containing the parsed documentation for the first matching<br/>
        /// member found, or <c>null</c> if no match is found or the file cannot be read.
        /// </returns>
        /// <remarks>
        /// <para>The method scans the file line by line, handling two types of documentation comments:</para>
        /// <list type="bullet">
        /// <item>
        /// <description><b>Block comments (<c>/** ... */</c> or <c>/*! ... */</c>):</b><br/>
        /// When <see cref="_cppBlockOpen"/> matches, collects lines until <c>*/</c> is found.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Line comments (<c>///</c> or <c>//!</c>):</b><br/>
        /// When <see cref="_cppLineDoc"/> matches, collects consecutive matching lines.
        /// </description>
        /// </item>
        /// </list>
        /// <para>For each block found, the method peeks past blank lines and preprocessor directives (<c>#</c>)<br/>
        /// to find the following member declaration. If the declaration's member name (extracted via<br/>
        /// <see cref="_cppMemberScan"/>) matches <paramref name="targetName"/>, the block is parsed with<br/>
        /// <see cref="DocCommentParser.Parse"/> using <see cref="DocCommentLanguage.Cpp"/>.</para>
        /// </remarks>
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

        /// <summary>
        /// Searches compiled XML documentation files (.xml) in bin/Debug and bin/Release<br/>
        /// directories for a member element matching the specified target name and cref.
        /// </summary>
        /// <param name="fileDir">
        /// The directory of the current source file, used as the root for searching parent<br/>
        /// directories and their bin/ subdirectories.
        /// </param>
        /// <param name="targetSimpleName">
        /// The simplified member name to match against the end of member element name attributes.
        /// </param>
        /// <param name="fullCref">
        /// The full cref string for more precise matching. Used when <paramref name="targetSimpleName"/><br/>
        /// alone is not specific enough (e.g., overloaded methods).
        /// </param>
        /// <returns>
        /// A <see cref="ParsedDocComment"/> containing the parsed documentation from the first<br/>
        /// matching XML member element found, or <c>null</c> if no match is found.
        /// </returns>
        /// <remarks>
        /// <para>The method searches the following directories for .xml files:</para>
        /// <list type="bullet">
        /// <item><description>The current file's directory (<paramref name="fileDir"/>).</description></item>
        /// <item><description><c>parent/bin/Debug</c> — Parent solution's debug output.</description></item>
        /// <item><description><c>parent/bin/Release</c> — Parent solution's release output.</description></item>
        /// <item><description><c>fileDir/bin/Debug</c> — Project's debug output.</description></item>
        /// <item><description><c>fileDir/bin/Release</c> — Project's release output.</description></item>
        /// </list>
        /// <para>For each XML file found, the method:</para>
        /// <list type="number">
        /// <item><description>Loads the file with <see cref="XDocument.Load"/>.</description></item>
        /// <item><description>Iterates through all <c>&lt;member&gt;</c> elements.</description></item>
        /// <item><description>Matches the <c>name</c> attribute against <paramref name="targetSimpleName"/> and <paramref name="fullCref"/>.</description></item>
        /// <item><description>If matched, extracts the inner XML content, wraps it in a synthetic <c>///</c> comment block,<br/>
        /// and parses it via <see cref="DocCommentParser.Parse"/> with <see cref="DocCommentLanguage.CSharp"/>.</description></item>
        /// </list>
        /// <para>This fallback is only used when in-file and cross-file source searches fail to find the target member.<br/>
        /// C++ projects typically don't produce compiled XML doc files, so this is primarily for mixed C#/C++ solutions.</para>
        /// </remarks>
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

        /// <summary>
        /// Resolves an <c>&lt;include&gt;</c> directive by loading the specified external XML file<br/>
        /// and merging its documentation content with the original parsed comment.
        /// </summary>
        /// <param name="doc">
        /// The <see cref="ParsedDocComment"/> containing the <see cref="ParsedDocComment.Include"/> entry<br/>
        /// to resolve. If the Include property is <c>null</c>, the method returns the input unchanged.
        /// </param>
        /// <param name="fileDir">
        /// The directory of the current source file, used to resolve relative file paths<br/>
        /// in the <c>&lt;include&gt;</c> directive.
        /// </param>
        /// <returns>
        /// A merged <see cref="ParsedDocComment"/> combining the original documentation with content<br/>
        /// from the included XML file. Returns the original <paramref name="doc"/> if the file doesn't<br/>
        /// exist, the path is unresolvable, or parsing fails.
        /// </returns>
        /// <remarks>
        /// <para>The method executes the following resolution steps:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>Path resolution:</b> If <see cref="IncludeEntry.File"/> is not rooted, combines it with<br/>
        /// <paramref name="fileDir"/> using <see cref="Path.Combine"/>. Returns the original doc if the file<br/>
        /// doesn't exist.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>XML loading:</b> Loads the file with <see cref="XDocument.Load"/>.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Content extraction:</b> Uses <see cref="XPathExtensions.XPathSelectElements"/> or<br/>
        /// <see cref="XPathExtensions.XPathSelectElement"/> with <see cref="IncludeEntry.Path"/> to locate the target content.
        ///   <list type="bullet">
        ///   <item><description>If <see cref="IncludeEntry.Path"/> is empty, uses the root element's child nodes.</description></item>
        ///   <item><description>If the XPath selects a single <c>&lt;member&gt;</c> element, extracts its child nodes.</description></item>
        ///   <item><description>Otherwise, uses the selected elements' nodes directly.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Normalization:</b> Concatenates the extracted nodes, wraps each line in <c>///</c> prefixes,<br/>
        /// and parses with <see cref="DocCommentParser.Parse"/> using <see cref="DocCommentLanguage.CSharp"/>.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Merge:</b> Calls <see cref="Merge"/> with <c>clearInheritDoc: false</c> to combine the original<br/>
        /// documentation with the included content. Clears the <see cref="ParsedDocComment.Include"/> property<br/>
        /// in the result to indicate successful resolution.
        /// </description>
        /// </item>
        /// </list>
        /// <para>All file I/O and XML parsing operations are wrapped in try-catch to prevent failures<br/>
        /// from disrupting the editor. On any error, the original <paramref name="doc"/> is returned unchanged.</para>
        /// </remarks>
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

        /// <summary>
        /// Merges two <see cref="ParsedDocComment"/> instances, with the primary instance's non-empty<br/>
        /// fields taking precedence over the secondary instance's values.
        /// </summary>
        /// <param name="a">
        /// The primary <see cref="ParsedDocComment"/> (typically the inheritor or original documentation).<br/>
        /// Non-empty fields from this instance take precedence in the merge result.
        /// </param>
        /// <param name="b">
        /// The secondary <see cref="ParsedDocComment"/> (typically the inherited or included source).<br/>
        /// Provides fallback values for empty or missing fields in <paramref name="a"/>.
        /// </param>
        /// <param name="clearInheritDoc">
        /// When <c>true</c>, clears the <see cref="ParsedDocComment.InheritDoc"/> property in the merged result<br/>
        /// to prevent further inheritance resolution. When <c>false</c>, preserves the original value.
        /// </param>
        /// <returns>
        /// A new <see cref="ParsedDocComment"/> containing the merged documentation from both inputs,
        /// with <see cref="ParsedDocComment.IsValid"/> set to <c>true</c>.
        /// </returns>
        /// <remarks>
        /// <para>The merge operation handles the following documentation elements:</para>
        /// <list type="bullet">
        /// <item><description><b>Text fields (coalesced):</b> <see cref="ParsedDocComment.Summary"/>, <see cref="ParsedDocComment.Remarks"/>, <see cref="ParsedDocComment.Returns"/>, <see cref="ParsedDocComment.Example"/>, <see cref="ParsedDocComment.Permission"/>, <see cref="ParsedDocComment.PermissionCref"/>, <see cref="ParsedDocComment.Brief"/>, <see cref="ParsedDocComment.Note"/>, <see cref="ParsedDocComment.Warning"/>, <see cref="ParsedDocComment.Attention"/>, <see cref="ParsedDocComment.Deprecated"/>, <see cref="ParsedDocComment.Since"/>, <see cref="ParsedDocComment.Version"/>, <see cref="ParsedDocComment.Author"/>, <see cref="ParsedDocComment.Date"/>, <see cref="ParsedDocComment.Copyright"/>, <see cref="ParsedDocComment.Bug"/>, <see cref="ParsedDocComment.Todo"/>, <see cref="ParsedDocComment.Pre"/>, <see cref="ParsedDocComment.Post"/>, <see cref="ParsedDocComment.Invariant"/>, <see cref="ParsedDocComment.Remark"/>.</description></item>
        /// <item><description><b>Parameter lists (merged by name):</b> <see cref="ParsedDocComment.Params"/> and <see cref="ParsedDocComment.TypeParams"/> — merged via <see cref="MergeParams"/>, with <paramref name="a"/> entries taking precedence and unique <paramref name="b"/> entries appended.</description></item>
        /// <item><description><b>Collection fields (unified by unique keys):</b> <see cref="ParsedDocComment.Exceptions"/> (key: <c>FullCref + Description</c>), <see cref="ParsedDocComment.SeeAlsos"/> (key: <c>Cref + Href + Label</c>), <see cref="ParsedDocComment.CompletionList"/> (key: item value), <see cref="ParsedDocComment.RetVals"/> (key: <c>Name</c>), <see cref="ParsedDocComment.SeeEntries"/> (key: item value) — merged via <see cref="Union{T}"/> to prevent duplicates.</description></item>
        /// </list>
        /// <para>The merge strategy uses <see cref="Coalesce"/> for simple string fields (first non-empty wins),<br/>
        /// <see cref="MergeParams"/> for parameter lists (append unique by name), and <see cref="Union{T}"/><br/>
        /// for collection fields (append unique by key selector).</para>
        /// <para>When <paramref name="clearInheritDoc"/> is <c>true</c>, the merged result's <see cref="ParsedDocComment.InheritDoc"/><br/>
        /// is set to <c>null</c>, indicating that inheritance resolution is complete and no further<br/>
        /// <c>&lt;inheritdoc&gt;</c> processing should occur on the merged result.</para>
        /// </remarks>
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

        /// <summary>
        /// Returns the first non-empty string value, or the second value if the first is empty.<br/>
        /// This helper implements the coalescing logic for documentation field merging.
        /// </summary>
        /// <param name="a">
        /// The primary string value to check for emptiness.
        /// </param>
        /// <param name="b">
        /// The fallback string value to return if <paramref name="a"/> is null, empty, or whitespace.
        /// </param>
        /// <returns>
        /// <paramref name="a"/> if it contains non-whitespace characters; otherwise, <paramref name="b"/>.
        /// </returns>
        /// <remarks>
        /// Uses <see cref="string.IsNullOrWhiteSpace"/> to determine if a string is effectively empty.<br/>
        /// This ensures that whitespace-only values are treated as missing documentation.
        /// </remarks>
        private static string Coalesce(string a, string b)
            => string.IsNullOrWhiteSpace(a) ? b : a;

        /// <summary>
        /// Merges two parameter entry lists, preserving the order and values from the first list<br/>
        /// and appending unique entries from the second list based on parameter name.
        /// </summary>
        /// <param name="a">
        /// The primary list of <see cref="ParamEntry"/> instances. Entries from this list<br/>
        /// appear first in the result and take precedence in case of name conflicts.
        /// </param>
        /// <param name="b">
        /// The secondary list of <see cref="ParamEntry"/> instances. Only entries with unique<br/>
        /// names not present in <paramref name="a"/> are appended to the result.
        /// </param>
        /// <returns>
        /// A merged list containing all unique parameter entries from both lists, with <paramref name="a"/>
        /// entries taking precedence and appearing first.
        /// </returns>
        /// <remarks>
        /// Uses a <see cref="HashSet{T}"/> with <see cref="StringComparer.Ordinal"/> to track<br/>
        /// parameter names and ensure no duplicates in the result. Comparison is case-sensitive.
        /// </remarks>
        private static List<ParamEntry> MergeParams(List<ParamEntry> a, List<ParamEntry> b)
        {
            var result = new List<ParamEntry>(a);
            var seen = new HashSet<string>(a.Select(p => p.Name), StringComparer.Ordinal);
            foreach (var p in b) if (seen.Add(p.Name)) result.Add(p);
            return result;
        }

        /// <summary>
        /// Merges two lists of items, ensuring uniqueness based on a key selector function.<br/>
        /// Items from the first list take precedence; items from the second list are appended<br/>
        /// only if their key is not already present in the first list.
        /// </summary>
        /// <typeparam name="T">
        /// The type of items in the lists being merged.
        /// </typeparam>
        /// <param name="a">
        /// The primary list whose items appear first in the result.
        /// </param>
        /// <param name="b">
        /// The secondary list whose unique items (by key) are appended to the result.
        /// </param>
        /// <param name="key">
        /// A function that extracts a unique string key from each item for comparison.
        /// </param>
        /// <returns>
        /// A merged list containing all items from <paramref name="a"/> and unique items from <paramref name="b"/>.
        /// </returns>
        /// <remarks>
        /// Uses a <see cref="HashSet{T}"/> to track keys from <paramref name="a"/> and filters <paramref name="b"/>
        /// to exclude items with duplicate keys. This prevents duplicate documentation entries<br/>
        /// for exceptions, see-also references, completion lists, and return values.
        /// </remarks>
        private static List<T> Union<T>(List<T> a, List<T> b, Func<T, string> key)
        {
            var result = new List<T>(a);
            var seen = new HashSet<string>(a.Select(key));
            foreach (var item in b) if (seen.Add(key(item))) result.Add(item);
            return result;
        }

        // ── Indent measurement ────────────────────────────────────────────────────

        /// <summary>
        /// Calculates the horizontal indentation width in pixels for a given line of text.<br/>
        /// This measurement is used to position the rendered documentation control<br/>
        /// at the correct horizontal offset matching the source code indentation.
        /// </summary>
        /// <param name="lineText">
        /// The raw text of the first line of the documentation comment block.<br/>
        /// Used to count leading whitespace characters (spaces and tabs).
        /// </param>
        /// <returns>
        /// The indentation width in pixels, calculated based on:
        /// <list type="bullet">
        /// <item><description>Space characters (<c>' '</c>) — counted as 1 unit each.</description></item>
        /// <item><description>Tab characters (<c>'\t'</c>) — counted as 4 units each.</description></item>
        /// </list>
        /// Multiplied by the editor's column width if available; otherwise, uses a fallback of 7.2 pixels per unit.
        /// </returns>
        /// <remarks>
        /// The method attempts to retrieve the editor's <see cref="ITextFormatter.ColumnWidth"/> from<br/>
        /// <see cref="IWpfTextView.FormattedLineSource"/> for accurate pixel measurements. If unavailable<br/>
        /// (due to view state or exceptions), falls back to a hardcoded 7.2 pixels per indentation unit,<br/>
        /// which approximates the default font size in typical editor configurations.
        /// </remarks>
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

        /// <summary>
        /// Handles buffer content change events, invalidating the tag cache and triggering<br/>
        /// a full rebuild of tags for the entire snapshot.
        /// </summary>
        /// <param name="sender">
        /// The <see cref="ITextBuffer"/> that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// A <see cref="TextContentChangedEventArgs"/> containing the before and after snapshots.<br/>
        /// The <see cref="TextContentChangedEventArgs.After"/> snapshot becomes the new active snapshot.
        /// </param>
        /// <remarks>
        /// This handler responds to text edits by:
        /// <list type="number">
        /// <item><description>Clearing the cached snapshot and tag list.</description></item>
        /// <item><description>Raising <see cref="TagsChanged"/> for the entire buffer length to force re-tagging.</description></item>
        /// </list>
        /// <para>The editor will subsequently call <see cref="GetTags"/> which will rebuild all tags<br/>
        /// with the updated buffer content.</para>
        /// </remarks>
        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _cachedSnapshot = null;
            _cachedTags = null;
            var snap = e.After;
            TagsChanged?.Invoke(this,
                new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));
        }

        /// <summary>
        /// Handles layout change events, invalidating the tag cache when viewport dimensions change.<br/>
        /// This ensures documentation rendering adapts to window resizes and zoom operations.
        /// </summary>
        /// <param name="sender">
        /// The <see cref="IWpfTextView"/> that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// A <see cref="TextViewLayoutChangedEventArgs"/> containing the old and new view state<br/>
        /// including viewport width and height.
        /// </param>
        /// <remarks>
        /// The handler only invalidates the cache when viewport width or height changes occur,<br/>
        /// ignoring scroll-only or selection changes. This optimization prevents unnecessary<br/>
        /// rebuilds during routine editor navigation.
        /// <para>When invalidated, the handler:</para>
        /// <list type="number">
        /// <item><description>Clears the cached snapshot and tag list.</description></item>
        /// <item><description>Raises <see cref="TagsChanged"/> for the entire buffer to trigger re-layout of adornments.</description></item>
        /// </list>
        /// </remarks>
        private void OnLayoutChanged(object sender, Microsoft.VisualStudio.Text.Editor.TextViewLayoutChangedEventArgs e)
        {
            if (e.NewViewState.ViewportWidth != e.OldViewState.ViewportWidth
                || e.NewViewState.ViewportHeight != e.OldViewState.ViewportHeight)
            {
                _cachedSnapshot = null;
                _cachedTags = null;
                var snap = _buffer.CurrentSnapshot;
                TagsChanged?.Invoke(this,
                    new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));
            }
        }

        /// <summary>
        /// Handles view closure events, resetting caret tracking and clearing cached state.<br/>
        /// This handler performs cleanup to prevent memory leaks when the editor view is closed.
        /// </summary>
        /// <param name="sender">
        /// The <see cref="IWpfTextView"/> that is being closed (unused).
        /// </param>
        /// <param name="e">
        /// Event arguments (unused).
        /// </param>
        /// <remarks>
        /// The handler resets the caret line tracker to -1 and clears the tag cache.<br/>
        /// Actual resource disposal is handled by the <see cref="Dispose"/> method which<br/>
        /// unsubscribes from all events when the tagger is disposed.
        /// </remarks>
        private void OnViewClosed(object sender, EventArgs e)
        {
            _caretLine = -1;
            _cachedSnapshot = null;
            _cachedTags = null;
        }

        /// <summary>
        /// Handles caret position changes to manage documentation visibility in caret-based mode.<br/>
        /// When the caret enters a documentation comment region, the rendered adornment is hidden<br/>
        /// to allow raw XML editing. When the caret leaves, the adornment reappears.
        /// </summary>
        /// <param name="sender">
        /// The <see cref="ITextCaret"/> that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// A <see cref="CaretPositionChangedEventArgs"/> containing the old and new caret positions.
        /// </param>
        /// <remarks>
        /// <para>The handler is inactive when glyph toggle mode is enabled (<see cref="RenderDocOptions.EffectiveGlyphToggle"/>
        /// is <c>true</c>), as visibility is then controlled by user interaction with margin glyphs.</para>
        /// <para>When active, the handler:</para>
        /// <list type="number">
        /// <item><description>Extracts the new caret line number from the buffer position.</description></item>
        /// <item><description>Skips processing if the line number hasn't changed.</description></item>
        /// <item><description>Invokes the local <c>Invalidate</c> function for both the old and new lines.</description></item>
        /// </list>
        /// <para>The <c>Invalidate</c> function searches the cached tags for any documentation block<br/>
        /// spanning the specified line number and raises <see cref="TagsChanged"/> for the affected span.<br/>
        /// This targeted invalidation is more efficient than rebuilding the entire buffer.</para>
        /// </remarks>
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
        /// Handles settings change notifications from the options dialog or theme change broadcasts.<br/>
        /// Bumps the static generation counter to invalidate all cached tags and triggers a two-phase<br/>
        /// rebuild with the updated configuration.
        /// </summary>
        /// <param name="sender">
        /// The source of the settings change event (unused).
        /// </param>
        /// <param name="e">
        /// Event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>The handler implements a two-phase invalidation/rebuild strategy to prevent visual glitches:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>Phase 1 — Clear:</b>
        ///   <list type="bullet">
        ///   <item><description>Increments <see cref="_settingsGeneration"/> atomically via <see cref="System.Threading.Interlocked.Increment"/>.</description></item>
        ///   <item><description>Clears the cached snapshot and tags.</description></item>
        ///   <item><description>Sets <see cref="_forceEmpty"/> to <c>true</c> to suppress tag rendering.</description></item>
        ///   <item><description>Raises <see cref="TagsChanged"/> for the entire buffer, causing the editor to remove all existing adornments.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Phase 2 — Rebuild:</b>
        ///   <list type="bullet">
        ///   <item><description>Schedules a dispatcher callback at <see cref="DispatcherPriority.Normal"/>.</description></item>
        ///   <item><description>Resets <see cref="_forceEmpty"/> to <c>false</c>.</description></item>
        ///   <item><description>Raises <see cref="TagsChanged"/> again, triggering <see cref="GetOrBuildTags"/> to rebuild all tags with new settings.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// </list>
        /// <para>This two-phase approach ensures the editor first clears old adornments before<br/>
        /// creating new ones, preventing visual artifacts during the transition.</para>
        /// </remarks>
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

        /// <summary>
        /// Releases all resources used by the tagger, including event handler subscriptions.<br/>
        /// This method is called when the editor disposes the tagger (typically when the view closes).
        /// </summary>
        /// <remarks>
        /// <para>The method unsubscribes from the following events to prevent memory leaks:</para>
        /// <list type="bullet">
        /// <item><description><see cref="ITextBuffer.Changed"/> — buffer content modification events.</description></item>
        /// <item><description><see cref="ITextCaret.PositionChanged"/> — caret movement events.</description></item>
        /// <item><description><see cref="IWpfTextView.LayoutChanged"/> — viewport layout update events.</description></item>
        /// <item><description><see cref="IWpfTextView.Closed"/> — view closure events.</description></item>
        /// <item><description><see cref="SettingsChangedBroadcast.SettingsChanged"/> — global settings change broadcasts.</description></item>
        /// </list>
        /// <para>After disposal, the tagger will no longer respond to editor state changes<br/>
        /// or produce tags. The tagger should not be used after disposal.</para>
        /// </remarks>
        public void Dispose()
        {
            _buffer.Changed -= OnBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}