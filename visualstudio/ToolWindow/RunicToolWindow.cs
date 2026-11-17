using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.ToolWindow;

/// <summary>
/// The Runic tool window: VS Code's Runic activity-bar views as sections (Dev Session, ViewModels, Doctor,
/// Translations) under the toolbar of <c>Commands.vsct</c> (PARITY §2.2).
/// </summary>
[Guid(WindowGuid)]
public sealed class RunicToolWindow : ToolWindowPane
{
    public const string WindowGuid = "e2eee322-91ba-482d-a465-6ce89ec51dda";

    public RunicToolWindow() : base(null)
    {
        Caption = ToolWindowSections.Caption;
        ToolBar = new CommandID(new Guid(RunicCommandIds.CommandSet), RunicCommandIds.ToolWindowToolbar);
        Content = new RunicToolWindowControl();
    }
}
