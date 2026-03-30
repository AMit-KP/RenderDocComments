using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RenderDocComments.DocCommentRenderer
{
    public class ParsedDocComment
    {
        // ── Shared fields (C# and C++) ─────────────────────────────────────────
        public string Summary { get; set; } = string.Empty;
        public string Remarks { get; set; } = string.Empty;
        public string Returns { get; set; } = string.Empty;
        public string Example { get; set; } = string.Empty;
        public string Permission { get; set; } = string.Empty;
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
        public InheritDocEntry InheritDoc
        {
            get; set;
        }
        public IncludeEntry Include
        {
            get; set;
        }
        public List<ParamEntry> Params { get; set; } = new List<ParamEntry>();
        public List<ParamEntry> TypeParams { get; set; } = new List<ParamEntry>();
        public List<ExceptionEntry> Exceptions { get; set; } = new List<ExceptionEntry>();
        public List<SeeAlsoEntry> SeeAlsos { get; set; } = new List<SeeAlsoEntry>();
        public List<string> CompletionList { get; set; } = new List<string>();
        public bool IsValid
        {
            get; set;
        }
    }

    // ── Data types ────────────────────────────────────────────────────────────────

    public class ParamEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        /// <summary>
        /// Direction hint for C++ \param[in], \param[out], \param[in,out].
        /// Empty string means no direction was specified (normal for C# params too).
        /// </summary>
        public string Direction { get; set; } = string.Empty;
    }

    public class ExceptionEntry
    {
        public string Type { get; set; } = string.Empty;
        public string FullCref { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class SeeAlsoEntry
    {
        public string Label { get; set; } = string.Empty;
        public string Cref { get; set; } = string.Empty;
        public string Href { get; set; } = string.Empty;
    }

    public class InheritDocEntry
    {
        public string Cref { get; set; } = string.Empty;
    }

    public class IncludeEntry
    {
        public string File { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    /// <summary>A \retval entry — specific return value with a name and description.</summary>
    public class RetValEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    // ── Language hint ─────────────────────────────────────────────────────────────

    public enum DocCommentLanguage
    {
        CSharp, Cpp
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  DocCommentParser
    // ═════════════════════════════════════════════════════════════════════════════

    public static class DocCommentParser
    {
        // ── Public entry point ────────────────────────────────────────────────────

        // Detects an XML opening tag inside a stripped comment line.
        private static readonly Regex _xmlTagDetect = new Regex(
            @"<(?:summary|remarks|returns?|param|typeparam|exception|seealso|example|" +
            @"inheritdoc|include|permission|completionlist|value|code|see|para|list|br)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Detects a Doxygen command anywhere in a comment line.
        private static readonly Regex _doxygenTagDetect = new Regex(
            @"[\\@](?:brief|short|details?|param|tparam|returns?|throws?|exception|" +
            @"note|warning|attention|deprecated|since|version|author|date|copyright|" +
            @"bug|todo|pre|post|invariant|remark|sa|see|example|code|verbatim)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parses a raw doc-comment block.
        /// When <paramref name="language"/> is <see cref="DocCommentLanguage.Cpp"/> the
        /// block is first inspected: if it contains XML-doc tags it is parsed with the
        /// C# XML parser (XML-style C++ comments as used by MSVC / VS tooling render
        /// identically to C# comments).  Only pure Doxygen-command blocks use the
        /// Doxygen parser.  For all other languages the XML parser is always used.
        /// </summary>
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

        private enum CppCommentStyle
        {
            Xml, Doxygen
        }

        /// <summary>
        /// Inspects stripped comment lines and returns Xml if any XML doc-tag is found
        /// before any Doxygen command, otherwise returns Doxygen.
        /// Plain text with no decisive markers defaults to Xml so that untagged ///
        /// descriptions render the same way as in C#.
        /// </summary>
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

        // Regex that matches < or > that are NOT part of a valid XML construct:
        //   - << and >> (C++ shift operators used inside <c> tags)
        //   - a lone < that is not followed by a letter, /, ! or ?  (stray angle bracket)
        // These make XDocument.Parse throw even though the developer's intent is clear.
        private static readonly Regex _fixBareAngles = new Regex(
            @"<<|>>|<(?![a-zA-Z/!?])",
            RegexOptions.Compiled);

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

        private static ParsedDocComment ParseCSharp(string rawCommentBlock)
        {
            var lines = rawCommentBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder("<root>");
            foreach (var line in lines)
                sb.AppendLine(Regex.Replace(line, @"^\s*///\s?", ""));
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

        // Strips leading comment decorators for a single source line.
        // Handles:  ///  //!  /**  /*!  * (JavaDoc interior)  */
        private static readonly Regex _cppStripLine = new Regex(
            @"^\s*(?:/{3,}[!/]?|/\*[*!]\s*|(?<=\n)\s*\*(?!/))",
            RegexOptions.Compiled);

        // Matches any Doxygen command starting a logical section:
        //   [\\@]cmd[dir]  firstArg
        // The "dir" group captures optional [in], [out], [in,out].
        // The "first" group captures the first non-space token after the command.
        private static readonly Regex _cppTagLine = new Regex(
            @"^[\\@](?<tag>[a-zA-Z]+)(?:\[(?<dir>[^\]]*)\])?(?:\s+(?<first>\S+))?",
            RegexOptions.Compiled);

        // \code … \endcode  or  \verbatim … \endverbatim  (multi-line)
        private static readonly Regex _cppCodeBlock = new Regex(
            @"[\\@](?:code|verbatim)\b[^\n]*\n(?<body>[\s\S]*?)[\\@](?:endcode|endverbatim)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // \f[ … \f]  or  \f{ … \f}  (display math)
        private static readonly Regex _cppLatexBlock = new Regex(
            @"[\\@]f[\[{](?<math>[\s\S]*?)[\\@]f[\]}]",
            RegexOptions.Compiled);

        // \f$ … \f$  (inline math)
        private static readonly Regex _cppLatexInline = new Regex(
            @"[\\@]f\$(?<math>.*?)[\\@]f\$",
            RegexOptions.Compiled);

        // \link target label \endlink
        private static readonly Regex _cppLinkCmd = new Regex(
            @"[\\@]link\s+(?<target>\S+)\s+(?<label>.*?)\s*[\\@]endlink",
            RegexOptions.Compiled);

        // \ref target  or  \ref target "label"
        private static readonly Regex _cppRefCmd = new Regex(
            @"[\\@]ref\s+(?<target>\S+)(?:\s+""(?<label>[^""]+)"")?",
            RegexOptions.Compiled);

        // \p, \a, \e, \em, \c → inline code;  \b → bold (rendered as inline code)
        private static readonly Regex _cppInlineWordCmd = new Regex(
            @"[\\@](?<cmd>p|a|e|em|c|b)\s+(?<word>\S+)",
            RegexOptions.Compiled);

        // HTML <a href="…">label</a>
        private static readonly Regex _cppHtmlAnchor = new Regex(
            @"<a\s+href=""(?<href>[^""]+)""\s*>(?<label>.*?)</a>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // HTML inline formatting tags — strip (keep text content)
        private static readonly Regex _cppHtmlInline = new Regex(
            @"</?(?:b|i|em|strong|tt|code|var|u|s|strike|small|big|sup|sub)>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // HTML <br> or <br/>
        private static readonly Regex _cppHtmlBr = new Regex(
            @"<br\s*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // HTML <p> / </p> → paragraph break
        private static readonly Regex _cppHtmlPara = new Regex(
            @"</?p\s*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // HTML <ul> / <ol> / <li> — minimal list support
        private static readonly Regex _cppHtmlListItem = new Regex(
            @"<li\s*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _cppHtmlListTags = new Regex(
            @"</?(?:ul|ol|li)\s*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Stray \command that was not consumed by tag processing — remove it
        private static readonly Regex _cppStrayCmd = new Regex(
            @"[\\@](?:param|tparam|return|returns|throws?|exception|brief|short|details?|" +
            @"note|warning|attention|deprecated|since|version|author|date|copyright|" +
            @"bug|todo|pre|post|invariant|remark|remarks?|sa|see|example|par|" +
            @"endlink|endcode|endverbatim)\b[^\n]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        #endregion

        // ── Set of all recognised Doxygen command names ───────────────────────────

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

        private static string AppendField(string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return field;
            return string.IsNullOrWhiteSpace(field) ? value : field + "\n\n" + value;
        }

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

        private static IEnumerable<XElement> SafeElements(XElement root, string name)
            => root?.Elements(name) ?? new XElement[0];

        private static string CollapseWhitespace(string s)
            => Regex.Replace(
                s.Replace("\r\n", " ").Replace("\n", " "), @"\s{2,}", " ");

        internal static string StripPrefix(string cref)
        {
            if (string.IsNullOrEmpty(cref)) return string.Empty;
            return cref.Length > 2 && cref[1] == ':' ? cref.Substring(2) : cref;
        }

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