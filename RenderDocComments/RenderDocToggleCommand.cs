using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace RenderDocComments
{
    /// <summary>
    /// Registers and handles the toolbar toggle command that enables/disables documentation<br/>
    /// comment rendering for the current file only.
    /// </summary>
    internal sealed class RenderDocToggleCommand
    {
        /// <summary>
        /// The GUID identifying this command set. Must match the GUID in the .vsct file.
        /// </summary>
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        /// <summary>
        /// The numeric ID of the toggle command. Must match the value in the .vsct file.
        /// </summary>
        public const int CommandId = 0x0101;

        /// <summary>
        /// The property key used to store the disabled state in the ITextBuffer properties.
        /// </summary>
        private const string DisabledProperty = "RenderDocComments_Disabled";

        private readonly AsyncPackage _package;

        /// <summary>
        /// Initializes a new instance of the <see cref="RenderDocToggleCommand"/> class.
        /// </summary>
        private RenderDocToggleCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var id = new CommandID(CommandSet, CommandId);
            var cmd = new OleMenuCommand(Execute, id);
            cmd.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(cmd);
        }

        /// <summary>
        /// Gets the singleton instance of this command.
        /// </summary>
        public static RenderDocToggleCommand Instance { get; private set; }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new RenderDocToggleCommand(package, commandService);
        }

        /// <summary>
        /// Updates the button's checked state and visibility before it is displayed.
        /// </summary>
        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cmd = (OleMenuCommand)sender;
            
            // Ensure the command is supported and visible
            cmd.Supported = true;
            cmd.Visible = true;
            
            var buffer = GetActiveBuffer();
            if (buffer == null)
            {
                cmd.Enabled = false;
                cmd.Checked = false;
                return;
            }

            cmd.Enabled = true;

            // Checked if NOT disabled for this buffer
            bool disabled = buffer.Properties.TryGetProperty(DisabledProperty, out bool val) && val;
            cmd.Checked = !disabled;
        }

        /// <summary>
        /// Toggles the rendering state for the current buffer.
        /// </summary>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var buffer = GetActiveBuffer();
            if (buffer == null) return;

            bool disabled = buffer.Properties.TryGetProperty(DisabledProperty, out bool val) && val;
            if (disabled)
                buffer.Properties.RemoveProperty(DisabledProperty);
            else
                buffer.Properties.AddProperty(DisabledProperty, true);

            // Broadcast change to refresh taggers
            SettingsChangedBroadcast.RaiseSettingsChanged();
        }

        /// <summary>
        /// Retrieves the ITextBuffer for the currently active text view.
        /// </summary>
        private ITextBuffer GetActiveBuffer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var serviceProvider = (IServiceProvider)_package;
            var textManager = serviceProvider.GetService(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null) return null;

            textManager.GetActiveView(1, null, out IVsTextView textView);
            if (textView == null) return null;

            var componentModel = serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            if (componentModel == null) return null;

            var editorAdapter = componentModel.DefaultExportProvider.GetExportedValue<IVsEditorAdaptersFactoryService>();
            var wpfTextView = editorAdapter.GetWpfTextView(textView);
            return wpfTextView?.TextBuffer;
        }
    }
}
