using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using RunicArtifex.DevKit.VisualStudio.Core;
using RunicArtifex.DevKit.VisualStudio.Output;
using RunicArtifex.DevKit.VisualStudio.Settings;
using StreamJsonRpc;

namespace RunicArtifex.DevKit.VisualStudio;

internal static class ContentTypes
{
    [Export, Name("rmf2"), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition Definition = null!;
    [Export, FileExtension(".rmf2"), ContentType("rmf2")]
    internal static FileExtensionToContentTypeDefinition GroupedExtension = null!;
    [Export, FileExtension(".mf2"), ContentType("rmf2")]
    internal static FileExtensionToContentTypeDefinition DirectExtension = null!;
}

[Export(typeof(ILanguageClient)), Export(typeof(RunicLanguageClient)), ContentType("rmf2")]
public sealed class RunicLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
{
    private Process? process;
    private JsonRpc? rpc;
    public string Name => "Runic MF2";
    public IEnumerable<string> ConfigurationSections => Array.Empty<string>();
    // runic.json buffers are synchronized by ConfigurationBuffers, so the server accepts
    // configuration-changing refactors from this client. runicProtocol 2 opts in to the language server protocol of
    // Translations 0.14 (F2 for inputs and slots, the Extract Group and Inline Resource code actions, rename
    // confirmation through window/showMessageRequest, the server's own file watcher); Translations 0.13 ignores it.
    public object? InitializationOptions => new { runicConfigurationSync = true, runicProtocol = 2 };
    internal ConfigurationBuffers Configuration { get; } = new();
    public IEnumerable<string> FilesToWatch => new[] { "**/*.mf2", "**/*.rmf2", "**/runic.json" };
    // The extension shows its own start-failure InfoBar, once per solution, with install help (ServerStartInfoBar).
    public bool ShowNotificationOnInitializeFailed => false;
    // Go to Definition into a library catalog opens a read-only copy of its runic-library: source (Translations/LibraryDocuments.cs),
    // the file Inline Resource empties is deleted once it is saved (Translations/InlineCleanup.cs), and a rename the
    // server refuses says why (Translations/RenameRefusals.cs), and a refactoring that creates a file in a missing folder
    // gets the folder first (Translations/NewFolders.cs; last, so the other layers see the edit it lets through).
    public object? MiddleLayer => middleLayer ??= CreateMiddleLayer();
    private Translations.MiddleLayers? middleLayer;
    private Translations.MiddleLayers CreateMiddleLayer()
    {
        var refusals = new Translations.RefusalReport();
        return new Translations.MiddleLayers(new Translations.LibraryDocuments(RequestAsync), new Translations.InlineCleanup(),
            new Translations.RenameRefusals(refusals), new Translations.NewFolders(() => solutionFolder, refusals));
    }
    // The open solution's folder at the server's start, a root inside which NewFolders may create folders.
    private volatile string? solutionFolder;
    public object? CustomMessageTarget => null;
    public event Microsoft.VisualStudio.Threading.AsyncEventHandler<EventArgs>? StartAsync;
    public event Microsoft.VisualStudio.Threading.AsyncEventHandler<EventArgs>? StopAsync;
    private IVsOutputWindowPane? output;
    private ServerStartAttempt attempt = new();
    private string solutionKey = "";
    // The server's last standard-error lines, which say why a start failed.
    private readonly Queue<string> stderrTail = new();
    private const int StderrTailLines = 20;

    public async Task<Connection?> ActivateAsync(CancellationToken token)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
        var options = await RunicOptionsPage.GetAsync(token);
        var pane = output = await RunicOutput.TranslationsAsync(token);
        var dte = (EnvDTE.DTE)ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE));
        string? solution = dte?.Solution?.FullName;
        string? solutionDirectory = string.IsNullOrEmpty(solution) ? null : Path.GetDirectoryName(solution);
        solutionFolder = solutionDirectory;
        string? file = dte?.ActiveDocument?.FullName;
        if (string.IsNullOrEmpty(file)) file = solution;
        solutionKey = solution ?? file ?? "";
        attempt = new ServerStartAttempt { Dotnet = RunicSettings.Dotnet(options.DotnetPath, Environment.GetEnvironmentVariable(RunicSettings.DotnetVariable)) };
        lock (stderrTail) stderrTail.Clear();
        DisposeProcess();
        try
        {
            if (string.IsNullOrEmpty(file)) throw new InvalidOperationException("Open an MF2 document in the solution or folder containing the local Runic tool manifest.");
            var assembly = RunicSettings.ServerAssembly(options.ServerAssembly, Environment.GetEnvironmentVariable(RunicSettings.ServerAssemblyVariable), solutionDirectory);
            var launch = ServerLaunch.Find(Path.GetDirectoryName(file)!, assembly);
            attempt.Directory = launch.Directory;
            if (launch.UsesToolManifest) await ServerTrustGate.RequireAsync(launch.Directory, solutionDirectory, token);
            pane.OutputStringThreadSafe($"Starting {attempt.Dotnet} {launch.Arguments} in {launch.Directory}{Environment.NewLine}");
            var started = process = new Process { StartInfo = new ProcessStartInfo {
                FileName = attempt.Dotnet, Arguments = launch.Arguments,
                WorkingDirectory = launch.Directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            } };
            started.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                lock (stderrTail) { stderrTail.Enqueue(args.Data); while (stderrTail.Count > StderrTailLines) stderrTail.Dequeue(); }
                pane.OutputStringThreadSafe(args.Data + Environment.NewLine);
            };
            try { if (!started.Start()) throw new InvalidOperationException("Could not start the project-local Runic language server."); }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { attempt.ExecutableMissing = true; throw; }
            started.BeginErrorReadLine();
            var tracer = new LspTracer(() => options.Trace, line => pane.OutputStringThreadSafe(line + Environment.NewLine));
            return new Connection(new TracingStream(started.StandardOutput.BaseStream, tracer, LspDirection.Received),
                new TracingStream(started.StandardInput.BaseStream, tracer, LspDirection.Sending));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            attempt.Error = error.Message;
            if (error is ServerLaunchException launchError) attempt.Known = launchError.Problem;
            ReportStartFailure();
            throw;
        }
    }

    /// <summary>The server's failure, in the Output pane and once per solution in an InfoBar.</summary>
    private void ReportStartFailure()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        lock (stderrTail) attempt.Stderr = stderrTail.ToArray();
        string message = ServerStartFailure.Message(attempt);
        output?.OutputStringThreadSafe(message + Environment.NewLine);
        ServerStartInfoBar.Show(solutionKey, message, output);
    }

    public async Task OnLoadedAsync() { if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty); }
    // The configuration buffers open once both the custom-message connection and the
    // initialize handshake exist, in whichever order Visual Studio reports them.
    private bool initialized;
    public Task OnServerInitializedAsync() { initialized = true; if (rpc != null) Configuration.Attach(rpc); return Task.CompletedTask; }
    public async Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
    {
        // A server that exited during initialize has written why to standard error; read it to the end.
        var exited = process;
        if (exited != null) await Task.Run(() => { try { if (exited.HasExited) exited.WaitForExit(); } catch (InvalidOperationException) { } });
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (attempt.Error == null && attempt.Known == null)
        {
            attempt.Error = initializationState.InitializationException?.Message ?? initializationState.StatusMessage;
            ReportStartFailure();
        }
        return new InitializationFailureContext { FailureMessage = ServerStartFailure.Message(attempt) };
    }
    public Task AttachForCustomMessageAsync(JsonRpc jsonRpc) { rpc = jsonRpc; if (initialized) Configuration.Attach(rpc); return Task.CompletedTask; }
    /// <summary>True while the server is connected for custom requests.</summary>
    internal bool IsRunning => rpc != null;
    internal Task<JObject> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
        => (rpc ?? throw new InvalidOperationException("The Runic language server is not ready.")).InvokeWithParameterObjectAsync<JObject>(method, parameters, cancellationToken);
    internal async Task RestartAsync()
    {
        ServerTrustGate.ForgetDeclined(); // Restart Language Server asks again
        if (StopAsync != null) await StopAsync.InvokeAsync(this, EventArgs.Empty);
        DisposeProcess();
        if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty);
    }
    public void Dispose() => DisposeProcess();
    private void DisposeProcess()
    {
        rpc = null; initialized = false;
        Configuration.Attach(null);
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        process.Dispose(); process = null;
    }
}

internal sealed class ServerLaunch
{
    internal string Directory { get; }
    internal string Arguments { get; }
    /// <summary>Whether it runs the folder's tool manifest (<c>dotnet tool run</c>), which the trust gate covers; not a configured server DLL.</summary>
    internal bool UsesToolManifest { get; }
    private ServerLaunch(string directory, string arguments, bool usesToolManifest) { Directory = directory; Arguments = arguments; UsesToolManifest = usesToolManifest; }
    internal static ServerLaunch Find(string directory, ServerAssemblySetting? assembly)
    {
        if (assembly != null)
        {
            if (!Path.IsPathRooted(assembly.Path) || !File.Exists(assembly.Path))
                throw new ServerLaunchException(ServerStartProblem.AssemblyMissing, $"{assembly.Source} must identify an existing absolute tool DLL; {assembly.Path} is not one.");
            return new ServerLaunch(directory, Quote(assembly.Path) + " lsp", usesToolManifest: false);
        }
        for (string? current = directory; current != null; current = Path.GetDirectoryName(current))
        {
            string manifest = Path.Combine(current, ".config", "dotnet-tools.json");
            if (!File.Exists(manifest)) continue;
            var json = JObject.Parse(File.ReadAllText(manifest));
            foreach (var tool in (json["tools"] as JObject ?? new JObject()).Properties())
                if (tool.Value["commands"] is JArray commands && commands.ToObject<string[]>() is string[] names && Array.IndexOf(names, "runic-translations") >= 0)
                    return new ServerLaunch(current, "tool run runic-translations -- lsp", usesToolManifest: true);
            if (json.Value<bool?>("isRoot") == true) break;
        }
        throw new ServerLaunchException(ServerStartProblem.ToolMissing, "No project-local runic-translations tool manifest found. Restore the local tool before opening MF2 resources.");
    }
    // Windows CommandLineToArgvW escaping; ProcessStartInfo on .NET Framework has no ArgumentList.
    internal static string Quote(string value)
    {
        var result = new System.Text.StringBuilder("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

/// <summary>A start failure the extension finds before it starts a process.</summary>
internal sealed class ServerLaunchException : InvalidOperationException
{
    internal ServerLaunchException(ServerStartProblem problem, string message) : base(message) { Problem = problem; }
    internal ServerStartProblem Problem { get; }
}

/// <summary>
/// The language server's trust gate in Visual Studio (<see cref="ServerTrust"/>, DEV-SEC-86): a tool manifest outside the
/// open solution's folder starts the server only after the user says Yes, which is remembered per folder in Visual
/// Studio's user settings; a No holds until Restart Language Server.
/// </summary>
internal static class ServerTrustGate
{
    private static readonly HashSet<string> declined = new(StringComparer.OrdinalIgnoreCase);

    public static void ForgetDeclined() { lock (declined) declined.Clear(); }

    public static async Task RequireAsync(string manifestFolder, string? solutionFolder, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string key = ServerTrust.Key(manifestFolder);
        ServerTrustDecision decision;
        lock (declined) decision = ServerTrust.Decide(manifestFolder, solutionFolder, IsTrusted, declined);
        if (decision == ServerTrustDecision.Ask)
        {
            int answer = VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, ServerTrust.Question(key, solutionFolder), "Runic DevKit",
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            if (answer == 6 /* IDYES */) { Trust(key); return; }
            lock (declined) declined.Add(key);
            decision = ServerTrustDecision.Declined;
        }
        if (decision == ServerTrustDecision.Declined)
            throw new ServerLaunchException(ServerStartProblem.NotTrusted, ServerTrust.DeclinedMessage(key));
    }

    private static bool IsTrusted(string key)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return ServerTrust.ParseFolders(Read()).Contains(key, StringComparer.OrdinalIgnoreCase); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { return false; }
    }

    private static void Trust(string key)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var store = Store();
            ErrorHandler.ThrowOnFailure(store.CollectionExists(ServerTrust.Collection, out int exists));
            if (exists == 0) ErrorHandler.ThrowOnFailure(store.CreateCollection(ServerTrust.Collection));
            ErrorHandler.ThrowOnFailure(store.SetString(ServerTrust.Collection, ServerTrust.Property, ServerTrust.AddFolder(Read(), key)));
        }
        // Not remembered: the next start asks again.
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }

    private static string? Read()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var store = Store();
        ErrorHandler.ThrowOnFailure(store.CollectionExists(ServerTrust.Collection, out int exists));
        if (exists == 0) return null;
        ErrorHandler.ThrowOnFailure(store.GetStringOrDefault(ServerTrust.Collection, ServerTrust.Property, "", out string value));
        return value;
    }

    private static IVsWritableSettingsStore Store()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsSettingsManager)) is not IVsSettingsManager manager) throw new InvalidOperationException("Visual Studio's settings manager is unavailable.");
        ErrorHandler.ThrowOnFailure(manager.GetWritableSettingsStore((uint)__VsSettingsScope.SettingsScope_UserSettings, out var store));
        return store;
    }
}
