using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Commands;

/// <summary>Registers one <see cref="OleMenuCommand"/> per entry of <see cref="RunicCommandCatalog"/> with its handler.</summary>
internal static class CommandRegistrar
{
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
            command.BeforeQueryStatus += (sender, _) => handler.BeforeQueryStatus((OleMenuCommand)sender);
            menu.AddCommand(command);
        }
    }

    private static void Execute(RunicPackage package, RunicCommandHandler handler)
    {
        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await handler.ExecuteAsync(package, package.DisposalToken); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(package, error.Message, RunicPackage.Title,
                    OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        });
    }
}
