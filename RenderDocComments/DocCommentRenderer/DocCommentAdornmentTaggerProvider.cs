/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    DocCommentAdornmentTaggerProvider.cs
 *  Purpose: MEF-exported factory that supplies DocCommentAdornmentTagger
 *           instances to Visual Studio text views for supported programming
 *           languages.
 *
 *  Architecture Role:
 *    Implements IViewTaggerProvider — the entry point through which the VS
 *    editor infrastructure requests a tagger when a document view is opened.
 *    Discovered by MEF via [Export] and filtered by content type, tag type,
 *    and text view role attributes.
 *
 *  Key Classes:
 *    DocCommentAdornmentTaggerProvider  — IViewTaggerProvider implementation;
 *                                         creates taggers on demand.
 *
 *  Dependencies:
 *    • DocCommentAdornmentTagger.cs  — Creates the singleton tagger instance.
 *    • DocCommentAdornmentTag.cs     — Uses IEditorFormatMap for theme colors.
 *    • Microsoft.VisualStudio.Utilities  (ContentType, TagType, TextViewRole
 *      attributes).
 *
 *  When to Edit:
 *    • Adding support for a new programming language — add a [ContentType]
 *      attribute.
 *    • Changing the tag type (e.g., switching from IntraTextAdornmentTag to a
 *      custom tag type).
 *    • Modifying how the format map is initialized or injecting additional
 *      services into the view.
 *    • Changing the singleton key or tagger lifecycle management.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace RenderDocComments.DocCommentRenderer
{
    /// <summary>
    /// MEF-exported factory that supplies <see cref="DocCommentAdornmentTagger"/> instances<br/>
    /// to Visual Studio text views for supported programming languages.
    /// </summary>
    /// <remarks>
    /// <para>This class implements <see cref="IViewTaggerProvider"/> to create taggers on demand<br/>
    /// when the editor opens files of supported content types. The provider is discovered by<br/>
    /// the Visual Studio composition container via the <see cref="ExportAttribute"/>.</para>
    /// <para>The provider is configured with the following attributes:</para>
    /// <list type="bullet">
    /// <item><description><see cref="ExportAttribute"/> — Exports as <see cref="IViewTaggerProvider"/> for MEF discovery.</description></item>
    /// <item><description><see cref="ContentTypeAttribute"/> — Registers support for multiple content types:
    ///   <list type="bullet">
    ///   <item><description><c>CSharp</c> — C# source files.</description></item>
    ///   <item><description><c>Basic</c> — VB.NET source files.</description></item>
    ///   <item><description><c>FSharp</c> / <c>F#</c> — F# source files.</description></item>
    ///   <item><description><c>C/C++</c> — C and C++ source/header files.</description></item>
    ///   </list>
    /// </description></item>
    /// <item><description><see cref="TagTypeAttribute"/> — Specifies that this provider creates <see cref="IntraTextAdornmentTag"/> instances.</description></item>
    /// <item><description><see cref="TextViewRoleAttribute"/> — Restricts the provider to document views (<see cref="PredefinedTextViewRoles.Document"/>),<br/>
    /// excluding auxiliary views like the preview pane or find results.</description></item>
    /// </list>
    /// <para>The provider ensures each text buffer gets exactly one tagger instance via the<br/>
    /// singleton property pattern, preventing duplicate adornments and redundant processing.</para>
    /// </remarks>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [ContentType("FSharp")]
    [ContentType("F#")]
    [ContentType("C/C++")]
    [TagType(typeof(IntraTextAdornmentTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class DocCommentAdornmentTaggerProvider : IViewTaggerProvider
    {
        /// <summary>
        /// Gets or sets the buffer tag aggregator factory service imported from Visual Studio.<br/>
        /// This service is used to create tag aggregators for the text buffer.
        /// </summary>
        /// <value>
        /// An <see cref="IBufferTagAggregatorFactoryService"/> instance provided by the Visual Studio<br/>
        /// editor infrastructure via MEF composition.
        /// </value>
        /// <remarks>
        /// <para>This property is decorated with <see cref="ImportAttribute"/> to request dependency<br/>
        /// injection from the MEF composition container. The container automatically resolves<br/>
        /// this service when the provider is instantiated.</para>
        /// <para>The tag aggregator factory enables the creation of <see cref="ITagAggregator{T}"/> instances<br/>
        /// which can aggregate tags from multiple taggers into a unified view. While this provider<br/>
        /// does not currently use the aggregator directly, the import is available for potential<br/>
        /// future features such as squiggle aggregation or error tag consolidation.</para>
        /// </remarks>
        [Import]
        internal IBufferTagAggregatorFactoryService TagAggregatorFactory
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the editor format map service imported from Visual Studio.<br/>
        /// This service provides access to theme-aware color and font settings for the text view.
        /// </summary>
        /// <value>
        /// An <see cref="IEditorFormatMapService"/> instance provided by the Visual Studio<br/>
        /// editor infrastructure via MEF composition.
        /// </value>
        /// <remarks>
        /// <para>This property is decorated with <see cref="ImportAttribute"/> to request dependency<br/>
        /// injection from the MEF composition container. The format map service is essential for<br/>
        /// extracting theme colors (foreground, background) that the documentation renderer<br/>
        /// uses to match the IDE's appearance.</para>
        /// <para>The service is used in <see cref="CreateTagger{T}"/> to ensure the <see cref="IEditorFormatMap"/><br/>
        /// is available in the view's property bag before the tagger is created. This enables<br/>
        /// <see cref="DocCommentAdornmentTag"/> to retrieve accurate theme colors for rendering.</para>
        /// </remarks>
        [Import]
        internal IEditorFormatMapService FormatMapService
        {
            get; set;
        }

        /// <summary>
        /// Creates a <see cref="DocCommentAdornmentTagger"/> for the specified text view and buffer.<br/>
        /// This method is called by the Visual Studio editor when a tagger is needed for a view.<br/>
        /// It implements a singleton pattern per buffer to ensure only one tagger instance exists.
        /// </summary>
        /// <typeparam name="T">
        /// The type of tag being requested. Must implement <see cref="ITag"/>.
        /// </typeparam>
        /// <param name="textView">
        /// The <see cref="ITextView"/> hosting the document. Must be an <see cref="IWpfTextView"/><br/>
        /// for WPF-based rendering support.
        /// </param>
        /// <param name="buffer">
        /// The <see cref="ITextBuffer"/> representing the document content being edited.
        /// </param>
        /// <returns>
        /// A <see cref="DocCommentAdornmentTagger"/> instance as an <see cref="ITagger{T}"/>,<br/>
        /// or <c>null</c> if the text view is not a WPF view or the tag type is incompatible.
        /// </returns>
        /// <remarks>
        /// <para>The method performs the following operations:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>View type validation:</b> Verifies that <paramref name="textView"/> is an <see cref="IWpfTextView"/>.<br/>
        /// Returns <c>null</c> for non-WPF views (e.g., native views) since the renderer requires WPF controls.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Format map initialization:</b> Ensures the view's property bag contains an <see cref="IEditorFormatMap"/>.<br/>
        /// If absent, retrieves it from <see cref="FormatMapService"/> and adds it to the properties.<br/>
        /// This step guarantees that <see cref="DocCommentAdornmentTag.CreateControl"/> can access theme colors.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Singleton tagger creation:</b> Uses <see cref="PropertyCollectionExtensions.GetOrCreateSingletonProperty{T}"/><br/>
        /// to create or retrieve the tagger instance associated with the buffer. This ensures:
        ///   <list type="bullet">
        ///   <item><description>Only one tagger exists per buffer, regardless of how many views display it.</description></item>
        ///   <item><description>Tagger state (cache, caret tracking) is shared across all views of the same buffer.</description></item>
        ///   <item><description>Memory efficiency through shared resources and event subscriptions.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// </list>
        /// <para>The tagger is keyed by <c>typeof(DocCommentAdornmentTagger)</c> in the buffer's property bag,<br/>
        /// making it retrievable by other components that need access to the tagger instance.</para>
        /// </remarks>
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer)
            where T : ITag
        {
            if (!(textView is IWpfTextView wpfView)) return null;
            if (FormatMapService != null && !wpfView.Properties.ContainsProperty(typeof(Microsoft.VisualStudio.Text.Classification.IEditorFormatMap)))
            {
                var map = FormatMapService.GetEditorFormatMap(wpfView);
                wpfView.Properties.AddProperty(typeof(Microsoft.VisualStudio.Text.Classification.IEditorFormatMap), map);
            }

            return buffer.Properties.GetOrCreateSingletonProperty(
                typeof(DocCommentAdornmentTagger),
                () => new DocCommentAdornmentTagger(buffer, wpfView))
                as ITagger<T>;
        }
    }
}