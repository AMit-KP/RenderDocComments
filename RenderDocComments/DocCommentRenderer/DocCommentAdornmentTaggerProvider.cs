using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace RenderDocComments.DocCommentRenderer
{
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
        [Import]
        internal IBufferTagAggregatorFactoryService TagAggregatorFactory
        {
            get; set;
        }

        [Import]
        internal IEditorFormatMapService FormatMapService
        {
            get; set;
        }

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