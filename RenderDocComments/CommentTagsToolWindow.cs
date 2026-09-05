using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace RenderDocComments
{
    /// <summary>
    /// This class implements the tool window for Comment Tags Explorer.
    /// In Visual Studio, tool windows are composed of a frame (managed by the Shell)
    /// and the actual content (hosted WPF UserControl).
    /// </summary>
    [Guid(WindowGuidString)]
    public class CommentTagsToolWindow : ToolWindowPane
    {
        public const string WindowGuidString = "8a8c7f08-542b-4a73-a446-24e491a28a81";

        public CommentTagsToolWindow() : base(null)
        {
            Caption = "Comment Tags Explorer";
            Content = new CommentTagsToolWindowControl();
            //FIXME
            //WARN a
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Content is IDisposable disposable)
            {
                disposable.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

