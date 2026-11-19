using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RunicArtifex.DevKit.VisualStudio.Commands;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.DevSession;
using RunicArtifex.DevKit.VisualStudio.ToolWindow;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;

namespace RunicArtifex.DevKit.VisualStudio.Debugging;

/// <summary>
/// Debug the Window (VS Code: <c>runic.dev.debugWindow</c>): the .NET code and the page in one debug session
/// (SPIKE-VS-0 §6, D070.19). It makes sure <c>dotnet runic dev --no-window</c> runs and is ready, builds the project,
/// starts the program <c>dotnet run</c> would start under the CoreCLR debugger with the session file's
/// <c>window.environment</c> and a DevTools Protocol port (<see cref="WindowDebug.Plan"/>), and once that endpoint
/// answers attaches the JavaScript engine over the CDP transport (<see cref="PageAttach"/>). Unlike the
/// <c>jsWebView2Debugging</c> profile there is no 30-second limit and the environment is not lost.
/// </summary>
internal sealed class DebugWindowCommand : RunicCommandHandler
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The running run, if any. A hung <c>Attach2</c> or a solution reload releases it, so the command does not stay
    /// disabled until Visual Studio restarts (runic-editors#23).
    /// </summary>
    private static readonly SingleRun Run = new();

    public DebugWindowCommand()
    {
        // A solution closed and opened again (as after deleting .vs) starts over: a run still waiting ends.
        SolutionEvents.OnAfterCloseSolution += (_, _) => ResetAfter("the solution closed");
        SolutionEvents.OnAfterOpenSolution += (_, _) => ResetAfter("the solution opened");
    }

    public override int Id => RunicCommandIds.DebugWindow;

    public override void BeforeQueryStatus(OleMenuCommand command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        command.Visible = true;
        command.Enabled = RunicSdkState.Instance.Projects.Count > 0 && !Run.IsRunning;
    }

    public override async Task ExecuteAsync(RunicPackage package, CancellationToken cancellationToken)
    {
        if (Run.TryStart(cancellationToken) is not { } ticket) return;
        try { await RunAsync(ticket.Token, () => { if (Run.Release(ticket)) UpdateCommands(); }); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !ticket.IsCurrent) { }
        finally
        {
            if (Run.Release(ticket)) UpdateCommands();
            await PageAttach.SetStatusAsync("");
        }
    }

    private static void ResetAfter(string reason)
    {
        if (!Run.Reset()) return;
        SdkOutput.WriteLine($"Debug the Window: {reason}; stopped the run that was still waiting.");
        UpdateCommands();
    }

    /// <summary>Re-queries the command state now (toolbar buttons otherwise wait for the next idle query).</summary>
    private static void UpdateCommands() => _ = RunicSdkState.Background.RunAsync(async () =>
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await PageAttach.SetStatusAsync("");
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsUIShell)) is IVsUIShell shell) shell.UpdateCommandUI(0);
    });

    private static async Task RunAsync(CancellationToken cancellationToken, Action attachHung)
    {
        var service = DevSessionService.Instance;
        string project = await service.PickAsync(null, cancellationToken);
        string name = Path.GetFileName(project);

        // 1. dotnet runic dev --no-window, until it is ready for the IDE (owner ide).
        var snapshot = service.Refresh(project);
        if (!snapshot.IsReadyForIde)
        {
            await PageAttach.SetStatusAsync($"Runic: starting dotnet runic dev --no-window for {name}…");
            await service.StartAsync(project, noWindow: true, StartDevCommand.ConfirmAsync, cancellationToken);
            var started = service.Refresh(project);
            if (started.IsActive && started.Session?.Window?.OwnedByIde != true) return; // the restart was declined
            snapshot = await service.WaitForAsync(project, current => current.IsReadyForIde, ReadyTimeout,
                current => service.StartedHere(project) || current.IsActive ? null : "dotnet runic dev stopped; see the Runic SDK Output pane and the Error List.",
                cancellationToken);
        }
        var session = snapshot.Session!;

        // 2. The build, as F5's.
        await PageAttach.SetStatusAsync($"Runic: building {name}…");
        string configuration = await BuildAsync(project, cancellationToken);

        // 3. The program dotnet run would start, with the session's environment and the page's port.
        await PageAttach.SetStatusAsync($"Runic: starting {name} under the debugger…");
        string dotnet = await RunicSdkState.DotnetAsync(cancellationToken);
        await TaskScheduler.Default;
        var arguments = WindowDebug.RunPropertiesArguments(project, configuration);
        var result = await ToolProcess.RunAsync(dotnet, arguments, Path.GetDirectoryName(project) ?? Environment.CurrentDirectory, TimeSpan.FromMinutes(2), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"dotnet msbuild could not read the run properties of {name}: {result.Detail}");
        var program = WindowDebug.Program(project, WindowDebug.ParseRunProperties(result.Output));
        var plan = WindowDebug.Plan(CurrentEnvironment(), session.Window?.Environment, WindowDebug.FreePort);
        SdkOutput.WriteLine($"Debugging {program.Program} {program.CommandLineArguments} in {program.WorkingDirectory} with {string.Join(" ", plan.Added.Select(pair => pair.Key + "=" + pair.Value))}");
        if (plan.Host == PageHost.WebView2 && PageAttach.IsElevated())
            SdkOutput.WriteLine("Visual Studio runs as administrator: WebView2 ignores WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS in an elevated Window, so the page's debugger cannot attach.");
        // A page breakpoint with "Break all processes" on would also stop the Window, and WebView2 with it (checklist row 10).
        string engine = plan.Host == PageHost.CsWebUi ? "chromium" : "webview2";
        await BreakAllProcesses.SuspendForAsync(engine, "Debug the Window");
        int processId;
        try { processId = await LaunchAsync(program, plan, cancellationToken); }
        catch { await BreakAllProcesses.RestoreIfNotDebuggingAsync(); throw; }
        var endpoint = new PageEndpoint(engine, plan.EndpointUrl, "Debug the Window");
        PageAttach.Remember(project, endpoint, processId);

        // 4. The page: attach the JavaScript engine once the endpoint answers and lists the page.
        await PageAttach.SetStatusAsync($"Runic: waiting for the page's DevTools endpoint {plan.EndpointUrl}…");
        try
        {
            var pages = await PageAttach.WaitForPageAsync(plan.EndpointUrl, processId, () => PageAttach.IsRunning(processId), PageAttach.SetStatusAsync, cancellationToken, attachHung);
            SdkOutput.WriteLine($"Attached the JavaScript debugger to {string.Join(", ", pages)} ({plan.EndpointUrl}).");
            if (session.Frontend?.DevelopmentServer != null)
                SdkOutput.WriteLine("TSX and TS breakpoints bind through the development server's source maps with absolute sources, which runic-sdk 0.22's vite-plugin-runic writes.");
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException)
        {
            // The .NET debugger keeps running; only the page part failed.
            SdkOutput.WriteLine("Debug the Window: " + error.Message);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, error.Message, RunicPackage.Title,
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    private static IReadOnlyDictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
            if (pair.Key is string key && pair.Value is string value) result[key] = value;
        return result;
    }

    // ---- Build --------------------------------------------------------------------------------------------------

    /// <summary>Builds the project in the active solution configuration, as F5 would; returns the project's configuration name.</summary>
    private static async Task<string> BuildAsync(string project, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) throw new InvalidOperationException("Visual Studio automation (DTE) is unavailable.");
        var item = Projects(dte.Solution.Projects).FirstOrDefault(candidate => string.Equals(FullName(candidate), project, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{Path.GetFileName(project)} is not loaded in this solution.");
        string configuration = item.ConfigurationManager?.ActiveConfiguration?.ConfigurationName ?? "Debug";
        var build = dte.Solution.SolutionBuild;
        if (build.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress) throw new InvalidOperationException("A build is running; start Debug the Window again when it has finished.");
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = dte.Events.BuildEvents; // keep the RCW alive while waiting
        void OnDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action) => done.TrySetResult(true);
        events.OnBuildDone += OnDone;
        try
        {
            var active = (EnvDTE80.SolutionConfiguration2)build.ActiveConfiguration;
            try { build.BuildProject(active.Name + "|" + active.PlatformName, item.UniqueName, false); }
            catch (ArgumentException) { build.BuildProject(active.Name, item.UniqueName, false); }
            using (cancellationToken.Register(() => done.TrySetCanceled()))
                await done.Task;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (build.LastBuildInfo != 0) throw new InvalidOperationException($"The build of {Path.GetFileName(project)} failed; see the Error List.");
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            events.OnBuildDone -= OnDone;
            GC.KeepAlive(events);
        }
        return configuration;
    }

    private static string? FullName(EnvDTE.Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return project.FullName; } catch (Exception error) when (error is COMException or NotImplementedException or ArgumentException) { return null; }
    }

    /// <summary>The solution's projects, inside solution folders too.</summary>
    private static IEnumerable<EnvDTE.Project> Projects(EnvDTE.Projects projects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var pending = new Stack<EnvDTE.Project>(projects.OfType<EnvDTE.Project>());
        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (project.Kind == EnvDTE80.ProjectKinds.vsProjectKindSolutionFolder)
            {
                if (project.ProjectItems == null) continue;
                foreach (EnvDTE.ProjectItem child in project.ProjectItems)
                    if (child.SubProject is EnvDTE.Project sub) pending.Push(sub);
                continue;
            }
            yield return project;
        }
    }

    // ---- Launch ---------------------------------------------------------------------------------------------------

    /// <summary>Starts the program under the CoreCLR debugger (<c>IVsDebugger4.LaunchDebugTargets4</c>); returns its process ID.</summary>
    private static async Task<int> LaunchAsync(WindowProgram program, WindowDebugPlan plan, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsShellDebugger)) is not IVsDebugger4 debugger)
            throw new InvalidOperationException("The Visual Studio debugger is unavailable.");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var target = new VsDebugTargetInfo4
        {
            dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
            bstrExe = program.Program,
            bstrArg = program.CommandLineArguments,
            bstrCurDir = program.WorkingDirectory,
            bstrEnv = WindowDebug.EnvironmentBlock(plan.Environment),
            guidLaunchDebugEngine = new Guid(DebugEngines.CoreClr),
            fSendToOutputWindow = true,
        };
        var results = new VsDebugTargetProcessInfo[1];
        debugger.LaunchDebugTargets4(1, new[] { target }, results);
        return (int)results[0].dwProcessId;
    }
}
