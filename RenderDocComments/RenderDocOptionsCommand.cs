using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;

namespace RenderDocComments
{
    /// <summary>
    /// Registers and handles the "Extensions > RenderDocOptions" menu command.
    ///
    /// SETUP REQUIRED IN .vsct
    /// ────────────────────────
    /// Add the following inside your &lt;Commands&gt; / &lt;Buttons&gt; block:
    ///
    ///   &lt;Button guid="guidRenderDocCommandSet" id="cmdidRenderDocOptions" priority="0x0100"
    ///           type="Button"&gt;
    ///     &lt;Parent guid="guidSHLMainMenu" id="IDG_VS_MM_TOOLSADDINS"/&gt;
    ///     &lt;Strings&gt;
    ///       &lt;ButtonText&gt;RenderDocOptions&lt;/ButtonText&gt;
    ///     &lt;/Strings&gt;
    ///   &lt;/Button&gt;
    ///
    /// And in &lt;Symbols&gt;:
    ///
    ///   &lt;GuidSymbol name="guidRenderDocCommandSet"
    ///              value="{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"&gt;
    ///     &lt;IDSymbol name="cmdidRenderDocOptions" value="0x0100"/&gt;
    ///   &lt;/GuidSymbol&gt;
    ///
    /// The GUID below must match the one in the .vsct file.
    /// </summary>
    internal sealed class RenderDocOptionsCommand
    {
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
        public const int CommandId = 0x0100;

        private readonly AsyncPackage _package;

        private RenderDocOptionsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var id = new CommandID(CommandSet, CommandId);
            var cmd = new MenuCommand(Execute, id);
            commandService.AddCommand(cmd);
        }

        public static RenderDocOptionsCommand Instance { get; private set; }

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
                                 as OleMenuCommandService;
            Instance = new RenderDocOptionsCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = new RenderDocOptionsWindow(_package);
            window.ShowDialog();
        }
    }
}
