using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;

namespace RunicArtifex.DevKit.VisualStudio.Commands;

/// <summary>
/// Registers one <see cref="OleMenuCommand"/> per entry of <see cref="RunicCommandCatalog"/> with its handler. A Runic
/// SDK command (<see cref="RunicCommand.SdkOnly"/>) stays hidden, whatever its handler says, until
/// <see cref="RunicSdkState"/> has found a Runic SDK project (<see cref="RunicActivation.ShowsSdkFeatures"/>).
/// </summary>
internal static class CommandRegistrar
{
    private static bool sdkShown;

    public static async Task RegisterAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        var handlers = typeof(RunicCommandHandler).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(RunicCommandHandler).IsAssignableFrom(type))
            .Select(type => (RunicCommandHandler)Activator.CreateInstance(type))
            .ToList();
        foreach (var command in RunicCommandCatalog.All)
        {
            int count = handlers.Count(handler => handler.Id == command.Id);
            if (count != 1) throw new InvalidOperationException($"Command {command.Symbol} has {count} handlers; it needs exactly one.");
        }
        if (handlers.FirstOrDefault(handler => RunicCommandCatalog.All.All(command => command.Id != handler.Id)) is { } unknown)
            throw new InvalidOperationException($"{unknown.GetType().Name} handles 0x{unknown.Id:X4}, which is not in the command table.");

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var menu = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService
            ?? throw new InvalidOperationException("Visual Studio command service is unavailable.");
        var commandSet = new Guid(RunicCommandIds.CommandSet);
        foreach (var handler in handlers)
        {
            var command = new OleMenuCommand((_, _) => Execute(package, handler), new CommandID(commandSet, handler.Id));
            command.BeforeQueryStatus += (sender, _) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var target = (OleMenuCommand)sender;
                handler.BeforeQueryStatus(target);
                var state = RunicSdkState.Instance;
                if (!RunicCommandCatalog.IsVisible(handler.Id, state.Scanned, state.Projects.Count)) target.Visible = target.Enabled = false;
            };
            menu.AddCommand(command);
        }
        RunicSdkState.Instance.Changed += (_, _) => _ = package.JoinableTaskFactory.RunAsync(() => UpdateWhenSdkShownChangesAsync(package));
        await UpdateWhenSdkShownChangesAsync(package);
    }

    /// <summary>The toolbar queries its commands' status only when told to; tell it when the SDK commands appear or go.</summary>
    private static async Task UpdateWhenSdkShownChangesAsync(RunicPackage package)
    {
        var state = RunicSdkState.Instance;
        bool shown = RunicActivation.ShowsSdkFeatures(state.Scanned, state.Projects.Count);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (shown == sdkShown) return;
        sdkShown = shown;
        if (await package.GetServiceAsync(typeof(SVsUIShell)) is IVsUIShell shell) shell.UpdateCommandUI(0);
    }

    private static void Execute(RunicPackage package, RunicCommandHandler handler)
    {
        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await handler.ExecuteAsync(package, package.DisposalToken); }
            catch (OperationCanceledException) { }
            catch (Debugging.HungAttachEndedException error) { SdkOutput.WriteLine(error.Message); }
            catch (DevExitException error)
            {
                SdkOutput.WriteLine($"{RunicPackage.Title}: {error.Message}");
                await DevSession.ToolRestore.ReportAsync(package, error.Project, error.Exit, package.DisposalToken);
            }
            catch (Exception error)
            {
                SdkOutput.WriteLine($"{RunicPackage.Title}: {error.GetType().Name}: {error.Message}");
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsClosing.ShowMessage(package, error.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        });
    }
}
