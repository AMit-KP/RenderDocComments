using System;

namespace RenderDocComments
{
    /// <summary>
    /// Simple static event bus.  Any component that needs to react when the user
    /// saves new settings (e.g. the tagger, to force a full rebuild) subscribes here.
    /// </summary>
    public static class SettingsChangedBroadcast
    {
        /// <summary>Fired after the user saves options or when a theme change is detected.</summary>
        public static event EventHandler SettingsChanged;

        public static void RaiseSettingsChanged()
            => SettingsChanged?.Invoke(null, EventArgs.Empty);
    }
}
