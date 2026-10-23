/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    RegionBannerTaggerProvider.cs
 *  Purpose: MEF-exported factory supplying RegionBannerTagger instances to
 *           Visual Studio text views for the region-capable languages
 *           (C#, VB.NET, C/C++).
 *
 *  Key Classes:
 *    RegionBannerTaggerProvider — IViewTaggerProvider implementation.
 *
 *  Dependencies:
 *    • RegionBannerTagger.cs — the created instance.
 *    • Microsoft.VisualStudio.Utilities (ContentType / TagType / TextViewRole).
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace RenderDocComments.DocCommentRenderer.RegionBanners
{
    /// <summary>
    /// MEF-exported factory that supplies <see cref="RegionBannerTagger"/>
    /// instances to Visual Studio text views for region-capable languages.
    /// </summary>
    /// <remarks>
    /// <para>View-keyed singleton (each IWpfTextView gets its own tagger bound to
    /// its own events and theme/font metrics), identical to the other adornment
    /// taggers' providers. F# has no regions and is not registered.</para>
    /// </remarks>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [ContentType("C/C++")]
    [TagType(typeof(IntraTextAdornmentTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class RegionBannerTaggerProvider : IViewTaggerProvider
    {
        /// <summary>
        /// Gets or sets the editor format map service imported from Visual Studio.
        /// Seeds the view's property bag with the per-view
        /// <see cref="IEditorFormatMap"/> that the tagger reads theme colours
        /// from — mirroring the other providers, since MEF provider creation
        /// order is not guaranteed.
        /// </summary>
        [Import]
        internal IEditorFormatMapService FormatMapService
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the outlining manager service used to detect collapsed
        /// regions (collapsed regions render as a plain label instead of the
        /// banner).
        /// </summary>
        [Import]
        internal IOutliningManagerService OutliningManagerService
        {
            get;
            set;
        }

        /// <summary>
        /// Creates (or reuses) the per-view <see cref="RegionBannerTagger"/>.
        /// </summary>
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer)
            where T : ITag
        {
            if (!(textView is IWpfTextView wpfView)) return null;

            if (FormatMapService != null &&
                !wpfView.Properties.ContainsProperty(typeof(IEditorFormatMap)))
            {
                var map = FormatMapService.GetEditorFormatMap(wpfView);
                wpfView.Properties.AddProperty(typeof(IEditorFormatMap), map);
            }

            return wpfView.Properties.GetOrCreateSingletonProperty(
                typeof(RegionBannerTagger),
                () => new RegionBannerTagger(buffer, wpfView, OutliningManagerService))
                as ITagger<T>;
        }
    }
}
