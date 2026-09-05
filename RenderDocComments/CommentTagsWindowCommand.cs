using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace RenderDocComments
{
    /// <summary>
    /// Command handler for opening the Comment Tags Explorer tool window.
    /// </summary>
    internal sealed class CommentTagsWindowCommand
    {
        public const int CommandId = 0x0102;
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        private readonly AsyncPackage _package;

        private CommentTagsWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static CommentTagsWindowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new CommentTagsWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowToolWindow();
        }

        public static void ShowToolWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var package = Instance?._package;
            if (package == null) return;

            ToolWindowPane window = package.FindToolWindow(typeof(CommentTagsToolWindow), 0, true);
            if (window?.Frame == null)
            {
                throw new NotSupportedException("Cannot create Comment Tags Explorer tool window");
            }

            var windowFrame = (IVsWindowFrame)window.Frame;
            ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }
    }
}
