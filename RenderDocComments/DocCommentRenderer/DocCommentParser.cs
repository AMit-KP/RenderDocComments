using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RenderDocComments.DocCommentRenderer
{
    /// <summary>
    /// Represents a parsed documentation comment block containing all extracted fields,<br/>
    /// structured data, and metadata from XML-doc or Doxygen comment syntax.
    /// </summary>
    /// <remarks>
    /// <para>This class serves as the data model for documentation comments, populated by<br/>
    /// <see cref="DocCommentParser.Parse"/>. It supports both C#-style XML documentation<br/>
    /// (<c>&lt;summary&gt;</c>, <c>&lt;param&gt;</c>, etc.) and C++-style Doxygen commands<br/>
    /// (<c>\brief</c>, <c>\param</c>, etc.), normalizing both into a unified structure.</para>
    /// <para>The class is organized into three categories of fields:</para>
    /// <list type="bullet">
    /// <item><description><b>Shared fields (C# and C++):</b> <see cref="Summary"/>, <see cref="Remarks"/>, <see cref="Returns"/>, <see cref="Example"/>, <see cref="Permission"/>, <see cref="PermissionCref"/>.</description></item>
    /// <item><description><b>C++ / Doxygen-only fields:</b> <see cref="Brief"/>, <see cref="Details"/>, <see cref="Note"/>, <see cref="Warning"/>, <see cref="Attention"/>, <see cref="Deprecated"/>, <see cref="Since"/>, <see cref="Version"/>, <see cref="Author"/>, <see cref="Date"/>, <see cref="Copyright"/>, <see cref="Bug"/>, <see cref="Todo"/>, <see cref="Pre"/>, <see cref="Post"/>, <see cref="Invariant"/>, <see cref="Remark"/>, <see cref="SeeEntries"/>, <see cref="RetVals"/>.</description></item>
    /// <item><description><b>Shared structured fields:</b> <see cref="InheritDoc"/>, <see cref="Include"/>, <see cref="Params"/>, <see cref="TypeParams"/>, <see cref="Exceptions"/>, <see cref="SeeAlsos"/>, <see cref="CompletionList"/>.</description></item>
    /// </list>
    /// </remarks>
    public class ParsedDocComment
    {
        // ── Shared fields (C# and C++) ─────────────────────────────────────────
        
        /// <summary>
        /// Gets or sets the primary summary text for the documentation comment.<br/>
        /// Populated from <c>&lt;summary&gt;</c> (XML) or <c>\brief</c>/<c>\short</c> (Doxygen).
        /// </summary>
        public string Summary { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the extended remarks/documentation text.<br/>
        /// Populated from <c>&lt;remarks&gt;</c> (XML), <c>\details</c> (Doxygen),<br/>
        /// implicit text before the first tag, or <c>\par</c> sections.
        /// </summary>
        public string Remarks { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the return value documentation text.<br/>
        /// Populated from <c>&lt;returns&gt;</c> (XML) or <c>\return</c>/<c>\returns</c> (Doxygen).
        /// </summary>
        public string Returns { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the example usage documentation text.<br/>
        /// Populated from <c>&lt;example&gt;</c> (XML) or <c>\example</c> (Doxygen).
        /// </summary>
        public string Example { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the permission/access documentation text.<br/>
        /// Populated from <c>&lt;permission&gt;</c> (XML).
        /// </summary>
        public string Permission { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the cref attribute of the permission element.<br/>
        /// Represents the code reference for the permission (e.g., a class or method name).
        /// </summary>
        public string PermissionCref { get; set; } = string.Empty;

        // ── C++ / Doxygen-only fields ─────────────────────────────────────────
        
        /// <summary>\brief / \short — becomes Summary when no &lt;summary&gt; is present.</summary>
        public string Brief { get; set; } = string.Empty;
        /// <summary>\details — excess prose before any tag in an implicit zone.</summary>
        public string Details { get; set; } = string.Empty;
        /// <summary>\note</summary>
        public string Note { get; set; } = string.Empty;
        /// <summary>\warning</summary>
        public string Warning { get; set; } = string.Empty;
        /// <summary>\attention</summary>
        public string Attention { get; set; } = string.Empty;
        /// <summary>\deprecated</summary>
        public string Deprecated { get; set; } = string.Empty;
        /// <summary>\since</summary>
        public string Since { get; set; } = string.Empty;
        /// <summary>\version</summary>
        public string Version { get; set; } = string.Empty;
        /// <summary>\author</summary>
        public string Author { get; set; } = string.Empty;
        /// <summary>\date</summary>
        public string Date { get; set; } = string.Empty;
        /// <summary>\copyright</summary>
        public string Copyright { get; set; } = string.Empty;
        /// <summary>\bug</summary>
        public string Bug { get; set; } = string.Empty;
        /// <summary>\todo</summary>
        public string Todo { get; set; } = string.Empty;
        /// <summary>\pre</summary>
        public string Pre { get; set; } = string.Empty;
        /// <summary>\post</summary>
        public string Post { get; set; } = string.Empty;
        /// <summary>\invariant</summary>
        public string Invariant { get; set; } = string.Empty;
        /// <summary>\remark / \remarks</summary>
        public string Remark { get; set; } = string.Empty;
        /// <summary>Raw \sa / \see entries before URL classification.</summary>
        public List<string> SeeEntries { get; set; } = new List<string>();
        /// <summary>\retval entries.</summary>
        public List<RetValEntry> RetVals { get; set; } = new List<RetValEntry>();

        // ── Shared structured fields ──────────────────────────────────────────
        
        /// <summary>
        /// Gets or sets the <c>&lt;inheritdoc&gt;</c> entry when present in the documentation comment.<br/>
        /// Contains an optional cref attribute specifying the target member to inherit from.
        /// </summary>
        public InheritDocEntry InheritDoc
        {
            get; set;
        }
        
        /// <summary>
        /// Gets or sets the <c>&lt;include&gt;</c> entry when present in the documentation comment.<br/>
        /// References an external XML file containing documentation content.
        /// </summary>
        public IncludeEntry Include
        {
            get; set;
        }
        
        /// <summary>
        /// Gets the list of parameter documentation entries.<br/>
        /// Populated from <c>&lt;param&gt;</c> (XML) or <c>\param</c> (Doxygen).
        /// </summary>
        public List<ParamEntry> Params { get; set; } = new List<ParamEntry>();
        
        /// <summary>
        /// Gets the list of type parameter (generic template) documentation entries.<br/>
        /// Populated from <c>&lt;typeparam&gt;</c> (XML) or <c>\tparam</c> (Doxygen).
        /// </summary>
        public List<ParamEntry> TypeParams { get; set; } = new List<ParamEntry>();
        
        /// <summary>
        /// Gets the list of exception documentation entries.<br/>
        /// Populated from <c>&lt;exception&gt;</c> (XML) or <c>\throws</c>/<c>\throw</c>/<c>\exception</c> (Doxygen).
        /// </summary>
        public List<ExceptionEntry> Exceptions { get; set; } = new List<ExceptionEntry>();
        
        /// <summary>
        /// Gets the list of "See Also" cross-reference entries.<br/>
        /// Populated from <c>&lt;seealso&gt;</c> (XML) or <c>\sa</c>/<c>\see</c> (Doxygen).
        /// </summary>
        public List<SeeAlsoEntry> SeeAlsos { get; set; } = new List<SeeAlsoEntry>();
        
        /// <summary>
        /// Gets the list of completion list entries for IntelliSense completion.<br/>
        /// Populated from <c>&lt;completionlist&gt;</c> (XML).
        /// </summary>
        public List<string> CompletionList { get; set; } = new List<string>();
        
        /// <summary>
        /// Gets or sets a value indicating whether the parsed documentation comment<br/>
        /// is valid and contains meaningful content. Set to <c>true</c> by the parser<br/>
        /// when parsing succeeds without errors.
        /// </summary>
        public bool IsValid
        {
            get; set;
        }
    }

    // ── Data types ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents a single parameter or type parameter entry in a documentation comment.<br/>
    /// Contains the parameter name, description, and optional direction hint (for C++).
    /// </summary>
    public class ParamEntry
    {
        /// <summary>
        /// Gets or sets the name of the parameter or type parameter.<br/>
        /// Extracted from the <c>name</c> attribute of <c>&lt;param&gt;</c>/<c>&lt;typeparam&gt;</c><br/>
        /// or the first argument of <c>\param</c>/<c>\tparam</c> Doxygen commands.
        /// </summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the description text for the parameter.<br/>
        /// Contains the inner content of the XML element or the text following<br/>
        /// the parameter name in Doxygen syntax, with inline markup preserved as tokens.
        /// </summary>
        public string Description { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the direction hint for C++ parameters (<c>in</c>, <c>out</c>, <c>in,out</c>).<br/>
        /// Extracted from the optional [<c>in</c>], [<c>out</c>], or [<c>in,out</c>] suffix<br/>
        /// on the <c>\param</c> Doxygen command. Empty string for C# parameters or<br/>
        /// C++ parameters without an explicit direction specifier.
        /// </summary>
        public string Direction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents an exception documentation entry specifying the exception type and description.<br/>
    /// Populated from <c>&lt;exception cref="..."&gt;</c> (XML) or <c>\throws</c>/<c>\throw</c>/<c>\exception</c> (Doxygen).
    /// </summary>
    public class ExceptionEntry
    {
        /// <summary>
        /// Gets or sets the simplified exception type name.<br/>
        /// Extracted by stripping the cref prefix and namespace qualifiers from <see cref="FullCref"/>.
        /// </summary>
        public string Type { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the full cref attribute value (e.g., <c>T:System.Exception</c>).<br/>
        /// Used for navigation links when the user clicks on the exception type in the rendered view.
        /// </summary>
        public string FullCref { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the description of when/why the exception is thrown.<br/>
        /// Contains the inner content of the XML element or text following the exception name<br/>
        /// in Doxygen syntax, with inline markup preserved as tokens.
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a "See Also" cross-reference entry linking to related documentation.<br/>
    /// Populated from <c>&lt;seealso&gt;</c> (XML) or <c>\sa</c>/<c>\see</c> (Doxygen).
    /// </summary>
    public class SeeAlsoEntry
    {
        /// <summary>
        /// Gets or sets the display label for the cross-reference link.<br/>
        /// Defaults to the simplified cref name or the href URL if no label is specified.
        /// </summary>
        public string Label { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the cref attribute for code symbol navigation.<br/>
        /// Used when the entry references another code entity (class, method, property, etc.).
        /// </summary>
        public string Cref { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the href attribute for external URL navigation.<br/>
        /// Used when the entry references an external web resource or documentation page.
        /// </summary>
        public string Href { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents an <c>&lt;inheritdoc&gt;</c> entry specifying documentation inheritance.<br/>
    /// When present, the tagger attempts to resolve and merge documentation from the target member.
    /// </summary>
    public class InheritDocEntry
    {
        /// <summary>
        /// Gets or sets the optional cref attribute specifying the target member to inherit from.<br/>
        /// If empty, the inheritor's own member name is used as the target lookup key.
        /// </summary>
        public string Cref { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents an <c>&lt;include&gt;</c> entry referencing an external XML documentation file.<br/>
    /// When present, the tagger loads the specified file and extracts documentation from it.
    /// </summary>
    public class IncludeEntry
    {
        /// <summary>
        /// Gets or sets the file path of the external XML documentation file.<br/>
        /// Can be absolute or relative to the source file's directory.
        /// </summary>
        public string File { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the XPath expression for locating specific content within the XML file.<br/>
        /// If empty, the root element's content is used.
        /// </summary>
        public string Path { get; set; } = string.Empty;
    }

    /// <summary>A \retval entry — specific return value with a name and description.</summary>
    public class RetValEntry
    {
        /// <summary>
        /// Gets or sets the name of the return value (e.g., error code, status constant).<br/>
        /// Extracted as the first argument following the <c>\retval</c> Doxygen command.
        /// </summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the description of what the return value means.<br/>
        /// Contains the text following the return value name in Doxygen syntax.
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    // ── Language hint ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Enumeration specifying the documentation comment language style for the parser.<br/>
    /// Determines whether XML-doc syntax or Doxygen command syntax is used for parsing.
    /// </summary>
    public enum DocCommentLanguage
    {
        /// <summary>
        /// C#-style XML documentation syntax (<c>&lt;summary&gt;</c>, <c>&lt;param&gt;</c>, etc.).<br/>
        /// Also used for F# and VB.NET documentation comments.
        /// </summary>
        CSharp,
        
        /// <summary>
        /// C++-style Doxygen command syntax (<c>\brief</c>, <c>\param</c>, etc.)<br/>
        /// or C++ XML documentation syntax (triple-slash comments in MSVC/VS tooling).<br/>
        /// The parser auto-detects which style is used within the comment block.
        /// </summary>
        Cpp
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  DocCommentParser
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Static parser class that transforms raw documentation comment text into<br/>
    /// structured <see cref="ParsedDocComment"/> objects supporting both XML-doc and Doxygen syntax.
    /// </summary>
    /// <remarks>
    /// <para>The parser supports two documentation comment styles:</para>
    /// <list type="bullet">
    /// <item>
    /// <description><b>XML Documentation (C#, F#, VB.NET, C++ triple-slash):</b><br/>
    /// Parses XML tags like <c>&lt;summary&gt;</c>, <c>&lt;param&gt;</c>, <c>&lt;returns&gt;</c>,<br/>
    /// <c>&lt;exception&gt;</c>, <c>&lt;seealso&gt;</c>, <c>&lt;inheritdoc&gt;</c>, <c>&lt;include&gt;</c>, etc.<br/>
    /// Converts inline elements (<c>&lt;c&gt;</c>, <c>&lt;see&gt;</c>, <c>&lt;paramref&gt;</c>)<br/>
    /// into an intermediate token format consumed by <see cref="DocCommentControl"/>.
    /// </description>
    /// </item>
    /// <item>
    /// <description><b>Doxygen Commands (C++ <c>/** ... */</c> or <c>/*! ... */</c>):</b><br/>
    /// Recognizes commands like <c>\brief</c>, <c>\param</c>, <c>\returns</c>, <c>\throws</c>,<br/>
    /// <c>\retval</c>, <c>\note</c>, <c>\warning</c>, <c>\deprecated</c>, <c>\since</c>, <c>\version</c>,<br/>
    /// <c>\author</c>, <c>\date</c>, <c>\copyright</c>, <c>\bug</c>, <c>\todo</c>, <c>\pre</c>, <c>\post</c>,<br/>
    /// <c>\invariant</c>, <c>\remark</c>, <c>\sa</c>/<c>\see</c>, <c>\example</c>, <c>\code</c>, <c>\verbatim</c>, etc.<br/>
    /// Also handles inline markup (<c>\p</c>, <c>\a</c>, <c>\e</c>, <c>\em</c>, <c>\c</c>, <c>\b</c>)<br/>
    /// and HTML subset tags (<c>&lt;a href&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;em&gt;</c>, etc.).
    /// </description>
    /// </item>
    /// </list>
    /// <para>For C++ comments, the parser auto-detects the style by scanning for XML tags<br/>
    /// versus Doxygen commands. If XML tags are found first, the XML parser is used.<br/>
    /// If Doxygen commands are found first, the Doxygen parser is used.<br/>
    /// Plain text without decisive markers defaults to XML style for C#-like rendering.</para>
    /// <para>Both parsers convert documentation content into a unified <see cref="ParsedDocComment"/><br/>
    /// structure, with inline markup expressed as token strings like <c>`code`</c>,<br/>
    /// <c>[LINK cref=...]...[/LINK]</c>, <c>[CODE]...[/CODE]</c>, <c>[BOLD]...[/BOLD]</c>, etc.<br/>
    /// These tokens are later rendered by <see cref="DocCommentControl"/> via <see cref="DocCommentControl.Tokenise"/>.</para>
    /// </remarks>
    public static class DocCommentParser
    {
        // ── Public entry point ────────────────────────────────────────────────────

        /// <summary>
        /// Detects XML opening tags inside stripped comment lines to determine if<br/>
        /// the comment block uses XML-doc syntax rather than Doxygen commands.
        /// </summary>
        /// <remarks>
        /// Matches opening tags for: <c>&lt;summary&gt;</c>, <c>&lt;remarks&gt;</c>, <c>&lt;returns&gt;</c>,<br/>
        /// <c>&lt;param&gt;</c>, <c>&lt;typeparam&gt;</c>, <c>&lt;exception&gt;</c>, <c>&lt;seealso&gt;</c>,<br/>
        /// <c>&lt;example&gt;</c>, <c>&lt;inheritdoc&gt;</c>, <c>&lt;include&gt;</c>, <c>&lt;permission&gt;</c>,<br/>
        /// <c>&lt;completionlist&gt;</c>, <c>&lt;value&gt;</c>, <c>&lt;code&gt;</c>, <c>&lt;see&gt;</c>, <c>&lt;para&gt;</c>,<br/>
        /// <c>&lt;list&gt;</c>, <c>&lt;br&gt;</c>, <c>&lt;pre&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;strong&gt;</c>, <c>&lt;i&gt;</c>,<br/>
        /// <c>&lt;em&gt;</c>, <c>&lt;u&gt;</c>, <c>&lt;s&gt;</c>, <c>&lt;strike&gt;</c>.
        /// </remarks>
        private static readonly Regex _xmlTagDetect = new Regex(
            @"<(?:summary|remarks|returns?|param|typeparam|exception|seealso|example|" +
            @"inheritdoc|include|permission|completionlist|value|code|see|para|list|br|pre|" +
            @"b|strong|i|em|u|s|strike)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Detects Doxygen commands anywhere in a comment line to determine if<br/>
        /// the comment block uses Doxygen command syntax rather than XML-doc syntax.
        /// </summary>
        /// <remarks>
        /// Matches commands prefixed with <c>\</c> or <c>@</c>: <c>\brief</c>, <c>\short</c>,<br/>
        /// <c>\details</c>, <c>\param</c>, <c>\tparam</c>, <c>\returns</c>, <c>\throws</c>, <c>\exception</c>,<br/>
        /// <c>\note</c>, <c>\warning</c>, <c>\attention</c>, <c>\deprecated</c>, <c>\since</c>, <c>\version</c>,<br/>
        /// <c>\author</c>, <c>\date</c>, <c>\copyright</c>, <c>\bug</c>, <c>\todo</c>, <c>\pre</c>, <c>\post</c>,<br/>
        /// <c>\invariant</c>, <c>\remark</c>, <c>\sa</c>, <c>\see</c>, <c>\example</c>, <c>\code</c>, <c>\verbatim</c>.
        /// </remarks>
        private static readonly Regex _doxygenTagDetect = new Regex(
            @"[\\@](?:brief|short|details?|param|tparam|returns?|throws?|exception|" +
            @"note|warning|attention|deprecated|since|version|author|date|copyright|" +
            @"bug|todo|pre|post|invariant|remark|sa|see|example|code|verbatim)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parses a raw documentation comment block into a structured <see cref="ParsedDocComment"/> object.<br/>
        /// This is the primary public entry point for all documentation comment parsing.
        /// </summary>
        /// <param name="rawCommentBlock">
        /// The raw documentation comment text as extracted from the source code.<br/>
        /// May contain <c>///</c> or <c>'''</c> line prefixes for XML-doc style,<br/>
        /// or <c>/** ... */</c>/<c>/*! ... */</c> block markers for Doxygen style.
        /// </param>
        /// <param name="language">
        /// A hint specifying the documentation comment language style. Defaults to <see cref="DocCommentLanguage.CSharp"/>.<br/>
        /// When set to <see cref="DocCommentLanguage.Cpp"/>, the parser auto-detects whether the block<br/>
        /// uses XML-doc syntax (triple-slash MSVC-style) or Doxygen command syntax.
        /// </param>
        /// <returns>
        /// A <see cref="ParsedDocComment"/> containing all extracted fields and structured data,<br/>
        /// with <see cref="ParsedDocComment.IsValid"/> set to <c>true</c> on success.<br/>
        /// Returns <c>null</c> if the input is empty or whitespace-only.
        /// </returns>
        /// <remarks>
        /// <para>The parsing strategy depends on the <paramref name="language"/> parameter:</para>
        /// <list type="bullet">
        /// <item>
        /// <description><b><see cref="DocCommentLanguage.CSharp"/> (and F#, VB.NET):</b><br/>
        /// Always uses the XML parser (<see cref="ParseCSharp"/>). Strips <c>///</c> or <c>'''</c> prefixes,<br/>
        /// wraps content in a synthetic <c>&lt;root&gt;</c> element, fixes invalid XML angles,<br/>
        /// and parses with <see cref="XDocument.Parse"/>.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b><see cref="DocCommentLanguage.Cpp"/>:</b><br/>
        /// First calls <see cref="DetectCppStyle"/> to determine the comment style:
        ///   <list type="bullet">
        ///   <item><description>If XML tags are found before Doxygen commands → uses <see cref="ParseCSharp"/>.</description></item>
        ///   <item><description>If Doxygen commands are found before XML tags → uses <see cref="ParseCpp"/>.</description></item>
        ///   <item><description>If no decisive markers → defaults to <see cref="CppCommentStyle.Xml"/> for C#-like rendering.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// </list>
        /// <para>If XML parsing fails (malformed XML), the method catches the exception and returns <c>null</c>.</para>
        /// </remarks>
        public static ParsedDocComment Parse(
            string rawCommentBlock,
            DocCommentLanguage language = DocCommentLanguage.CSharp)
        {
            if (string.IsNullOrWhiteSpace(rawCommentBlock))
                return null;

            if (language == DocCommentLanguage.Cpp)
            {
                var style = DetectCppStyle(rawCommentBlock);
                return style == CppCommentStyle.Xml
                    ? ParseCSharp(rawCommentBlock)   // XML-doc inside /// — use XML parser
                    : ParseCpp(rawCommentBlock);      // Doxygen commands — use Doxygen parser
            }

            return ParseCSharp(rawCommentBlock);
        }

        /// <summary>
        /// Enumeration distinguishing C++ comment styles for auto-detection purposes.<br/>
        /// Used internally by <see cref="DetectCppStyle"/> to select the appropriate parser.
        /// </summary>
        private enum CppCommentStyle
        {
            /// <summary>
            /// XML documentation style C++ comments (triple-slash <c>///</c> with XML tags).<br/>
            /// Commonly used in MSVC/Visual Studio tooling for C++ documentation.
            /// </summary>
            Xml,
            /// <summary>
            /// Doxygen command style C++ comments (<c>/** ... */</c> or <c>/*! ... */</c> with <c>\commands</c>).<br/>
            /// Standard Doxygen documentation syntax for C++ projects.
            /// </summary>
            Doxygen
        }

        /// <summary>
        /// Inspects stripped comment lines to determine whether the C++ comment block<br/>
        /// uses XML documentation syntax or Doxygen command syntax.
        /// </summary>
        /// <param name="rawBlock">
        /// The raw C++ comment block text, potentially containing <c>/** ... */</c>,<br/>
        /// <c>/*! ... */</c>, or <c>///</c> markers with mixed content.
        /// </param>
        /// <returns>
        /// <see cref="CppCommentStyle.Xml"/> if any XML documentation tag is found before<br/>
        /// any Doxygen command; <see cref="CppCommentStyle.Doxygen"/> if Doxygen commands<br/>
        /// appear first. Returns <see cref="CppCommentStyle.Xml"/> by default if no decisive<br/>
        /// markers are found, ensuring plain <c>///</c> text renders like C# documentation.
        /// </returns>
        /// <remarks>
        /// <para>The method processes the block line by line:</para>
        /// <list type="number">
        /// <item><description>Strips C++ comment prefixes using <see cref="StripCppLinePrefix"/>.</description></item>
        /// <item><description>Removes trailing <c>*/</c> block closers.</description></item>
        /// <item><description>Checks each line against <see cref="_xmlTagDetect"/> and <see cref="_doxygenTagDetect"/>.</description></item>
        /// <item><description>Returns the style of the first matching pattern encountered.</description></item>
        /// </list>
        /// </remarks>
        private static CppCommentStyle DetectCppStyle(string rawBlock)
        {
            foreach (var raw in rawBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = StripCppLinePrefix(raw);
                var tr = line.TrimEnd();
                if (tr.EndsWith("*/")) line = tr.Substring(0, tr.LastIndexOf("*/")).TrimEnd();

                if (_xmlTagDetect.IsMatch(line)) return CppCommentStyle.Xml;
                if (_doxygenTagDetect.IsMatch(line)) return CppCommentStyle.Doxygen;
            }
            // No decisive marker — default to Xml (matches C# rendering for plain /// text).
            return CppCommentStyle.Xml;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  C# / XML-doc parser
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Regex that matches <c>&lt;</c> or <c>&gt;</c> characters that are NOT part of valid XML constructs.<br/>
        /// Used to fix C++ operators and stray angle brackets that are legal in doc-comment<br/>
        /// prose but cause <see cref="XDocument.Parse"/> to fail.
        /// </summary>
        /// <remarks>
        /// <para>Matches the following patterns:</para>
        /// <list type="bullet">
        /// <item><description><c>&lt;&lt;</c> — C++ left-shift operator used inside <c>&lt;c&gt;</c> tags (e.g., <c>operator&lt;&lt;</c>).</description></item>
        /// <item><description><c>&gt;&gt;</c> — C++ right-shift operator used inside <c>&lt;c&gt;</c> tags.</description></item>
        /// <item><description>Lone <c>&lt;</c> not followed by a letter, <c>/</c>, <c>!</c>, or <c>?</c> — stray angle bracket that would break XML parsing.</description></item>
        /// </list>
        /// <para>These are replaced with their corresponding XML entity equivalents (<c>&amp;lt;</c>, <c>&amp;gt;</c>)<br/>
        /// before the comment block is passed to <see cref="XDocument.Parse"/>.</para>
        /// </remarks>
        private static readonly Regex _fixBareAngles = new Regex(
            @"<<|>>|<(?![a-zA-Z/!?])",
            RegexOptions.Compiled);

        /// <summary>
        /// Escapes invalid XML angle brackets in the comment text to prevent parsing failures.<br/>
        /// Replaces C++ operators and stray <c>&lt;</c> characters with XML-safe entity equivalents.
        /// </summary>
        /// <param name="xml">
        /// The raw comment text wrapped in synthetic XML tags, potentially containing<br/>
        /// C++ operators (<c>&lt;&lt;</c>, <c>&gt;&gt;</c>) or stray angle brackets.
        /// </param>
        /// <returns>
        /// The input text with invalid angle brackets replaced:
        /// <list type="bullet">
        /// <item><description><c>&lt;&lt;</c> → <c>&amp;lt;&amp;lt;</c></description></item>
        /// <item><description><c>&gt;&gt;</c> → <c>&amp;gt;&amp;gt;</c></description></item>
        /// <item><description>Stray <c>&lt;</c> → <c>&amp;lt;</c></description></item>
        /// </list>
        /// </returns>
        private static string EscapeInvalidXmlAngles(string xml)
        {
            return _fixBareAngles.Replace(xml, m =>
            {
                switch (m.Value)
                {
                    case "<<": return "&lt;&lt;";
                    case ">>": return "&gt;&gt;";
                    default: return "&lt;";   // lone stray <
                }
            });
        }

        /// <summary>
        /// Parses a documentation comment block using the C# XML documentation parser.<br/>
        /// Strips comment prefixes, wraps content in a synthetic <c>&lt;root&gt;</c> element,<br/>
        /// fixes invalid XML angles, and parses with <see cref="XDocument.Parse"/>.
        /// </summary>
        /// <param name="rawCommentBlock">
        /// The raw documentation comment text containing <c>///</c> or <c>'''</c> line prefixes<br/>
        /// and XML documentation tags.
        /// </param>
        /// <returns>
        /// A <see cref="ParsedDocComment"/> with all XML documentation fields extracted<br/>
        /// and inline markup converted to intermediate token format, or <c>null</c> if parsing fails.
        /// </returns>
        /// <remarks>
        /// <para>The method executes the following steps:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>Line splitting and prefix stripping:</b><br/>
        /// Splits the input on newlines and removes <c>///</c> (C#/F#/C++) or <c>'''</c> (VB.NET)<br/>
        /// prefixes from each line using a regex replacement.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>XML wrapping:</b><br/>
        /// Wraps the stripped content in <c>&lt;root&gt;...&lt;/root&gt;</c> to create well-formed XML<br/>
        /// that <see cref="XDocument.Parse"/> can process.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Angle bracket escaping:</b><br/>
        /// Calls <see cref="EscapeInvalidXmlAngles"/> to fix C++ operators and stray angles.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>XML parsing:</b><br/>
        /// Parses the sanitized XML with <see cref="XDocument.Parse"/>. If parsing fails,<br/>
        /// the method catches the exception and returns <c>null</c>.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Field extraction:</b><br/>
        /// Extracts top-level XML elements into <see cref="ParsedDocComment"/> fields:
        ///   <list type="bullet">
        ///   <item><description><c>&lt;summary&gt;</c> → <see cref="ParsedDocComment.Summary"/></description></item>
        ///   <item><description><c>&lt;remarks&gt;</c> → <see cref="ParsedDocComment.Remarks"/></description></item>
        ///   <item><description><c>&lt;returns&gt;</c> → <see cref="ParsedDocComment.Returns"/></description></item>
        ///   <item><description><c>&lt;example&gt;</c> → <see cref="ParsedDocComment.Example"/></description></item>
        ///   <item><description><c>&lt;permission cref="..."&gt;</c> → <see cref="ParsedDocComment.Permission"/> and <see cref="ParsedDocComment.PermissionCref"/></description></item>
        ///   <item><description><c>&lt;inheritdoc cref="..."/&gt;</c> → <see cref="ParsedDocComment.InheritDoc"/></description></item>
        ///   <item><description><c>&lt;include file="..." path="..."/&gt;</c> → <see cref="ParsedDocComment.Include"/></description></item>
        ///   <item><description><c>&lt;param name="..."&gt;</c> → <see cref="ParsedDocComment.Params"/></description></item>
        ///   <item><description><c>&lt;typeparam name="..."&gt;</c> → <see cref="ParsedDocComment.TypeParams"/></description></item>
        ///   <item><description><c>&lt;exception cref="..."&gt;</c> → <see cref="ParsedDocComment.Exceptions"/> (with <see cref="SimplifyCref"/> for type name)</description></item>
        ///   <item><description><c>&lt;seealso cref="..."/&gt;</c> / <c>&lt;seealso href="..."/&gt;</c> → <see cref="ParsedDocComment.SeeAlsos"/></description></item>
        ///   <item><description><c>&lt;completionlist cref="..."/&gt;</c> → <see cref="ParsedDocComment.CompletionList"/></description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// </list>
        /// <para>All inner text content is processed via <see cref="ReadInnerMixed"/> to convert<br/>
        /// inline XML elements (<c>&lt;c&gt;</c>, <c>&lt;see&gt;</c>, <c>&lt;paramref&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, etc.)<br/>
        /// into the intermediate token format understood by <see cref="DocCommentControl"/>.</para>
        /// </remarks>
        private static ParsedDocComment ParseCSharp(string rawCommentBlock)
        {
            var lines = rawCommentBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder("<root>");
            foreach (var line in lines)
            {
                // Strip /// (C#/F#/C++) or ''' (VB.NET) prefixes.
                var stripped = Regex.Replace(line, @"^\s*(?:'''|///)\s?", "");
                sb.AppendLine(stripped);
            }
            sb.Append("</root>");

            // Fix C++ operators and stray angle brackets that are legal in doc-comment
            // prose but illegal in XML (e.g. <c>operator<<</c>).
            var xmlSrc = EscapeInvalidXmlAngles(sb.ToString());

            try
            {
                var xml = XDocument.Parse(xmlSrc, LoadOptions.None);
                var root = xml.Root;
                var result = new ParsedDocComment { IsValid = true };

                result.Summary = ReadInnerMixed(root?.Element("summary")) ?? string.Empty;
                result.Remarks = ReadInnerMixed(root?.Element("remarks")) ?? string.Empty;
                result.Returns = ReadInnerMixed(root?.Element("returns")) ?? string.Empty;
                result.Example = ReadInnerMixed(root?.Element("example")) ?? string.Empty;

                var permEl = root?.Element("permission");
                if (permEl != null)
                {
                    result.PermissionCref = StripPrefix(permEl.Attribute("cref")?.Value ?? string.Empty);
                    result.Permission = ReadInnerMixed(permEl) ?? string.Empty;
                }

                var idEl = root?.Element("inheritdoc");
                if (idEl != null)
                    result.InheritDoc = new InheritDocEntry
                    {
                        Cref = idEl.Attribute("cref")?.Value ?? string.Empty
                    };

                var inclEl = root?.Element("include");
                if (inclEl != null)
                    result.Include = new IncludeEntry
                    {
                        File = inclEl.Attribute("file")?.Value ?? string.Empty,
                        Path = inclEl.Attribute("path")?.Value ?? string.Empty
                    };

                foreach (var p in SafeElements(root, "param"))
                    result.Params.Add(new ParamEntry
                    {
                        Name = p.Attribute("name")?.Value ?? string.Empty,
                        Description = ReadInnerMixed(p) ?? string.Empty
                    });

                foreach (var tp in SafeElements(root, "typeparam"))
                    result.TypeParams.Add(new ParamEntry
                    {
                        Name = tp.Attribute("name")?.Value ?? string.Empty,
                        Description = ReadInnerMixed(tp) ?? string.Empty
                    });

                foreach (var ex in SafeElements(root, "exception"))
                {
                    var raw = ex.Attribute("cref")?.Value ?? string.Empty;
                    result.Exceptions.Add(new ExceptionEntry
                    {
                        FullCref = raw,
                        Type = SimplifyCref(raw),
                        Description = ReadInnerMixed(ex) ?? string.Empty
                    });
                }

                foreach (var sa in SafeElements(root, "seealso"))
                {
                    var cref = sa.Attribute("cref")?.Value ?? string.Empty;
                    var href = sa.Attribute("href")?.Value ?? string.Empty;
                    var label = !sa.IsEmpty
                        ? sa.Value
                        : !string.IsNullOrEmpty(cref) ? SimplifyCref(cref) : href;
                    result.SeeAlsos.Add(new SeeAlsoEntry
                    {
                        Label = label,
                        Cref = cref,
                        Href = href
                    });
                }

                var clEl = root?.Element("completionlist");
                if (clEl != null)
                {
                    var cref = clEl.Attribute("cref")?.Value;
                    if (!string.IsNullOrEmpty(cref))
                        result.CompletionList.Add(StripPrefix(cref));
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  C++ / Doxygen parser
        //
        //  Handles both /// line comments and /** ... */ / /*! ... */ block comments.
        //  Command prefix may be '\' or '@'.
        //
        //  Tags recognised:
        //    \brief / \short         → Summary  (also first implicit paragraph)
        //    \details                → Details  (merged into Remarks)
        //    \param[dir] name desc   → Params   (direction: in | out | in,out)
        //    \tparam name desc       → TypeParams
        //    \return / \returns      → Returns
        //    \retval name desc       → RetVals
        //    \throws / \throw / \exception name desc → Exceptions
        //    \note                   → Note
        //    \warning                → Warning
        //    \attention              → Attention
        //    \deprecated             → Deprecated
        //    \since                  → Since
        //    \version                → Version
        //    \author                 → Author
        //    \date                   → Date
        //    \copyright              → Copyright
        //    \bug                    → Bug
        //    \todo                   → Todo
        //    \pre                    → Pre-condition
        //    \post                   → Post-condition
        //    \invariant              → Invariant
        //    \remark / \remarks      → Remark
        //    \sa / \see              → SeeAlsos
        //    \example                → Example
        //    \code … \endcode        → [CODE]…[/CODE] block token
        //    \verbatim … \endverbatim→ [CODE]…[/CODE] block token
        //    \f$ … \f$               → inline `math` token
        //    \f[ … \f] / \f{ … \f}  → [CODE]math[/CODE] block token
        //    \par title              → Remarks paragraph
        //    Inline:
        //      \p name / \a name / \e name / \em name / \c name → `name`
        //      \b word               → `word`  (bold — rendered as inline code)
        //      \ref target ["label"] → [LINK cref=target]label[/LINK]
        //      \link target … \endlink → [LINK cref=target]…[/LINK]
        //      HTML subset: <a href>, <b>, <i>, <em>, <tt>, <code>, <strong>,
        //                   <var>, <u>, <br>
        // ══════════════════════════════════════════════════════════════════════════

        #region C++ stripping regexes

        /// <summary>
        /// Strips leading C++ comment decorators from a single source line.<br/>
        /// Handles: <c>///</c>, <c>//!</c>, <c>/**</c>, <c>/*!</c>, <c>*</c> (JavaDoc interior), <c>*/</c>.
        /// </summary>
        private static readonly Regex _cppStripLine = new Regex(
            @"^\s*(?:/{3,}[!/]?|/\*[*!]\s*|(?<=\n)\s*\*(?!/))",
            RegexOptions.Compiled);

        /// <summary>
        /// Matches any Doxygen command starting a logical section.<br/>
        /// Captures the tag name, optional direction suffix (<c>[in]</c>, <c>[out]</c>, <c>[in,out]</c>),<br/>
        /// and the first non-space token after the command.
        /// </summary>
        private static readonly Regex _cppTagLine = new Regex(
            @"^[\\@](?<tag>[a-zA-Z]+)(?:\[(?<dir>[^\]]*)\])?(?:\s+(?<first>\S+))?",
            RegexOptions.Compiled);

        /// <summary>
        /// Matches <c>\code … \endcode</c> or <c>\verbatim … \endverbatim</c> multi-line blocks.<br/>
        /// Captures the body content between the opening and closing delimiters.
        /// </summary>
        private static readonly Regex _cppCodeBlock = new Regex(
            @"[\\@](?:code|verbatim)\b[^\n]*\n(?<body>[\s\S]*?)[\\@](?:endcode|endverbatim)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Matches <c>\f[ … \f]</c> or <c>\f{ … \f}</c> display math blocks.<br/>
        /// Captures the mathematical expression between the delimiters.
        /// </summary>
        private static readonly Regex _cppLatexBlock = new Regex(
            @"[\\@]f[\[{](?<math>[\s\S]*?)[\\@]f[\]}]",
            RegexOptions.Compiled);

        /// <summary>
        /// Matches <c>\f$ … \f$</c> inline math expressions.<br/>
        /// Captures the mathematical expression between the delimiters.
        /// </summary>
        private static readonly Regex _cppLatexInline = new Regex(
            @"[\\@]f\$(?<math>.*?)[\\@]f\$",
            RegexOptions.Compiled);

        /// <summary>
        /// Matches <c>\link target label \endlink</c> cross-reference commands.<br/>
        /// Captures the target identifier and the display label.
        /// </summary>
        private static readonly Regex _cppLinkCmd = new Regex(
            @"[\\@]link\s+(?<target>\S+)\s+(?<label>.*?)\s*[\\@]endlink",
            RegexOptions.Compiled);

        /// <summary>
        /// Matches <c>\ref target</c> or <c>\ref target "label"</c> reference commands.<br/>
        /// Captures the target identifier and optional display label in quotes.
        /// </summary>
        private static readonly Regex _cppRefCmd = new Regex(
            @"[\\@]ref\s+(?<target>\S+)(?:\s+""(?<label>[^""]+)"")?",
            RegexOptions.Compiled);

        /// <summary>
        /// Matches inline word-level Doxygen commands: <c>\p</c>, <c>\a</c>, <c>\e</c>, <c>\em</c>,<br/>
        /// <c>\c</c> (all rendered as inline code) and <c>\b</c> (bold, rendered as inline code).
        /// </summary>
        private static readonly Regex _cppInlineWordCmd = new Regex(
            @"[\\@](?<cmd>p|a|e|em|c|b)\s+(?<word>\S+)",
            RegexOptions.Compiled);

        /// <summary>
        /// Matches HTML <c>&lt;a href="..."&gt;label&lt;/a&gt;</c> anchor elements.<br/>
        /// Captures the href URL and the display label text.
        /// </summary>
        private static readonly Regex _cppHtmlAnchor = new Regex(
            @"<a\s+href=""(?<href>[^""]+)""\s*>(?<label>.*?)</a>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Matches HTML inline formatting tags (<c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;em&gt;</c>, etc.).<br/>
        /// These are stripped (content preserved, tags removed) during inline processing.
        /// </summary>
        private static readonly Regex _cppHtmlInline = new Regex(
            @"</?(?:b|i|em|strong|tt|code|var|u|s|strike|small|big|sup|sub)>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Matches HTML <c>&lt;br&gt;</c> or <c>&lt;br/&gt;</c> line break elements.<br/>
        /// Replaced with a newline character during inline processing.
        /// </summary>
        private static readonly Regex _cppHtmlBr = new Regex(
            @"<br\s*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Matches HTML <c>&lt;p&gt;</c> or <c>&lt;/p&gt;</c> paragraph elements.<br/>
        /// Replaced with double newlines to create paragraph breaks.
        /// </summary>
        private static readonly Regex _cppHtmlPara = new Regex(
            @"</?p\s*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Matches HTML <c>&lt;li&gt;</c> list item elements.<br/>
        /// Replaced with a bullet point prefix for minimal list support.
        /// </summary>
        private static readonly Regex _cppHtmlListItem = new Regex(
            @"<li\s*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Matches HTML list container tags (<c>&lt;ul&gt;</c>, <c>&lt;ol&gt;</c>, <c>&lt;li&gt;</c>).<br/>
        /// These are stripped during inline processing (list items handled separately).
        /// </summary>
        private static readonly Regex _cppHtmlListTags = new Regex(
            @"</?(?:ul|ol|li)\s*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Matches stray Doxygen commands that were not consumed by tag processing.<br/>
        /// Removed during the final cleanup phase to prevent orphaned command text<br/>
        /// from appearing in the rendered documentation.
        /// </summary>
        private static readonly Regex _cppStrayCmd = new Regex(
            @"[\\@](?:param|tparam|return|returns|throws?|exception|brief|short|details?|" +
            @"note|warning|attention|deprecated|since|version|author|date|copyright|" +
            @"bug|todo|pre|post|invariant|remark|remarks?|sa|see|example|par|" +
            @"endlink|endcode|endverbatim)\b[^\n]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        #endregion

        // ── Set of all recognised Doxygen command names ───────────────────────────

        /// <summary>
        /// HashSet containing all recognised Doxygen command names used for validation<br/>
        /// during tag line matching. Includes documentation tags, structural tags,<br/>
        /// conditional tags, and miscellaneous commands.
        /// </summary>
        /// <remarks>
        /// <para>The set is organized into the following categories:</para>
        /// <list type="bullet">
        /// <item><description><b>Documentation tags:</b> <c>brief</c>, <c>short</c>, <c>details</c>, <c>detail</c>, <c>param</c>, <c>tparam</c>, <c>return</c>, <c>returns</c>, <c>retval</c>, <c>throws</c>, <c>throw</c>, <c>exception</c>, <c>note</c>, <c>warning</c>, <c>attention</c>, <c>deprecated</c>, <c>since</c>, <c>version</c>, <c>author</c>, <c>date</c>, <c>copyright</c>, <c>bug</c>, <c>todo</c>, <c>pre</c>, <c>post</c>, <c>invariant</c>, <c>remark</c>, <c>remarks</c>, <c>sa</c>, <c>see</c>, <c>example</c>, <c>par</c>.</description></item>
        /// <item><description><b>Block delimiters:</b> <c>code</c>, <c>endcode</c>, <c>verbatim</c>, <c>endverbatim</c>.</description></item>
        /// <item><description><b>Structural/grouping:</b> <c>name</c>, <c>class</c>, <c>struct</c>, <c>union</c>, <c>enum</c>, <c>fn</c>, <c>def</c>, <c>typedef</c>, <c>namespace</c>, <c>file</c>, <c>dir</c>, <c>mainpage</c>, <c>page</c>, <c>section</c>, <c>subsection</c>, <c>subsubsection</c>, <c>paragraph</c>, <c>ingroup</c>, <c>addtogroup</c>, <c>defgroup</c>, <c>weakgroup</c>, <c>subpage</c>, <c>anchor</c>.</description></item>
        /// <item><description><b>Conditional/access:</b> <c>internal</c>, <c>private</c>, <c>privatesection</c>, <c>protected</c>, <c>protectedsection</c>, <c>public</c>, <c>publicsection</c>, <c>cond</c>, <c>endcond</c>, <c>if</c>, <c>ifnot</c>, <c>elseif</c>, <c>else</c>, <c>endif</c>.</description></item>
        /// <item><description><b>Miscellaneous:</b> <c>image</c>, <c>dot</c>, <c>dotfile</c>, <c>msc</c>, <c>mscfile</c>, <c>include</c>, <c>dontinclude</c>, <c>skip</c>, <c>skipline</c>, <c>until</c>, <c>line</c>, <c>verbinclude</c>, <c>htmlinclude</c>, <c>htmlonly</c>, <c>endhtml</c>, <c>latexonly</c>, <c>endlatex</c>, <c>rtfonly</c>, <c>endrtf</c>, <c>manonly</c>, <c>endman</c>, <c>xmlonly</c>, <c>endxml</c>, <c>docbookonly</c>, <c>enddocbook</c>, <c>interface</c>, <c>protocol</c>, <c>category</c>, <c>nosubgrouping</c>, <c>hideinitializer</c>, <c>showinitializer</c>, <c>ref</c>, <c>link</c>, <c>endlink</c>.</description></item>
        /// </list>
        /// </remarks>
        private static readonly HashSet<string> _cppKnownTags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Documentation tags we actively process
            "brief", "short", "details", "detail",
            "param", "tparam",
            "return", "returns",
            "retval",
            "throws", "throw", "exception",
            "note", "warning", "attention",
            "deprecated",
            "since", "version", "author", "date", "copyright",
            "bug", "todo",
            "pre", "post", "invariant",
            "remark", "remarks",
            "sa", "see",
            "example",
            "par",
            // Block delimiters (consumed during pre-processing)
            "code", "endcode", "verbatim", "endverbatim",
            // Structural / grouping — we parse but don't render
            "name", "class", "struct", "union", "enum", "fn", "def",
            "typedef", "namespace", "file", "dir", "mainpage", "page",
            "section", "subsection", "subsubsection", "paragraph",
            "ingroup", "addtogroup", "defgroup", "weakgroup",
            "subpage", "anchor",
            // Conditional / access
            "internal", "private", "privatesection",
            "protected", "protectedsection",
            "public", "publicsection",
            "cond", "endcond", "if", "ifnot", "elseif", "else", "endif",
            // Misc inline/block commands that terminate a section
            "image", "dot", "dotfile", "msc", "mscfile",
            "include", "dontinclude", "skip", "skipline", "until",
            "line", "verbinclude", "htmlinclude",
            "htmlonly", "endhtml",
            "latexonly", "endlatex",
            "rtfonly", "endrtf",
            "manonly", "endman",
            "xmlonly", "endxml",
            "docbookonly", "enddocbook",
            "interface", "protocol", "category",
            "nosubgrouping", "hideinitializer", "showinitializer",
            "ref", "link", "endlink",
        };

        // ── Main C++ parse routine ────────────────────────────────────────────────

        /// <summary>
        /// Parses a C++ Doxygen-style documentation comment block into a <see cref="ParsedDocComment"/> object.<br/>
        /// Handles <c>/** ... */</c> and <c>/*! ... */</c> block comments as well as <c>///</c> and <c>//!</c> line comments.
        /// </summary>
        /// <param name="rawBlock">
        /// The raw C++ comment block text with Doxygen commands, potentially containing<br/>
        /// block comment markers (<c>/**</c>, <c>*/</c>) and/or line comment prefixes (<c>///</c>, <c>//!</c>).
        /// </param>
        /// <returns>
        /// A <see cref="ParsedDocComment"/> with all Doxygen fields extracted and<br/>
        /// inline markup converted to intermediate token format.
        /// </returns>
        /// <remarks>
        /// <para>The method executes the following parsing steps:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>Step 1 — Strip comment decorators:</b><br/>
        /// Splits the input on newlines and removes leading comment markers (<c>///</c>, <c>//!</c>, <c>/**</c>, <c>/*!</c>, <c>*</c>)<br/>
        /// from each line using <see cref="StripCppLinePrefix"/>. Also removes trailing <c>*/</c> closers.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Step 2 — Hoist code blocks and LaTeX math:</b><br/>
        /// Extracts <c>\code … \endcode</c>, <c>\verbatim … \endverbatim</c>, and <c>\f[ … \f]</c> blocks<br/>
        /// using <see cref="_cppCodeBlock"/> and <see cref="_cppLatexBlock"/>. Replaces them with sentinel<br/>
        /// placeholders (<c>\x01CODE{n}\x01</c>) to prevent their content from being scanned for Doxygen commands.<br/>
        /// Placeholders are restored in <see cref="ProcessCppInlines"/> during section body processing.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Step 3 — Line-by-line tag scanning:</b><br/>
        /// Iterates through each line, attempting to match a Doxygen command at the start using <see cref="_cppTagLine"/>.<br/>
        /// When a recognised tag (from <see cref="_cppKnownTags"/>) is found, the current section is committed<br/>
        /// via <see cref="StoreCppSection"/> and a new section begins. Continuation lines are appended<br/>
        /// to the current section's body.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Step 4 — Promote Brief → Summary:</b><br/>
        /// If <see cref="ParsedDocComment.Summary"/> is empty after parsing, attempts to populate it from:<br/>
        ///   <list type="bullet">
        ///   <item><description><see cref="ParsedDocComment.Brief"/> (from <c>\brief</c>/<c>\short</c>) if available.</description></item>
        ///   <item><description>First paragraph of <see cref="ParsedDocComment.Details"/> (implicit text before any tag) otherwise.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Step 5 — Merge Details → Remarks:</b><br/>
        /// Appends <see cref="ParsedDocComment.Details"/> to <see cref="ParsedDocComment.Remarks"/> if both are non-empty.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Step 6 — Append \remark to Remarks:</b><br/>
        /// Merges <see cref="ParsedDocComment.Remark"/> (from <c>\remark</c>/<c>\remarks</c>) into <see cref="ParsedDocComment.Remarks"/>.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Step 7 — Classify \sa / \see entries:</b><br/>
        /// Converts raw <see cref="ParsedDocComment.SeeEntries"/> strings into structured <see cref="SeeAlsoEntry"/> objects:
        ///   <list type="bullet">
        ///   <item><description>Entries starting with <c>http://</c> or <c>https://</c> → <see cref="SeeAlsoEntry.Href"/> populated.</description></item>
        ///   <item><description>All other entries → <see cref="SeeAlsoEntry.Cref"/> populated with <see cref="SimplifyCref"/> for the label.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// </list>
        /// </remarks>
        private static ParsedDocComment ParseCpp(string rawBlock)
        {
            // ── Step 1: strip comment decorators line by line ─────────────────────
            var rawLines = rawBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var strippedLines = new List<string>(rawLines.Length);

            foreach (var rawLine in rawLines)
            {
                var l = rawLine;

                // Remove trailing */ that closes a block comment
                var trimR = l.TrimEnd();
                if (trimR.EndsWith("*/"))
                    l = trimR.Substring(0, trimR.LastIndexOf("*/")).TrimEnd();

                // Strip leading comment markers: ///, //!, /**, /*!, * (body line)
                l = StripCppLinePrefix(l);

                strippedLines.Add(l);
            }

            var fullText = string.Join("\n", strippedLines);

            // ── Step 2: hoist \code blocks and LaTeX math before tag scanning ─────
            // Replace with sentinel placeholders; restored later in ProcessCppInlines.
            var codeBlocks = new List<string>();

            fullText = _cppCodeBlock.Replace(fullText, m =>
            {
                codeBlocks.Add(m.Groups["body"].Value.TrimEnd('\n', '\r'));
                return $"\x01CODE{codeBlocks.Count - 1}\x01";
            });

            fullText = _cppLatexBlock.Replace(fullText, m =>
            {
                codeBlocks.Add(m.Groups["math"].Value.Trim());
                return $"\x01CODE{codeBlocks.Count - 1}\x01";
            });

            // ── Step 3: scan line-by-line, splitting on Doxygen commands ──────────

            var result = new ParsedDocComment { IsValid = true };

            string currentTag = "implicit";   // text before the first explicit tag
            string currentFirst = string.Empty;
            string currentDir = string.Empty;
            var currentBody = new StringBuilder();

            void CommitSection()
            {
                var body = ProcessCppInlines(currentBody.ToString().Trim(), codeBlocks);
                StoreCppSection(result, currentTag, currentFirst, currentDir, body);
                currentBody.Clear();
                currentFirst = string.Empty;
                currentDir = string.Empty;
            }

            foreach (var rawLine in fullText.Split('\n'))
            {
                var trimmed = rawLine.TrimStart();

                // Try to match a Doxygen tag at the start of this (trimmed) line.
                var tagMatch = _cppTagLine.Match(trimmed);
                if (tagMatch.Success && _cppKnownTags.Contains(tagMatch.Groups["tag"].Value))
                {
                    CommitSection();

                    currentTag = tagMatch.Groups["tag"].Value.ToLowerInvariant();
                    currentDir = tagMatch.Groups["dir"].Value;

                    bool takesFirstArg =
                        currentTag == "param"
                        || currentTag == "tparam"
                        || currentTag == "throws" || currentTag == "throw" || currentTag == "exception"
                        || currentTag == "retval";

                    if (takesFirstArg)
                    {
                        currentFirst = tagMatch.Groups["first"].Value;
                        // Body starts after the entire matched prefix (including firstArg).
                        var rest = trimmed.Substring(tagMatch.Length).TrimStart();
                        currentBody.Append(rest);
                    }
                    else
                    {
                        // For all other tags, the \first word (if any) is the start of the body.
                        string firstWord = tagMatch.Groups["first"].Success
                            ? tagMatch.Groups["first"].Value
                            : string.Empty;
                        var rest = trimmed.Substring(tagMatch.Length).TrimStart();
                        if (!string.IsNullOrEmpty(firstWord))
                        {
                            currentBody.Append(firstWord);
                            if (!string.IsNullOrEmpty(rest))
                                currentBody.Append(" ").Append(rest);
                        }
                        else
                        {
                            currentBody.Append(rest);
                        }
                    }
                }
                else
                {
                    // Continuation line.
                    if (currentBody.Length > 0)
                        currentBody.Append("\n");
                    currentBody.Append(rawLine);
                }
            }

            CommitSection(); // flush last open section

            // ── Step 4: promote Brief → Summary ──────────────────────────────────
            if (string.IsNullOrWhiteSpace(result.Summary))
            {
                if (!string.IsNullOrWhiteSpace(result.Brief))
                    result.Summary = result.Brief;
                else if (!string.IsNullOrWhiteSpace(result.Details))
                {
                    // Use the first paragraph of implicit text as the brief.
                    var paras = result.Details.Split(
                        new[] { "\n\n" }, 2, StringSplitOptions.None);
                    result.Summary = paras[0].Trim();
                    result.Details = paras.Length > 1 ? paras[1].Trim() : string.Empty;
                }
            }

            // ── Step 5: merge Details → Remarks ──────────────────────────────────
            if (!string.IsNullOrWhiteSpace(result.Details))
            {
                if (string.IsNullOrWhiteSpace(result.Remarks))
                    result.Remarks = result.Details;
                else
                    result.Remarks = result.Details + "\n\n" + result.Remarks;
            }

            // ── Step 6: append \remark to Remarks ────────────────────────────────
            if (!string.IsNullOrWhiteSpace(result.Remark))
            {
                if (string.IsNullOrWhiteSpace(result.Remarks))
                    result.Remarks = result.Remark;
                else
                    result.Remarks += "\n\n" + result.Remark;
            }

            // ── Step 7: classify \sa / \see entries into SeeAlsos ────────────────
            foreach (var entry in result.SeeEntries)
            {
                if (entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    result.SeeAlsos.Add(new SeeAlsoEntry { Label = entry, Href = entry });
                else
                    result.SeeAlsos.Add(new SeeAlsoEntry
                    {
                        Label = SimplifyCref(entry),
                        Cref = entry
                    });
            }

            return result;
        }

        // ── Strip a single C++ doc comment line of its leading marker ─────────────

        /// <summary>
        /// Strips the leading C++ documentation comment decorator from a single line of text.<br/>
        /// Handles all recognized C++ comment prefix styles.
        /// </summary>
        /// <param name="line">
        /// A single line of raw comment text potentially starting with a C++ comment decorator.
        /// </param>
        /// <returns>
        /// The line with its leading comment decorator removed. Returns the original line<br/>
        /// unchanged if no recognized decorator is found (treated as a raw continuation line).
        /// </returns>
        /// <remarks>
        /// <para>The method checks for the following prefix patterns in order:</para>
        /// <list type="bullet">
        /// <item><description><c>///</c> (triple slash) — strips 3 characters plus optional trailing space.</description></item>
        /// <item><description><c>//!</c> (double-slash-exclamation) — strips 3 characters plus optional trailing space.</description></item>
        /// <item><description><c>/**</c> or <c>/*!</c> (block comment opener) — strips 3 characters plus optional trailing space.</description></item>
        /// <item><description><c>*</c> at start (block comment body line) — matches via regex <c>^\*(?!/)</c> to exclude the <c>*/</c> closer.</description></item>
        /// </list>
        /// <para>For each matched prefix, an additional leading space is also stripped if present,<br/>
        /// following the convention that documentation text is typically separated from<br/>
        /// the comment marker by a single space.</para>
        /// </remarks>
        private static string StripCppLinePrefix(string line)
        {
            var t = line.TrimStart();

            // ///<optional space>
            if (t.StartsWith("///"))
            {
                t = t.Substring(3);
                if (t.Length > 0 && t[0] == ' ') t = t.Substring(1);
                return t;
            }

            // //!<optional space>
            if (t.StartsWith("//!"))
            {
                t = t.Substring(3);
                if (t.Length > 0 && t[0] == ' ') t = t.Substring(1);
                return t;
            }

            // /**  or  /*!
            if (t.StartsWith("/**") || t.StartsWith("/*!"))
            {
                t = t.Substring(3);
                if (t.Length > 0 && t[0] == ' ') t = t.Substring(1);
                return t;
            }

            // Interior line of a block comment: leading whitespace then *
            // but NOT */ (that is the closer, already stripped by the caller).
            var asteriskMatch = Regex.Match(t, @"^\*(?!/)");
            if (asteriskMatch.Success)
            {
                t = t.Substring(asteriskMatch.Length);
                if (t.Length > 0 && t[0] == ' ') t = t.Substring(1);
                return t;
            }

            return line; // nothing to strip (raw continuation line)
        }

        // ── Store a completed section into the ParsedDocComment ───────────────────

        /// <summary>
        /// Stores a completed documentation section into the appropriate field of a<br/>
        /// <see cref="ParsedDocComment"/> based on the Doxygen tag that introduced the section.
        /// </summary>
        /// <param name="r">
        /// The <see cref="ParsedDocComment"/> being populated with parsed documentation content.
        /// </param>
        /// <param name="tag">
        /// The Doxygen command name (lowercase) that introduced this section (e.g., <c>"brief"</c>, <c>"param"</c>, <c>"note"</c>).<br/>
        /// Use <c>"implicit"</c> for text appearing before any explicit Doxygen tag.
        /// </param>
        /// <param name="firstArg">
        /// The first non-space token following the Doxygen command. Used as the parameter name<br/>
        /// for <c>\param</c>, <c>\tparam</c>, <c>\retval</c>, <c>\throws</c>, <c>\throw</c>, <c>\exception</c>,<br/>
        /// or as the paragraph title for <c>\par</c>.
        /// </param>
        /// <param name="dir">
        /// The optional direction suffix for <c>\param</c> commands (<c>"in"</c>, <c>"out"</c>, <c>"in,out"</c>).<br/>
        /// Empty string for tags that don't support direction suffixes.
        /// </param>
        /// <param name="body">
        /// The processed body text of the section, with inline markup already converted<br/>
        /// to intermediate token format by <see cref="ProcessCppInlines"/>.
        /// </param>
        /// <remarks>
        /// <para>The method dispatches on the <paramref name="tag"/> value to populate the correct field:</para>
        /// <list type="bullet">
        /// <item><description><c>implicit</c> → <see cref="ParsedDocComment.Details"/> (later merged into Remarks).</description></item>
        /// <item><description><c>brief</c>/<c>short</c> → <see cref="ParsedDocComment.Brief"/> (later promoted to Summary).</description></item>
        /// <item><description><c>details</c>/<c>detail</c> → <see cref="ParsedDocComment.Details"/>.</description></item>
        /// <item><description><c>par</c> → <see cref="ParsedDocComment.Remarks"/> (with optional title prefix).</description></item>
        /// <item><description><c>param</c> → <see cref="ParsedDocComment.Params"/> (appended or merged with existing entry by name).</description></item>
        /// <item><description><c>tparam</c> → <see cref="ParsedDocComment.TypeParams"/> (appended or merged).</description></item>
        /// <item><description><c>return</c>/<c>returns</c> → <see cref="ParsedDocComment.Returns"/>.</description></item>
        /// <item><description><c>retval</c> → <see cref="ParsedDocComment.RetVals"/> (new entry with name and description).</description></item>
        /// <item><description><c>throws</c>/<c>throw</c>/<c>exception</c> → <see cref="ParsedDocComment.Exceptions"/>.</description></item>
        /// <item><description><c>note</c>/<c>warning</c>/<c>attention</c>/<c>deprecated</c>/<c>bug</c>/<c>todo</c> → Corresponding admonition field.</description></item>
        /// <item><description><c>since</c>/<c>version</c>/<c>author</c>/<c>date</c>/<c>copyright</c> → Corresponding meta-information field.</description></item>
        /// <item><description><c>pre</c>/<c>post</c>/<c>invariant</c> → Corresponding contract field.</description></item>
        /// <item><description><c>remark</c>/<c>remarks</c> → <see cref="ParsedDocComment.Remark"/> (later merged into Remarks).</description></item>
        /// <item><description><c>sa</c>/<c>see</c> → <see cref="ParsedDocComment.SeeEntries"/> (split on commas/newlines).</description></item>
        /// <item><description><c>example</c> → <see cref="ParsedDocComment.Example"/>.</description></item>
        /// <item><description>All other recognised tags → Silently ignored (structural/grouping commands).</description></item>
        /// </list>
        /// <para>Empty sections are skipped except for flag-style tags (<c>deprecated</c>, <c>bug</c>, <c>todo</c>,<br/>
        /// <c>note</c>, <c>warning</c>, <c>attention</c>) which receive a default placeholder text.</para>
        /// </remarks>
        private static void StoreCppSection(
            ParsedDocComment r,
            string tag,
            string firstArg,
            string dir,
            string body)
        {
            // Skip entirely empty sections (except for tags that are flags).
            bool isEmpty = string.IsNullOrWhiteSpace(body) && string.IsNullOrEmpty(firstArg);
            if (isEmpty
                && tag != "deprecated" && tag != "bug" && tag != "todo"
                && tag != "note" && tag != "warning" && tag != "attention")
                return;

            switch (tag)
            {
                // ── Implicit text before any tag ─────────────────────────────────
                case "implicit":
                    r.Details = AppendField(r.Details, body);
                    break;

                // ── Brief / Summary ───────────────────────────────────────────────
                case "brief":
                case "short":
                    r.Brief = AppendField(r.Brief, body);
                    break;

                // ── Extended description ──────────────────────────────────────────
                case "details":
                case "detail":
                    r.Details = AppendField(r.Details, body);
                    break;

                // ── Custom paragraph (\par title) ─────────────────────────────────
                case "par":
                    // The body of \par goes to Remarks.  If there is a firstArg
                    // (the paragraph title) we prefix it.
                    var parText = string.IsNullOrEmpty(firstArg)
                        ? body
                        : (string.IsNullOrEmpty(body)
                            ? firstArg
                            : firstArg + "\n" + body);
                    r.Remarks = AppendField(r.Remarks, parText);
                    break;

                // ── Parameters ────────────────────────────────────────────────────
                case "param":
                    {
                        if (string.IsNullOrEmpty(firstArg)) break;
                        var existing = r.Params.Find(p => p.Name == firstArg);
                        if (existing != null)
                            existing.Description = AppendText(existing.Description, body);
                        else
                            r.Params.Add(new ParamEntry
                            {
                                Name = firstArg,
                                Description = body,
                                Direction = NormaliseDirection(dir)
                            });
                        break;
                    }

                case "tparam":
                    {
                        if (string.IsNullOrEmpty(firstArg)) break;
                        var existing = r.TypeParams.Find(p => p.Name == firstArg);
                        if (existing != null)
                            existing.Description = AppendText(existing.Description, body);
                        else
                            r.TypeParams.Add(new ParamEntry
                            {
                                Name = firstArg,
                                Description = body
                            });
                        break;
                    }

                // ── Return value ──────────────────────────────────────────────────
                case "return":
                case "returns":
                    r.Returns = AppendField(r.Returns, body);
                    break;

                case "retval":
                    if (!string.IsNullOrEmpty(firstArg))
                        r.RetVals.Add(new RetValEntry { Name = firstArg, Description = body });
                    break;

                // ── Exceptions ────────────────────────────────────────────────────
                case "throws":
                case "throw":
                case "exception":
                    r.Exceptions.Add(new ExceptionEntry
                    {
                        Type = firstArg,
                        FullCref = firstArg,
                        Description = body
                    });
                    break;

                // ── Admonitions ───────────────────────────────────────────────────
                case "note":
                    r.Note = string.IsNullOrWhiteSpace(r.Note)
                        ? (string.IsNullOrWhiteSpace(body) ? "(note)" : body)
                        : r.Note + "\n\n" + body;
                    break;

                case "warning":
                    r.Warning = string.IsNullOrWhiteSpace(r.Warning)
                        ? (string.IsNullOrWhiteSpace(body) ? "(warning)" : body)
                        : r.Warning + "\n\n" + body;
                    break;

                case "attention":
                    r.Attention = string.IsNullOrWhiteSpace(r.Attention)
                        ? (string.IsNullOrWhiteSpace(body) ? "(attention)" : body)
                        : r.Attention + "\n\n" + body;
                    break;

                case "deprecated":
                    r.Deprecated = string.IsNullOrWhiteSpace(body) ? "(deprecated)" : body;
                    break;

                // ── Meta-information ──────────────────────────────────────────────
                case "since":
                    r.Since = AppendField(r.Since, body);
                    break;

                case "version":
                    r.Version = AppendField(r.Version, body);
                    break;

                case "author":
                    r.Author = AppendField(r.Author, body);
                    break;

                case "date":
                    r.Date = AppendField(r.Date, body);
                    break;

                case "copyright":
                    r.Copyright = AppendField(r.Copyright, body);
                    break;

                // ── Quality annotations ───────────────────────────────────────────
                case "bug":
                    r.Bug = string.IsNullOrWhiteSpace(body) ? "(bug)" : body;
                    break;

                case "todo":
                    r.Todo = string.IsNullOrWhiteSpace(body) ? "(todo)" : body;
                    break;

                // ── Contracts ─────────────────────────────────────────────────────
                case "pre":
                    r.Pre = AppendField(r.Pre, body);
                    break;

                case "post":
                    r.Post = AppendField(r.Post, body);
                    break;

                case "invariant":
                    r.Invariant = AppendField(r.Invariant, body);
                    break;

                // ── Remarks ───────────────────────────────────────────────────────
                case "remark":
                case "remarks":
                    r.Remark = AppendField(r.Remark, body);
                    break;

                // ── See also / cross-references ───────────────────────────────────
                case "sa":
                case "see":
                    // Body may be a comma-separated or newline-separated list.
                    foreach (var rawEntry in body.Split(
                        new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var e = rawEntry.Trim();
                        if (!string.IsNullOrEmpty(e))
                            r.SeeEntries.Add(e);
                    }
                    break;

                // ── Example ───────────────────────────────────────────────────────
                case "example":
                    r.Example = AppendField(r.Example, body);
                    break;

                // ── Structural / grouping / conditional tags — silently ignore ─────
                // These are valid Doxygen commands but produce no visible content.
                default:
                    break;
            }
        }

        // ── Inline markup processing for C++ text ─────────────────────────────────
        //
        // Converts all C++ / Doxygen inline markup (already stripped of comment
        // markers) into the same intermediate token format that DocCommentControl
        // already understands:
        //   `code`  [LINK cref=x]label[/LINK]  [LINK href=x]label[/LINK]
        //   [PARAMREF]name[/PARAMREF]  [CODE]…[/CODE]

        /// <summary>
        /// Processes inline Doxygen markup in a section body text, converting all recognized<br/>
        /// Doxygen commands and HTML subset tags into the intermediate token format understood<br/>
        /// by <see cref="DocCommentControl"/>.
        /// </summary>
        /// <param name="text">
        /// The raw section body text potentially containing Doxygen inline commands<br/>
        /// (<c>\p</c>, <c>\a</c>, <c>\e</c>, <c>\em</c>, <c>\c</c>, <c>\b</c>, <c>\ref</c>, <c>\link</c>, <c>\f$</c>)<br/>
        /// and HTML subset tags (<c>&lt;a href&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;em&gt;</c>, <c>&lt;br&gt;</c>, etc.).
        /// </param>
        /// <param name="codeBlocks">
        /// A list of code block bodies previously extracted during Step 2 of <see cref="ParseCpp"/>.<br/>
        /// Each entry corresponds to a <c>\x01CODE{n}\x01</c> placeholder in the text.
        /// </param>
        /// <returns>
        /// The processed text with all inline markup converted to intermediate tokens:
        /// <list type="bullet">
        /// <item><description><c>`code`</c> — Inline code (from <c>\p</c>, <c>\a</c>, <c>\e</c>, <c>\em</c>, <c>\c</c>, <c>\b</c>, <c>\f$</c>).</description></item>
        /// <item><description><c>[LINK cref=...]label[/LINK]</c> — Code symbol references (from <c>\ref</c>, <c>\link</c>).</description></item>
        /// <item><description><c>[LINK href=...]label[/LINK]</c> — External URL links (from HTML <c>&lt;a href&gt;</c>).</description></item>
        /// <item><description><c>[CODE]...[/CODE]</c> — Block code (from <c>\code</c>, <c>\verbatim</c>, <c>\f[</c>, <c>\f{</c>).</description></item>
        /// <item><description><c>[PARAMREF]name[/PARAMREF]</c> — Not generated by Doxygen parser (XML-only).</description></item>
        /// <item><description><c>[BOLD]...[/BOLD]</c> — Not generated by Doxygen parser (<c>\b</c> renders as inline code).</description></item>
        /// <item><description><c>[ITALIC]...[/ITALIC]</c> — Not generated by Doxygen parser.</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>The method applies the following transformations in order:</para>
        /// <list type="number">
        /// <item><description><b>Restore code block placeholders:</b> Replaces <c>\x01CODE{n}\x01</c> sentinels with <c>[CODE]...[/CODE]</c> tokens using the <paramref name="codeBlocks"/> list.</description></item>
        /// <item><description><b>LaTeX inline math:</b> Converts <c>\f$...\\f$</c> to <c>`...`</c> (inline code).</description></item>
        /// <item><description><b>\link ... \endlink:</b> Converts to <c>[LINK cref=target]label[/LINK]</c>.</description></item>
        /// <item><description><b>HTML anchors:</b> Converts <c>&lt;a href="..."&gt;...&lt;/a&gt;</c> to <c>[LINK href=...]...[/LINK]</c>.</description></item>
        /// <item><description><b>\ref:</b> Converts to <c>[LINK cref=target]label[/LINK]</c>, using <see cref="SimplifyCref"/> for the label if none provided.</description></item>
        /// <item><description><b>Inline word commands:</b> Converts <c>\p</c>/<c>\a</c>/<c>\e</c>/<c>\em</c>/<c>\c</c>/<c>\b</c> followed by a word to <c>`word`</c>.</description></item>
        /// <item><description><b>HTML list items:</b> Replaces <c>&lt;li&gt;</c> with bullet point prefix <c>"\n  • "</c>.</description></item>
        /// <item><description><b>HTML list containers:</b> Strips <c>&lt;ul&gt;</c>, <c>&lt;ol&gt;</c>, <c>&lt;li&gt;</c> container tags.</description></item>
        /// <item><description><b>HTML paragraphs:</b> Replaces <c>&lt;p&gt;</c>/<c>&lt;/p&gt;</c> with double newlines.</description></item>
        /// <item><description><b>HTML inline formatting:</b> Strips <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;em&gt;</c>, <c>&lt;strong&gt;</c>, <c>&lt;tt&gt;</c>, <c>&lt;code&gt;</c>, <c>&lt;var&gt;</c>, <c>&lt;u&gt;</c>, <c>&lt;s&gt;</c>, <c>&lt;strike&gt;</c>, <c>&lt;small&gt;</c>, <c>&lt;big&gt;</c>, <c>&lt;sup&gt;</c>, <c>&lt;sub&gt;</c>.</description></item>
        /// <item><description><b>HTML line breaks:</b> Replaces <c>&lt;br&gt;</c>/<c>&lt;br/&gt;</c> with newline.</description></item>
        /// <item><description><b>Stray command cleanup:</b> Removes any unconsumed Doxygen commands via <see cref="_cppStrayCmd"/>.</description></item>
        /// <item><description><b>Blank line normalization:</b> Collapses 3+ consecutive newlines to 2 (paragraph breaks).</description></item>
        /// </list>
        /// </remarks>
        private static string ProcessCppInlines(string text, List<string> codeBlocks)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // ── Restore \code / \verbatim placeholders ────────────────────────────
            text = Regex.Replace(text, @"\x01CODE(\d+)\x01", m =>
            {
                if (int.TryParse(m.Groups[1].Value, out int idx)
                    && idx >= 0 && idx < codeBlocks.Count)
                    return $"\n[CODE]{codeBlocks[idx]}[/CODE]\n";
                return string.Empty;
            });

            // ── LaTeX inline math → `math` ────────────────────────────────────────
            text = _cppLatexInline.Replace(text,
                m => $"`{m.Groups["math"].Value.Trim()}`");

            // ── \link … \endlink → [LINK cref=…] ──────────────────────────────────
            text = _cppLinkCmd.Replace(text,
                m => $"[LINK cref={m.Groups["target"].Value}]{m.Groups["label"].Value}[/LINK]");

            // ── HTML <a href="…">…</a> → [LINK href=…] ───────────────────────────
            text = _cppHtmlAnchor.Replace(text,
                m => $"[LINK href={m.Groups["href"].Value}]{m.Groups["label"].Value}[/LINK]");

            // ── \ref target → [LINK cref=…] ──────────────────────────────────────
            text = _cppRefCmd.Replace(text, m =>
            {
                var target = m.Groups["target"].Value;
                var label = m.Groups["label"].Success && m.Groups["label"].Length > 0
                    ? m.Groups["label"].Value
                    : SimplifyCref(target);
                return $"[LINK cref={target}]{label}[/LINK]";
            });

            // ── \p name / \a name / \e name / \em name / \c name / \b word ────────
            // All rendered as inline code (`word`).
            text = _cppInlineWordCmd.Replace(text,
                m => $"`{m.Groups["word"].Value}`");

            // ── HTML <li> → bullet ────────────────────────────────────────────────
            text = _cppHtmlListItem.Replace(text, "\n  • ");
            text = _cppHtmlListTags.Replace(text, string.Empty);

            // ── HTML <p> / </p> → paragraph break ────────────────────────────────
            text = _cppHtmlPara.Replace(text, "\n\n");

            // ── HTML inline formatting tags — strip ───────────────────────────────
            text = _cppHtmlInline.Replace(text, string.Empty);

            // ── HTML <br> → newline ───────────────────────────────────────────────
            text = _cppHtmlBr.Replace(text, "\n");

            // ── Remove any stray unprocessed Doxygen commands ─────────────────────
            text = _cppStrayCmd.Replace(text, string.Empty);

            // ── Collapse excessive blank lines ────────────────────────────────────
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            return text.Trim();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Normalizes a C++ parameter direction string to one of the recognized canonical forms.<br/>
        /// Used to standardize the optional <c>[in]</c>, <c>[out]</c>, <c>[in,out]</c> suffixes<br/>
        /// on <c>\param</c> Doxygen commands.
        /// </summary>
        /// <param name="dir">
        /// The raw direction string extracted from the <c>[dir]</c> suffix of a <c>\param</c> command.
        /// </param>
        /// <returns>
        /// One of the following normalized direction strings:
        /// <list type="bullet">
        /// <item><description><c>"in"</c> — Input parameter (normalized from <c>"in"</c> with optional whitespace).</description></item>
        /// <item><description><c>"out"</c> — Output parameter (normalized from <c>"out"</c> with optional whitespace).</description></item>
        /// <item><description><c>"in,out"</c> — Bidirectional parameter (normalized from <c>"in,out"</c> or <c>"inout"</c>).</description></item>
        /// <item><description>Original input — Returned unchanged if it doesn't match any recognized pattern.</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// Whitespace and tab characters within the direction string are removed before matching,<br/>
        /// so <c>"in , out"</c> is normalized to <c>"in,out"</c>.
        /// </remarks>
        private static string NormaliseDirection(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return string.Empty;
            switch (dir.ToLowerInvariant().Replace(" ", "").Replace("\t", ""))
            {
                case "in": return "in";
                case "out": return "out";
                case "in,out":
                case "inout": return "in,out";
                default: return dir;
            }
        }

        /// <summary>
        /// Appends a new value to an existing documentation field, separating them<br/>
        /// with a double newline (paragraph break) if both are non-empty.
        /// </summary>
        /// <param name="field">
        /// The current value of the documentation field, or <c>null</c>/empty if not yet populated.
        /// </param>
        /// <param name="value">
        /// The new value to append to the field.
        /// </param>
        /// <returns>
        /// The combined field value. Returns the original <paramref name="field"/> if <paramref name="value"/> is empty,<br/>
        /// or the <paramref name="value"/> if the <paramref name="field"/> is empty. Otherwise, returns<br/>
        /// <c>field + "\n\n" + value</c> to create a paragraph break between the two sections.
        /// </returns>
        private static string AppendField(string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return field;
            return string.IsNullOrWhiteSpace(field) ? value : field + "\n\n" + value;
        }

        /// <summary>
        /// Appends a new value to an existing text field (used for parameter descriptions),<br/>
        /// separating them with a double newline (paragraph break) if both are non-empty.
        /// </summary>
        /// <param name="existing">
        /// The current description text, or <c>null</c>/empty if not yet populated.
        /// </param>
        /// <param name="value">
        /// The new text to append.
        /// </param>
        /// <returns>
        /// The combined text. Returns the original <paramref name="existing"/> if <paramref name="value"/> is empty,<br/>
        /// or the <paramref name="value"/> if <paramref name="existing"/> is empty. Otherwise, returns<br/>
        /// <c>existing + "\n\n" + value</c>.
        /// </returns>
        private static string AppendText(string existing, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return existing;
            return string.IsNullOrWhiteSpace(existing)
                ? value
                : existing + "\n\n" + value;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  ReadInnerMixed — shared XML/inline reader used by the C# parser
        //  (C++ text arrives here already converted to the same token format)
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Recursively reads an XML element's content, converting all child elements<br/>
        /// and text nodes into the intermediate token format consumed by <see cref="DocCommentControl"/>.<br/>
        /// This is the core method that translates XML documentation inline markup into tokens.
        /// </summary>
        /// <param name="el">
        /// The <see cref="XElement"/> to read. Can be any XML documentation element<br/>
        /// (<c>&lt;summary&gt;</c>, <c>&lt;remarks&gt;</c>, <c>&lt;param&gt;</c>, <c>&lt;c&gt;</c>, <c>&lt;see&gt;</c>, etc.).
        /// </param>
        /// <returns>
        /// A string containing the element's content with all inline markup converted to tokens:
        /// <list type="bullet">
        /// <item><description><c>&lt;c&gt;</c> → <c>`content`</c> (inline code).</description></item>
        /// <item><description><c>&lt;code&gt;</c> → <c>\n[CODE]content[/CODE]\n</c> (block code).</description></item>
        /// <item><description><c>&lt;see cref="..."/&gt;</c> → <c>[LINK cref=...]label[/LINK]</c>.</description></item>
        /// <item><description><c>&lt;see href="..."/&gt;</c> → <c>[LINK href=...]label[/LINK]</c>.</description></item>
        /// <item><description><c>&lt;see langword="..."/&gt;</c> → <c>`langword`</c> (inline code for language keywords).</description></item>
        /// <item><description><c>&lt;paramref name="..."/&gt;</c> → <c>[PARAMREF]name[/PARAMREF]</c>.</description></item>
        /// <item><description><c>&lt;typeparamref name="..."/&gt;</c> → <c>[PARAMREF]name[/PARAMREF]</c>.</description></item>
        /// <item><description><c>&lt;para&gt;</c> → <c>\n\n</c> prefix + recursive content.</description></item>
        /// <item><description><c>&lt;br/&gt;</c> → <c>\n</c> (line break).</description></item>
        /// <item><description><c>&lt;list&gt;</c> → Bullet/numbered list with terms and descriptions.</description></item>
        /// <item><description><c>&lt;b&gt;</c>/<c>&lt;strong&gt;</c> → <c>[BOLD]content[/BOLD]</c>.</description></item>
        /// <item><description><c>&lt;i&gt;</c>/<c>&lt;em&gt;</c> → <c>[ITALIC]content[/ITALIC]</c>.</description></item>
        /// <item><description><c>&lt;u&gt;</c> → <c>[UNDERLINE]content[/UNDERLINE]</c>.</description></item>
        /// <item><description><c>&lt;s&gt;</c>/<c>&lt;strike&gt;</c> → <c>[STRIKE]content[/STRIKE]</c>.</description></item>
        /// <item><description><c>&lt;pre&gt;</c> → Recursive content (preformatted block).</description></item>
        /// <item><description><c>&lt;value&gt;</c> → Recursive content.</description></item>
        /// <item><description>Unknown elements → Raw text content (children's values concatenated).</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>The method processes each node in the element's content:</para>
        /// <list type="bullet">
        /// <item><description><b>XText nodes:</b> Whitespace is collapsed via <see cref="CollapseWhitespace"/>.</description></item>
        /// <item><description><b>XElement children:</b> Dispatched to the appropriate handler based on the element name.</description></item>
        /// </list>
        /// <para>Nested formatting is fully supported — e.g., <c>&lt;b&gt;&lt;i&gt;text&lt;/i&gt;&lt;/b&gt;</c> produces<br/>
        /// <c>[BOLD][ITALIC]text[/ITALIC][/BOLD]</c> through recursive invocation.</para>
        /// </remarks>
        internal static string ReadInnerMixed(XElement el)
        {
            if (el == null) return null;

            var sb = new StringBuilder();
            foreach (var node in el.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        sb.Append(CollapseWhitespace(text.Value));
                        break;

                    case XElement child:
                        switch (child.Name.LocalName.ToLower())
                        {
                            // ── Inline code ───────────────────────────────────────
                            case "c":
                                sb.Append($"`{child.Value}`");
                                break;

                            // ── Block code ────────────────────────────────────────
                            case "code":
                                sb.Append($"\n[CODE]{child.Value}[/CODE]\n");
                                break;

                            // ── see — cref, href, langword ────────────────────────
                            case "see":
                                var seeCref = child.Attribute("cref")?.Value ?? string.Empty;
                                var seeHref = child.Attribute("href")?.Value ?? string.Empty;
                                var seeLw = child.Attribute("langword")?.Value ?? string.Empty;
                                if (!string.IsNullOrEmpty(seeLw))
                                {
                                    sb.Append($"`{seeLw}`");
                                }
                                else
                                {
                                    var seeLabel = !child.IsEmpty
                                        ? child.Value
                                        : !string.IsNullOrEmpty(seeCref)
                                            ? SimplifyCref(seeCref) : seeHref;
                                    sb.Append(!string.IsNullOrEmpty(seeHref)
                                        ? $"[LINK href={seeHref}]{seeLabel}[/LINK]"
                                        : $"[LINK cref={seeCref}]{seeLabel}[/LINK]");
                                }
                                break;

                            // ── seealso inline ────────────────────────────────────
                            case "seealso":
                                var isaCref = child.Attribute("cref")?.Value ?? string.Empty;
                                var isaHref = child.Attribute("href")?.Value ?? string.Empty;
                                var isaLabel = !child.IsEmpty
                                    ? child.Value
                                    : !string.IsNullOrEmpty(isaCref)
                                        ? SimplifyCref(isaCref) : isaHref;
                                sb.Append(!string.IsNullOrEmpty(isaHref)
                                    ? $"[LINK href={isaHref}]{isaLabel}[/LINK]"
                                    : $"[LINK cref={isaCref}]{isaLabel}[/LINK]");
                                break;

                            // ── paramref ──────────────────────────────────────────
                            case "paramref":
                                sb.Append(
                                    $"[PARAMREF]{child.Attribute("name")?.Value}[/PARAMREF]");
                                break;

                            // ── typeparamref ──────────────────────────────────────
                            case "typeparamref":
                                sb.Append(
                                    $"[PARAMREF]{child.Attribute("name")?.Value}[/PARAMREF]");
                                break;

                            // ── para ──────────────────────────────────────────────
                            case "para":
                                sb.Append("\n\n");
                                sb.Append(ReadInnerMixed(child));
                                break;

                            // ── br ────────────────────────────────────────────────
                            case "br":
                                sb.Append("\n");
                                break;

                            // ── list ──────────────────────────────────────────────
                            case "list":
                                var listType = child.Attribute("type")?.Value ?? "bullet";
                                var header = child.Element("listheader");
                                if (header != null)
                                {
                                    var ht = header.Element("term")?.Value
                                             ?? ReadInnerMixed(header);
                                    if (!string.IsNullOrEmpty(ht))
                                        sb.Append($"\n  {ht}");
                                }
                                int listIdx = 1;
                                foreach (var item in child.Elements("item"))
                                {
                                    var termEl = item.Element("term");
                                    var descEl = item.Element("description");
                                    var term = termEl != null ? ReadInnerMixed(termEl) : null;
                                    var desc = descEl != null
                                        ? ReadInnerMixed(descEl) : item.Value;
                                    var bullet = listType == "number"
                                        ? $"{listIdx}." : "•";
                                    sb.Append(term != null
                                        ? $"\n  {bullet} {term}: {desc}"
                                        : $"\n  {bullet} {desc}");
                                    listIdx++;
                                }
                                break;

                            // ── value ─────────────────────────────────────────────
                            case "value":
                                sb.Append(ReadInnerMixed(child));
                                break;

                            // ── pre — preformatted block, recurse to honour inner tags ──
                            case "pre":
                                sb.Append(ReadInnerMixed(child));
                                break;

                            // ── bold ──────────────────────────────────────────────
                            case "b":
                            case "strong":
                                sb.Append($"[BOLD]{ReadInnerMixed(child)}[/BOLD]");
                                break;

                            // ── italic ────────────────────────────────────────────
                            case "i":
                            case "em":
                                sb.Append($"[ITALIC]{ReadInnerMixed(child)}[/ITALIC]");
                                break;

                            // ── underline ─────────────────────────────────────────
                            case "u":
                                sb.Append($"[UNDERLINE]{ReadInnerMixed(child)}[/UNDERLINE]");
                                break;

                            // ── strikethrough ─────────────────────────────────────
                            case "s":
                            case "strike":
                                sb.Append($"[STRIKE]{ReadInnerMixed(child)}[/STRIKE]");
                                break;

                            default:
                                sb.Append(child.Value);
                                break;
                        }
                        break;
                }
            }

            return sb.ToString().Trim();
        }

        // ── Shared helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Safely retrieves child elements with the specified name from an XML root element.<br/>
        /// Returns an empty sequence if the root is <c>null</c>, preventing null-reference exceptions.
        /// </summary>
        /// <param name="root">
        /// The parent <see cref="XElement"/> to search for child elements. May be <c>null</c>.
        /// </param>
        /// <param name="name">
        /// The name of the child elements to retrieve (e.g., <c>"param"</c>, <c>"exception"</c>, <c>"seealso"</c>).
        /// </param>
        /// <returns>
        /// An enumerable of <see cref="XElement"/> matching the specified name, or an empty array<br/>
        /// if <paramref name="root"/> is <c>null</c> or contains no matching children.
        /// </returns>
        private static IEnumerable<XElement> SafeElements(XElement root, string name)
            => root?.Elements(name) ?? new XElement[0];

        /// <summary>
        /// Collapses consecutive whitespace characters in a string into single spaces,<br/>
        /// and replaces line breaks with spaces. Used for normalizing XML text node content.
        /// </summary>
        /// <param name="s">
        /// The input string potentially containing line breaks and consecutive whitespace.
        /// </param>
        /// <returns>
        /// A string with all line breaks (<c>\r\n</c>, <c>\n</c>) replaced by spaces and<br/>
        /// consecutive whitespace sequences collapsed to single spaces.
        /// </returns>
        /// <remarks>
        /// This method is used during <see cref="ReadInnerMixed"/> to normalize text nodes<br/>
        /// within XML documentation elements, ensuring consistent whitespace in the output tokens.
        /// </remarks>
        private static string CollapseWhitespace(string s)
            => Regex.Replace(
                s.Replace("\r\n", " ").Replace("\n", " "), @"\s{2,}", " ");

        /// <summary>
        /// Strips the cref type prefix from a code reference string, leaving only the fully-qualified name.<br/>
        /// For example, <c>T:System.String</c> becomes <c>System.String</c>.
        /// </summary>
        /// <param name="cref">
        /// The raw cref attribute value from an XML documentation element (e.g., <c>T:System.String</c>,<br/>
        /// <c>M:System.Linq.Enumerable.Where``1(...)</c>, <c>P:System.Int32.MaxValue</c>).
        /// </param>
        /// <returns>
        /// The cref with the two-character type prefix removed (e.g., <c>T:</c>, <c>M:</c>, <c>P:</c>, <c>F:</c>, <c>E:</c>).<br/>
        /// Returns an empty string if the input is <c>null</c>, empty, or fewer than 3 characters long.
        /// </returns>
        internal static string StripPrefix(string cref)
        {
            if (string.IsNullOrEmpty(cref)) return string.Empty;
            return cref.Length > 2 && cref[1] == ':' ? cref.Substring(2) : cref;
        }

        /// <summary>
        /// Simplifies a cref string to a human-readable member name suitable for display<br/>
        /// in rendered documentation. Strips the type prefix, generic arity markers,<br/>
        /// parameter lists, and namespace qualifiers.
        /// </summary>
        /// <param name="cref">
        /// The raw cref attribute value (e.g., <c>M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})</c>).
        /// </param>
        /// <returns>
        /// The simplified member name (e.g., <c>Where</c>). The method applies the following transformations in order:
        /// <list type="number">
        /// <item><description>Strips the cref type prefix via <see cref="StripPrefix"/> (e.g., <c>M:</c> → removed).</description></item>
        /// <item><description>Removes generic arity markers (e.g., <c>``1</c>, <c>`1</c> → removed).</description></item>
        /// <item><description>Truncates at the first opening parenthesis to remove parameter lists (e.g., <c>MethodName(params)</c> → <c>MethodName</c>).</description></item>
        /// <item><description>Extracts the final component after the last <c>::</c> (C++ scope resolution) or <c>.</c> (C# dot notation).</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <para>Example transformations:</para>
        /// <list type="bullet">
        /// <item><description><c>T:System.String</c> → <c>String</c></description></item>
        /// <item><description><c>M:System.Console.WriteLine(System.String)</c> → <c>WriteLine</c></description></item>
        /// <item><description><c>P:System.Int32.MaxValue</c> → <c>MaxValue</c></description></item>
        /// <item><description><c>M:MyNamespace.MyClass.MyMethod``1(``0)</c> → <c>MyMethod</c></description></item>
        /// <item><description><c>N:MyNamespace</c> → <c>MyNamespace</c></description></item>
        /// </list>
        /// </remarks>
        public static string SimplifyCref(string cref)
        {
            if (string.IsNullOrEmpty(cref)) return string.Empty;
            var name = StripPrefix(cref);
            name = Regex.Replace(name, @"`\d+", string.Empty);
            var paren = name.IndexOf('(');
            if (paren >= 0) name = name.Substring(0, paren);
            // C++ scope resolution  ::
            var scope = name.LastIndexOf("::");
            if (scope >= 0) return name.Substring(scope + 2);
            // C# dot notation
            var dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1) : name;
        }
    }
}