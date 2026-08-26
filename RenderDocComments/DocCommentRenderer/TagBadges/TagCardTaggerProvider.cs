/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    TagCardTaggerProvider.cs
 *  Purpose: MEF-exported factory supplying TagCardTagger instances to Visual
 *           Studio text views for the four supported languages.
 *
 *  Architecture Role:
 *    Implements IViewTaggerProvider — the entry point through which the VS editor
 *    requests a tag-card tagger when a document view is opened. Discovered via
 *    MEF and filtered by content type, tag type, and text view role.
 *
 *  Key Classes:
 *    TagCardTaggerProvider — IViewTaggerProvider implementation.
 *
 *  Dependencies:
 *    • TagCardTagger.cs — the created instance.
 *    • Microsoft.VisualStudio.Utilities (ContentType / TagType / TextViewRole).
 *
 *  When to Edit:
 *    • Adding a language — add a [ContentType] attribute AND extend the scanner
 *      in TagCardTagger.CollectCommentRanges accordingly.
 *    • Changing singleton scope — see CreateTagger notes (view-keyed on purpose).
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace RenderDocComments.DocCommentRenderer.TagBadges
{
    /// <summary>
    /// MEF-exported factory that supplies <see cref="TagCardTagger"/> instances
    /// to Visual Studio text views for supported languages.
    /// </summary>
    /// <remarks>
    /// <para>Registered content types match the doc renderer exactly:
    /// <c>CSharp</c>, <c>Basic</c>, <c>FSharp</c>/<c>F#</c>, and <c>C/C++</c>.
    /// The provider exports <see cref="IntraTextAdornmentTag"/>s restricted to
    /// document views, excluding auxiliary surfaces such as find results.</para>
    /// </remarks>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [ContentType("FSharp")]
    [ContentType("F#")]
    [ContentType("C/C++")]
    [TagType(typeof(IntraTextAdornmentTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class TagCardTaggerProvider : IViewTaggerProvider
    {
        /// <summary>
        /// Creates (or reuses) the per-view <see cref="TagCardTagger"/>.
        /// </summary>
        /// <typeparam name="T">Requested tag type.</typeparam>
        /// <param name="textView">The hosting text view.</param>
        /// <param name="buffer">The buffer to scan.</param>
        /// <returns>The tagger, or null for non-WPF views / incompatible tag types.</returns>
        /// <remarks>
        /// <para><b>View-keyed singleton:</b> identical rationale to the doc
        /// renderer's provider — each IWpfTextView (both sides of a git-diff window,
        /// the normal editor tab) gets its own tagger bound to its own events,
        /// caret, and font metrics, avoiding stale cross-view state.</para>
        /// </remarks>
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer)
            where T : ITag
        {
            if (!(textView is IWpfTextView wpfView)) return null;

            // Cards read theme colours from the format map; guarantee it exists in
            // the view's property bag (same priming step as DocCommentAdornmentTag).
            return wpfView.Properties.GetOrCreateSingletonProperty(
                typeof(TagCardTagger),
                () => new TagCardTagger(buffer, wpfView))
                as ITagger<T>;
        }
    }
}
