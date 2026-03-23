using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace RenderDocComments.DocCommentRenderer
{
    internal sealed class DocCommentAdornmentLayer
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("RenderDocCommentsLayer")]
        [Order(Before = PredefinedAdornmentLayers.Text)]
        public AdornmentLayerDefinition EditorAdornmentLayer
        {
            get; set;
        }
    }
}