using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;

namespace RenderDocComments
{
    /// <summary>
    /// Registers and handles the "Extensions &gt; Render Doc Options" menu command.<br/>
    /// When invoked, opens the <see cref="RenderDocOptionsWindow"/> dialog for<br/>
    /// configuring extension settings and managing the Premium license.
    /// </summary>
    /// <remarks>
    /// <para>This class follows the standard Visual Studio SDK command pattern:</para>
    /// <list type="bullet">
    /// <item><description>A singleton instance is created during <see cref="InitializeAsync"/>.</description></item>
    /// <item><description>The command is registered with the VS menu service via <see cref="OleMenuCommandService"/>.</description></item>
    /// <item><description>Execution opens the options window as a modal dialog.</description></item>
    /// </list>
    /// <para>The command GUID and ID must match the definitions in the <c>.vsct</c> file<br/>
    /// for the menu item to appear and function correctly.</para>
    /// </remarks>
    internal sealed class RenderDocOptionsCommand
    {
        /// <summary>
        /// The GUID identifying this command set. Must match the GUID in the <c>.vsct</c> file's<br/>
        /// <c>guidRenderDocCommandSet</c> symbol definition.
        /// </summary>
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        /// <summary>
        /// The numeric ID of the Render Doc Options command. Must match the value in the<br/>
        /// <c>.vsct</c> file's <c>cmdidRenderDocOptions</c> symbol definition.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// The VSIX package that owns this command, used for service resolution and disposal tracking.
        /// </summary>
        private readonly AsyncPackage _package;

        /// <summary>
        /// Private constructor that registers the command with the Visual Studio menu service.
        /// </summary>
        /// <param name="package">
        /// The <see cref="AsyncPackage"/> that owns this command.
        /// </param>
        /// <param name="commandService">
        /// The <see cref="OleMenuCommandService"/> used to register the command.
        /// </param>
        /// <remarks>
        /// <para>The constructor creates a <see cref="MenuCommand"/> bound to <see cref="CommandSet"/><br/>
        /// and <see cref="CommandId"/>, wires up the <see cref="Execute"/> handler, and adds it<br/>
        /// to the menu service's command collection.</para>
        /// </remarks>
        private RenderDocOptionsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var id = new CommandID(CommandSet, CommandId);
            var cmd = new MenuCommand(Execute, id);
            commandService.AddCommand(cmd);
        }

        /// <summary>
        /// Gets the singleton instance of this command, set during <see cref="InitializeAsync"/>.
        /// </summary>
        public static RenderDocOptionsCommand Instance
        {
            get; private set;
        }

        /// <summary>
        /// Initializes the command by retrieving the menu service and creating the singleton instance.<br/>
        /// Called once during <see cref="RenderDocCommentsPackage.InitializeAsync"/>.
        /// </summary>
        /// <param name="package">
        /// The <see cref="AsyncPackage"/> that owns this command.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous initialization operation.
        /// </returns>
        /// <remarks>
        /// <para>The method must be called on the UI thread. It:</para>
        /// <list type="number">
        /// <item><description>Switches to the main thread via <see cref="ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync"/>.</description></item>
        /// <item><description>Retrieves the <see cref="IMenuCommandService"/> service from the package.</description></item>
        /// <item><description>Creates the singleton <see cref="RenderDocOptionsCommand"/> instance.</description></item>
        /// </list>
        /// </remarks>
        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
                                 as OleMenuCommandService;
            Instance = new RenderDocOptionsCommand(package, commandService);
        }

        /// <summary>
        /// Executes the command, opening the Render Doc Options window as a modal dialog.
        /// </summary>
        /// <param name="sender">
        /// The command sender (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>This method must run on the UI thread, enforced by <see cref="ThreadHelper.ThrowIfNotOnUIThread"/>.</para>
        /// <para>The options window is created with the package as its service provider,<br/>
        /// enabling the window to access Visual Studio services for settings persistence.</para>
        /// </remarks>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = new RenderDocOptionsWindow(_package);
            window.ShowDialog();
        }
    }
}
