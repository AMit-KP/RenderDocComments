using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace RenderDocComments
{
    /// <summary>
    /// Utility service for navigating to a specific file and line inside Visual Studio editor.
    /// </summary>
    internal static class CommentNavigator
    {
        /// <summary>
        /// Opens the specified file and moves the caret/cursor to the specified line number.
        /// </summary>
        /// <param name="serviceProvider">The Visual Studio service provider.</param>
        /// <param name="filePath">The full path of the file to open.</param>
        /// <param name="lineNumber">The 1-based line number.</param>
        public static void NavigateToLine(IServiceProvider serviceProvider, string filePath, int lineNumber)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return;

            IVsUIHierarchy hierarchy;
            uint itemId;
            IVsWindowFrame windowFrame;

            VsShellUtilities.OpenDocument(
                serviceProvider,
                filePath,
                Guid.Empty,
                out hierarchy,
                out itemId,
                out windowFrame);

            if (windowFrame == null)
                return;

            windowFrame.Show();

            // Get the text view and jump to the line
            var textView = VsShellUtilities.GetTextView(windowFrame);
            if (textView != null)
            {
                int targetLine = Math.Max(0, lineNumber - 1); // 0-based in IVsTextView
                textView.SetCaretPos(targetLine, 0);
                textView.CenterLines(targetLine, 1);
                
                var textSpan = new TextSpan
                {
                    iStartLine = targetLine,
                    iStartIndex = 0,
                    iEndLine = targetLine,
                    iEndIndex = 0
                };
                textView.EnsureSpanVisible(textSpan);
            }
        }
    }
}

