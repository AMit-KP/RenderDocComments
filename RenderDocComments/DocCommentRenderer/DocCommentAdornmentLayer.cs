using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace RenderDocComments.DocCommentRenderer
{
    /// <summary>
    /// Defines the editor adornment layer used by Render Doc Comments to render documentation comments.<br/>
    /// This class exports an <see cref="AdornmentLayerDefinition"/> that registers the visual layer<br/>
    /// where rendered documentation comment controls will be displayed in the text editor.
    /// </summary>
    /// <remarks>
    /// The adornment layer is positioned before the <see cref="PredefinedAdornmentLayers.Text"/> layer<br/>
    /// to ensure documentation renderings appear beneath the actual text content.<br/>
    /// The layer name "RenderDocCommentsLayer" is referenced by the tagger and glyph factory<br/>
    /// to create visual adornments in the editor viewport.
    /// </remarks>
    internal sealed class DocCommentAdornmentLayer
    {
        /// <summary>
        /// Gets the adornment layer definition for Render Doc Comments.<br/>
        /// This property is exported via MEF (Managed Extensibility Framework) to register<br/>
        /// a named adornment layer with the Visual Studio editor infrastructure.
        /// </summary>
        /// <value>
        /// An <see cref="AdornmentLayerDefinition"/> instance that defines the visual layer<br/>
        /// named "RenderDocCommentsLayer" where rendered documentation will be displayed.
        /// </value>
        /// <remarks>
        /// The <see cref="ExportAttribute"/> registers this property with the composition container,<br/>
        /// allowing the Visual Studio editor to discover and use this layer definition.<br/>
        /// The <see cref="OrderAttribute"/> ensures this layer renders before the standard text layer,<br/>
        /// positioning it in the lower z-order of the editor's visual tree.
        /// </remarks>
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("RenderDocCommentsLayer")]
        [Order(Before = PredefinedAdornmentLayers.Text)]
        public AdornmentLayerDefinition EditorAdornmentLayer
        {
            get; set;
        }
    }
}