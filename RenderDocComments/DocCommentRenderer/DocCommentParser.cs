using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RenderDocComments.DocCommentRenderer
{
    public class ParsedDocComment
    {
        public string Summary { get; set; } = string.Empty;
        public string Remarks { get; set; } = string.Empty;
        public string Returns { get; set; } = string.Empty;
        public string Example { get; set; } = string.Empty;
        public string Permission { get; set; } = string.Empty;
        public string PermissionCref { get; set; } = string.Empty;
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

    public class ParamEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class ExceptionEntry
    {
        public string Type { get; set; } = string.Empty; // from cref
        public string FullCref { get; set; } = string.Empty; // raw cref for navigation
        public string Description { get; set; } = string.Empty;
    }

    public class SeeAlsoEntry
    {
        public string Label { get; set; } = string.Empty;
        public string Cref { get; set; } = string.Empty; // raw, e.g. T:Foo.Bar
        public string Href { get; set; } = string.Empty;
    }

    public class InheritDocEntry
    {
        public string Cref { get; set; } = string.Empty; // optional — specific base to inherit from
    }

    public class IncludeEntry
    {
        public string File { get; set; } = string.Empty; // file="path/to/file.xml"
        public string Path { get; set; } = string.Empty; // path="xpath expression"
    }

    public static class DocCommentParser
    {
        public static ParsedDocComment Parse(string rawCommentBlock)
        {
            if (string.IsNullOrWhiteSpace(rawCommentBlock))
                return null;

            var lines = rawCommentBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder("<root>");
            foreach (var line in lines)
                sb.AppendLine(Regex.Replace(line, @"^\s*///\s?", ""));
            sb.Append("</root>");

            try
            {
                var xml = XDocument.Parse(sb.ToString(), LoadOptions.None);
                var root = xml.Root;
                var result = new ParsedDocComment { IsValid = true };

                // ── Simple text sections ──────────────────────────────────────────
                result.Summary = ReadInnerMixed(root?.Element("summary")) ?? string.Empty;
                result.Remarks = ReadInnerMixed(root?.Element("remarks")) ?? string.Empty;
                result.Returns = ReadInnerMixed(root?.Element("returns")) ?? string.Empty;
                result.Example = ReadInnerMixed(root?.Element("example")) ?? string.Empty;

                // permission — has cref attribute indicating which callers are allowed
                var permEl = root?.Element("permission");
                if (permEl != null)
                {
                    result.PermissionCref = StripPrefix(permEl.Attribute("cref")?.Value ?? string.Empty);
                    result.Permission = ReadInnerMixed(permEl) ?? string.Empty;
                }

                // inheritdoc — optional cref to a specific base member
                var idEl = root?.Element("inheritdoc");
                if (idEl != null)
                    result.InheritDoc = new InheritDocEntry
                    {
                        Cref = idEl.Attribute("cref")?.Value ?? string.Empty
                    };

                // include — file + path (XPath) attributes
                var inclEl = root?.Element("include");
                if (inclEl != null)
                    result.Include = new IncludeEntry
                    {
                        File = inclEl.Attribute("file")?.Value ?? string.Empty,
                        Path = inclEl.Attribute("path")?.Value ?? string.Empty
                    };

                // ── param — name attribute ────────────────────────────────────────
                foreach (var p in SafeElements(root, "param"))
                    result.Params.Add(new ParamEntry
                    {
                        Name = p.Attribute("name")?.Value ?? string.Empty,
                        Description = ReadInnerMixed(p) ?? string.Empty
                    });

                // ── typeparam — name attribute ────────────────────────────────────
                foreach (var tp in SafeElements(root, "typeparam"))
                    result.TypeParams.Add(new ParamEntry
                    {
                        Name = tp.Attribute("name")?.Value ?? string.Empty,
                        Description = ReadInnerMixed(tp) ?? string.Empty
                    });

                // ── exception — cref attribute ────────────────────────────────────
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

                // ── seealso — cref or href attribute ─────────────────────────────
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
                        Cref = cref,   // keep raw cref (T:Namespace.Type) for navigation
                        Href = href
                    });
                }

                // ── completionlist — cref attribute ───────────────────────────────
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

        // ── Inner mixed-content reader ────────────────────────────────────────────
        // Handles every attribute variant of every inline tag.

        internal static string ReadInnerMixed(XElement el)
        {
            if (el == null) return null;

            var sb = new StringBuilder();
            foreach (var node in el.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        // Covers both plain text and CDATA sections transparently
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
                                // lang attribute (e.g. lang="csharp") — ignored visually but kept
                                sb.Append($"\n[CODE]{child.Value}[/CODE]\n");
                                break;

                            // ── see — cref, href, langword attributes ─────────────
                            case "see":
                                var seeCref = child.Attribute("cref")?.Value ?? string.Empty;
                                var seeHref = child.Attribute("href")?.Value ?? string.Empty;
                                var seeLangword = child.Attribute("langword")?.Value ?? string.Empty;
                                if (!string.IsNullOrEmpty(seeLangword))
                                {
                                    // langword renders as inline code (e.g. <see langword="null"/>)
                                    sb.Append($"`{seeLangword}`");
                                }
                                else
                                {
                                    var seeLabel = !child.IsEmpty
                                        ? child.Value
                                        : !string.IsNullOrEmpty(seeCref) ? SimplifyCref(seeCref) : seeHref;
                                    if (!string.IsNullOrEmpty(seeHref))
                                        sb.Append($"[LINK href={seeHref}]{seeLabel}[/LINK]");
                                    else
                                        sb.Append($"[LINK cref={seeCref}]{seeLabel}[/LINK]");
                                }
                                break;

                            // ── seealso inline — cref or href ─────────────────────
                            case "seealso":
                                var iSaCref = child.Attribute("cref")?.Value ?? string.Empty;
                                var iSaHref = child.Attribute("href")?.Value ?? string.Empty;
                                var iSaLabel = !child.IsEmpty
                                    ? child.Value
                                    : !string.IsNullOrEmpty(iSaCref) ? SimplifyCref(iSaCref) : iSaHref;
                                if (!string.IsNullOrEmpty(iSaHref))
                                    sb.Append($"[LINK href={iSaHref}]{iSaLabel}[/LINK]");
                                else
                                    sb.Append($"[LINK cref={iSaCref}]{iSaLabel}[/LINK]");
                                break;

                            // ── paramref — name attribute ─────────────────────────
                            case "paramref":
                                sb.Append($"[PARAMREF]{child.Attribute("name")?.Value}[/PARAMREF]");
                                break;

                            // ── typeparamref — name attribute ─────────────────────
                            case "typeparamref":
                                sb.Append($"[PARAMREF]{child.Attribute("name")?.Value}[/PARAMREF]");
                                break;

                            // ── para — paragraph break ────────────────────────────
                            case "para":
                                sb.Append("\n\n");
                                sb.Append(ReadInnerMixed(child));
                                break;

                            // ── br — explicit line break ──────────────────────────
                            case "br":
                                sb.Append("\n");
                                break;

                            // ── list — type="bullet|number|table" ─────────────────
                            case "list":
                                var listType = child.Attribute("type")?.Value ?? "bullet";
                                // listheader optional
                                var header = child.Element("listheader");
                                if (header != null)
                                {
                                    var ht = header.Element("term")?.Value ?? ReadInnerMixed(header);
                                    if (!string.IsNullOrEmpty(ht))
                                        sb.Append($"\n  {ht}");
                                }
                                int idx = 1;
                                foreach (var item in child.Elements("item"))
                                {
                                    var termEl = item.Element("term");
                                    var descEl = item.Element("description");
                                    var term = termEl != null ? ReadInnerMixed(termEl) : null;
                                    var desc = descEl != null ? ReadInnerMixed(descEl) : item.Value;
                                    var bullet = listType == "number" ? $"{idx}." : "•";
                                    sb.Append(term != null
                                        ? $"\n  {bullet} {term}: {desc}"
                                        : $"\n  {bullet} {desc}");
                                    idx++;
                                }
                                break;

                            // ── value — property value description ────────────────
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

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static IEnumerable<XElement> SafeElements(XElement root, string name)
            => root?.Elements(name) ?? new XElement[0];

        private static string CollapseWhitespace(string s)
            => Regex.Replace(s.Replace("\r\n", " ").Replace("\n", " "), @"\s{2,}", " ");

        // Strips type prefix: "T:Foo.Bar" → "Foo.Bar", "M:Foo.Bar.Method" → "Foo.Bar.Method"
        internal static string StripPrefix(string cref)
        {
            if (string.IsNullOrEmpty(cref)) return string.Empty;
            return cref.Length > 2 && cref[1] == ':' ? cref.Substring(2) : cref;
        }

        // Returns just the last segment: "Foo.Bar.Baz" → "Baz"
        public static string SimplifyCref(string cref)
        {
            if (string.IsNullOrEmpty(cref)) return string.Empty;
            var name = StripPrefix(cref);
            // Strip generic arity: "List`1" → "List"
            name = Regex.Replace(name, @"`\d+", string.Empty);
            // Strip method parameters: "Foo.Bar(System.Int32)" → "Foo.Bar"
            var paren = name.IndexOf('(');
            if (paren >= 0) name = name.Substring(0, paren);
            var dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1) : name;
        }
    }
}