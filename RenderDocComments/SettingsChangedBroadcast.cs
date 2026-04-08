using System;

namespace RenderDocComments
{
    /// <summary>
    /// Static event bus that broadcasts settings change notifications across the extension.<br/>
    /// Components that need to react when the user saves new settings (e.g., forcing a full tagger rebuild)<br/>
    /// or when a Visual Studio theme change is detected subscribe to this event.
    /// </summary>
    /// <remarks>
    /// <para>This class serves as a centralized communication channel between loosely-coupled components:</para>
    /// <list type="bullet">
    /// <item><description><b>Publishers:</b> The options dialog (<see cref="RenderDocOptionsWindow"/>) and the package's theme change handler (<see cref="RenderDocCommentsPackage.OnThemeChanged"/>) invoke <see cref="RaiseSettingsChanged"/>.</description></item>
    /// <item><description><b>Subscribers:</b> The adornment tagger (<see cref="DocCommentRenderer.DocCommentAdornmentTagger"/>) and the glyph tagger (<see cref="DocCommentRenderer.DocCommentGlyphTagger"/>) subscribe to rebuild their cached tags with updated settings.</description></item>
    /// </list>
    /// <para>The event is fired with <c>null</c> sender and <see cref="EventArgs.Empty"/> to minimize allocation overhead.</para>
    /// </remarks>
    public static class SettingsChangedBroadcast
    {
        /// <summary>
        /// Event raised after the user saves options in the settings dialog or when a Visual Studio<br/>
        /// theme change is detected and auto-refresh is enabled.
        /// </summary>
        /// <remarks>
        /// <para>Subscribers should handle this event by:</para>
        /// <list type="number">
        /// <item><description>Invalidating any cached rendering state (e.g., cached adornment tags).</description></item>
        /// <item><description>Triggering a full rebuild of documentation comment tags with the new settings.</description></item>
        /// <item><description>Optionally raising <see cref="Microsoft.VisualStudio.Text.Tagging.SnapshotSpanEventArgs"/> on their own <see cref="Microsoft.VisualStudio.Text.Tagging.ITagger{T}.TagsChanged"/> event to notify the editor.</description></item>
        /// </list>
        /// <para>The event is raised on the caller's thread. If the subscriber needs to interact with WPF UI elements,<br/>
        /// it should marshal the call to the UI thread via the dispatcher (e.g., <see cref="System.Windows.Threading.Dispatcher.BeginInvoke"/>).</para>
        /// </remarks>
        public static event EventHandler SettingsChanged;

        /// <summary>
        /// Raises the <see cref="SettingsChanged"/> event, notifying all subscribers that settings<br/>
        /// have been updated and cached state should be invalidated.
        /// </summary>
        /// <remarks>
        /// <para>This method is called in the following scenarios:</para>
        /// <list type="bullet">
        /// <item><description>The user clicks "OK" or "Apply" in the Render Doc Options dialog.</description></item>
        /// <item><description>A Visual Studio theme change occurs and <see cref="RenderDocOptions.EffectiveAutoRefresh"/> is enabled.</description></item>
        /// <item><description>A glyph toggle button is clicked (via <see cref="DocCommentRenderer.DocCommentGlyphFactory.GenerateGlyph"/>).</description></item>
        /// </list>
        /// <para>The event is invoked with <c>null</c> as the sender and <see cref="EventArgs.Empty"/> as the arguments.<br/>
        /// If no subscribers are registered, the null-conditional operator (<c>?.</c>) ensures no exception is thrown.</para>
        /// </remarks>
        public static void RaiseSettingsChanged()
            => SettingsChanged?.Invoke(null, EventArgs.Empty);
    }
}
