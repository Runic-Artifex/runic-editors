import * as vscode from "vscode";
import { execFile, type ChildProcess } from "node:child_process";
import { readFileSync } from "node:fs";
import { basename, dirname, isAbsolute, relative, resolve } from "node:path";
import { detectToolVersion, devArguments, forceStop, onDevEnd, spawnDev, stopDevProcess, supportsDevSdk022, terminalText, type DevStop } from "./dev-process.js";
import { DevProblems, LineSplitter } from "./dev-problems.js";
import { DevSessionFollower, devSessionPath, devStateLabel, fileSystemSource, isActive, type DevSnapshot } from "./dev-session.js";
import { codeValue, diagnosticHelpUri, doctorHelpUri, isRunicCode } from "./diagnostics.js";
import { doctorArguments, doctorSummary, orderedChecks, parseDoctor, type DoctorCheck, type DoctorReport } from "./doctor.js";
import { locationText, newestSourceMap, parseSourceMap, sourceMapTree, type SourceLocation, type SourceTreeNode } from "./source-map.js";
import { coverageLine, errorSummary, type StatusResult } from "./status.js";
import { parseRunProperties, runPropertiesArguments, templateTaskLabels, templateWindowConfiguration, windowLaunchConfiguration } from "./window-debug.js";
import { detectWorkspace, projectRunicToolVersion, type WorkspaceKind } from "./workspace.js";

/** What the Runic SDK features need from the rest of the extension. */
export interface SdkHost {
  dotnetPath(folder: vscode.WorkspaceFolder): string;
  /** `runic-translations status` of a runic.json project, through the workspace's local tool. */
  translationsStatus(folder: vscode.WorkspaceFolder, project: string): Promise<StatusResult | undefined>;
}

interface ProjectState {
  project: string; folder: vscode.WorkspaceFolder; follower: DevSessionFollower;
  sourceMapPath?: string; sourceTree?: SourceTreeNode[]; sourceMapError?: string;
  doctor?: DoctorReport; doctorError?: string; doctorRunning?: boolean;
  execution?: vscode.TaskExecution;
  /** The `dev` of a task this extension runs with its own terminal (dotnet-runic 0.22 and later), which it stops on its input. */
  devTerminal?: DevTerminal;
  /** The Problems of that terminal's `dev`, kept after it ends until the next `dev` of the project starts. */
  devProblems?: DevProblems;
}

interface RunicTaskDefinition extends vscode.TaskDefinition { type: "runic"; command: "dev" | "doctor" | "build"; noWindow?: boolean; project?: string }

const pollInterval = 2000;
const doctorSource = "runic doctor";

/** A node of the Runic views; `project` names the Runic SDK project it belongs to. */
class Node extends vscode.TreeItem {
  children?: () => Node[];
  constructor(label: string | vscode.TreeItemLabel, readonly project?: string, collapsible = vscode.TreeItemCollapsibleState.None) { super(label, collapsible); }
}

function node(label: string, options: { project?: string; description?: string; tooltip?: string | vscode.MarkdownString; icon?: string | vscode.ThemeIcon; command?: vscode.Command; contextValue?: string; children?: () => Node[]; expanded?: boolean; id?: string } = {}): Node {
  const item = new Node(label, options.project, options.children ? (options.expanded === false ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.Expanded) : vscode.TreeItemCollapsibleState.None);
  item.description = options.description; item.tooltip = options.tooltip; item.command = options.command; item.contextValue = options.contextValue; item.children = options.children;
  if (options.id) item.id = options.id;
  if (options.icon) item.iconPath = typeof options.icon === "string" ? new vscode.ThemeIcon(options.icon) : options.icon;
  return item;
}

class Provider implements vscode.TreeDataProvider<Node> {
  private readonly emitter = new vscode.EventEmitter<Node | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;
  constructor(private readonly roots: () => Node[] | Promise<Node[]>) {}
  refresh() { this.emitter.fire(undefined); }
  getTreeItem(element: Node) { return element; }
  getChildren(element?: Node) { return element ? element.children?.() ?? [] : this.roots(); }
}

/**
 * The Runic SDK features: the `dotnet runic dev` session (start, stop, its session file, tasks with problem matchers),
 * F5 of the Window with `dev --no-window`, `dotnet runic doctor`, Runic diagnostics' code actions, the ViewModels tree
 * of the development source map, and the Translations status view. The SDK views show only in workspaces with a Runic
 * SDK project (`runic.sdkProject`); the Translations view with a runic.json project (`runic.translationsProject`).
 */
export class RunicSdk implements vscode.Disposable {
  readonly log: vscode.LogOutputChannel;
  readonly projects = new Map<string, ProjectState>();
  readonly kinds = new Map<string, WorkspaceKind>();
  private readonly disposables: vscode.Disposable[] = [];
  private readonly doctorDiagnostics = vscode.languages.createDiagnosticCollection("runic-doctor");
  private readonly devDiagnostics = vscode.languages.createDiagnosticCollection("runic-dev");
  private readonly statusItem = vscode.window.createStatusBarItem("runic.dev", vscode.StatusBarAlignment.Left, 99);
  private readonly devView = new Provider(() => this.devRoots());
  private readonly viewModelsView = new Provider(() => this.viewModelRoots());
  private readonly doctorView = new Provider(() => this.doctorRoots());
  private readonly translationsView = new Provider(() => this.translationRoots());
  private poll?: ReturnType<typeof setInterval>;
  private rescanTimer?: ReturnType<typeof setTimeout>;
  private sourceMapTimer?: ReturnType<typeof setTimeout>;
  private scanned: Promise<void> = Promise.resolve();

  constructor(private readonly host: SdkHost) {
    this.log = vscode.window.createOutputChannel("Runic", { log: true });
    this.statusItem.name = "Runic dev"; this.statusItem.command = "runic.dev.actions";
    const sessionWatcher = vscode.workspace.createFileSystemWatcher("**/obj/runic/dev-session.json");
    const sourceMapWatcher = vscode.workspace.createFileSystemWatcher("**/obj/**/runic-bridge/sources.json");
    const projectWatcher = vscode.workspace.createFileSystemWatcher("**/{*.csproj,runic.json}");
    const refreshSessions = () => { void this.refreshSessions(); };
    const reloadSourceMaps = () => { if (this.sourceMapTimer) clearTimeout(this.sourceMapTimer); this.sourceMapTimer = setTimeout(() => { this.sourceMapTimer = undefined; this.loadSourceMaps(); }, 200); };
    const rescan = () => this.scheduleRescan();
    this.disposables.push(this.log, this.doctorDiagnostics, this.devDiagnostics, this.statusItem, sessionWatcher, sourceMapWatcher, projectWatcher,
      sessionWatcher.onDidCreate(refreshSessions), sessionWatcher.onDidChange(refreshSessions), sessionWatcher.onDidDelete(refreshSessions),
      sourceMapWatcher.onDidCreate(reloadSourceMaps), sourceMapWatcher.onDidChange(reloadSourceMaps), sourceMapWatcher.onDidDelete(reloadSourceMaps),
      projectWatcher.onDidCreate(rescan), projectWatcher.onDidDelete(rescan), projectWatcher.onDidChange(uri => { if (uri.path.endsWith(".csproj")) rescan(); }),
      vscode.workspace.onDidChangeWorkspaceFolders(rescan),
      vscode.window.registerTreeDataProvider("runic.devSession", this.devView),
      vscode.window.registerTreeDataProvider("runic.viewModels", this.viewModelsView),
      vscode.window.registerTreeDataProvider("runic.doctor", this.doctorView),
      vscode.window.registerTreeDataProvider("runic.translationsStatus", this.translationsView),
      vscode.tasks.registerTaskProvider("runic", {
        provideTasks: () => [...this.projects.values()].flatMap(state => [this.task(state, "dev"), this.task(state, "dev", true), this.task(state, "build"), this.task(state, "doctor")]),
        resolveTask: task => {
          const definition = task.definition as RunicTaskDefinition;
          const folder = task.scope && typeof task.scope === "object" ? task.scope as vscode.WorkspaceFolder : undefined;
          const project = this.resolveTaskProject(definition, folder);
          const state = project ? this.projects.get(project) : undefined;
          return state ? this.task(state, definition.command, definition.noWindow, definition) : undefined;
        },
      }),
      vscode.debug.registerDebugConfigurationProvider("runic", {
        provideDebugConfigurations: () => [...this.projects.values()].map(state => ({ type: "runic", request: "launch", name: this.projects.size > 1 ? `Runic: Window (${basename(state.project)})` : "Runic: Window", project: this.workspaceRelative(state) })),
        resolveDebugConfiguration: (folder, configuration, token) => this.resolveWindowDebug(folder, configuration, token),
      }),
      vscode.debug.registerDebugConfigurationProvider("runic", {
        provideDebugConfigurations: () => [...this.projects.values()].slice(0, 1).map(() => ({ type: "runic", request: "launch", name: "Runic: Window" })),
      }, vscode.DebugConfigurationProviderTriggerKind.Dynamic),
      vscode.tasks.onDidEndTask(event => {
        for (const state of this.projects.values()) if (state.execution === event.execution) { state.execution = undefined; void state.follower.refresh(); }
      }),
      vscode.languages.registerCodeActionsProvider({ scheme: "file" }, { provideCodeActions: (document, _range, context) => this.codeActions(document, context) },
        { providedCodeActionKinds: [vscode.CodeActionKind.QuickFix] }),
      vscode.commands.registerCommand("runic.showOutput", () => this.log.show(true)),
      vscode.commands.registerCommand("runic.dev.start", (target?: unknown) => this.guard(() => this.startDev(target, false))),
      vscode.commands.registerCommand("runic.dev.startNoWindow", (target?: unknown) => this.guard(() => this.startDev(target, true))),
      vscode.commands.registerCommand("runic.dev.stop", (target?: unknown) => this.guard(() => this.stopDev(target))),
      vscode.commands.registerCommand("runic.dev.debugWindow", (target?: unknown) => this.guard(() => this.debugWindow(target))),
      vscode.commands.registerCommand("runic.dev.openBrowser", (target?: unknown) => this.guard(() => this.openBrowser(target))),
      vscode.commands.registerCommand("runic.dev.openSessionFile", (target?: unknown) => this.guard(async () => { const state = await this.pick(target); if (state) await vscode.window.showTextDocument(vscode.Uri.file(state.follower.path)); })),
      vscode.commands.registerCommand("runic.dev.actions", () => this.guard(() => this.actions())),
      vscode.commands.registerCommand("runic.doctor.run", (target?: unknown) => this.guard(() => this.runDoctor(target))),
      vscode.commands.registerCommand("runic.viewModels.refresh", () => { this.loadSourceMaps(); }),
      vscode.commands.registerCommand("runic.openSourceLocation", (location: SourceLocation) => this.guard(() => openLocation(location))),
      vscode.commands.registerCommand("runic.translations.refreshStatusView", () => this.translationsView.refresh()),
    );
    this.poll = setInterval(() => { if (this.projects.size) void this.refreshSessions(); }, pollInterval);
    this.rescan();
  }

  /** Resolves when the first workspace scan and its session reads are done (the host test waits on it). */
  whenScanned(): Promise<void> { return this.scanned; }

  /** The Translations side changed status (a save, a watched change); the Translations view reloads. */
  translationsChanged() { this.translationsView.refresh(); }

  private scheduleRescan() {
    if (this.rescanTimer) clearTimeout(this.rescanTimer);
    this.rescanTimer = setTimeout(() => { this.rescanTimer = undefined; this.rescan(); }, 300);
  }

  /** Detects the Runic SDK and Translations projects of every workspace folder and sets the context keys of the views. */
  rescan() {
    this.kinds.clear();
    const seen = new Set<string>();
    for (const folder of vscode.workspace.workspaceFolders ?? []) {
      if (folder.uri.scheme !== "file") continue;
      const kind = detectWorkspace(folder.uri.fsPath);
      this.kinds.set(folder.uri.toString(), kind);
      for (const project of kind.sdkProjects) {
        seen.add(project);
        if (!this.projects.has(project)) {
          const state: ProjectState = { project, folder, follower: new DevSessionFollower(project, fileSystemSource, devSessionPath(project), message => this.log.info(message)) };
          state.follower.onDidChange(snapshot => this.sessionChanged(state, snapshot));
          this.projects.set(project, state);
        }
      }
    }
    for (const project of [...this.projects.keys()]) if (!seen.has(project)) { this.projects.delete(project); this.doctorDiagnostics.delete(vscode.Uri.file(project)); this.publishDevProblems(); }
    const kinds = [...this.kinds.values()];
    void vscode.commands.executeCommand("setContext", "runic.sdkProject", this.projects.size > 0);
    void vscode.commands.executeCommand("setContext", "runic.translationsProject", kinds.some(kind => kind.translationsProjects.length > 0));
    this.loadSourceMaps();
    this.scanned = this.refreshSessions().then(() => undefined);
    this.devView.refresh(); this.doctorView.refresh(); this.translationsView.refresh();
    this.updateStatus();
  }

  private async refreshSessions() {
    await Promise.all([...this.projects.values()].map(state => state.follower.refresh()));
  }

  private sessionChanged(state: ProjectState, snapshot: DevSnapshot) {
    this.log.info(`${basename(state.project)}: dotnet runic dev ${devStateLabel(snapshot)}${snapshot.session ? ` (process ${snapshot.session.processId})` : ""}`);
    void vscode.commands.executeCommand("setContext", "runic.devActive", [...this.projects.values()].some(item => isActive(item.follower.snapshot)));
    this.devView.refresh();
    this.updateStatus();
  }

  private updateStatus() {
    const states = [...this.projects.values()];
    if (!states.length) { this.statusItem.hide(); return; }
    const active = states.filter(state => isActive(state.follower.snapshot));
    const single = states.length === 1 ? states[0].follower.snapshot : undefined;
    const icon = single ? (single.state === "ready" ? "$(debug-start)" : single.state === "starting" ? "$(sync~spin)" : "$(debug-stop)") : active.length ? "$(debug-start)" : "$(debug-stop)";
    this.statusItem.text = `${icon} Runic dev${single ? `: ${single.state === "none" ? "not started" : single.state}` : `: ${active.length} of ${states.length} running`}`;
    this.statusItem.tooltip = states.map(state => `${vscode.workspace.asRelativePath(state.project)}: dotnet runic dev ${devStateLabel(state.follower.snapshot)}`).join("\n");
    this.statusItem.show();
  }

  private loadSourceMaps() {
    for (const state of this.projects.values()) {
      state.sourceMapPath = newestSourceMap(state.project);
      state.sourceTree = undefined; state.sourceMapError = undefined;
      if (!state.sourceMapPath) continue;
      try { state.sourceTree = sourceMapTree(parseSourceMap(readFileSync(state.sourceMapPath, "utf8"))); }
      catch (error) { state.sourceMapError = errorSummary(error); }
    }
    this.viewModelsView.refresh();
  }

  // ---- Picking a project -------------------------------------------------------------------------------------------

  private async pick(target?: unknown): Promise<ProjectState | undefined> {
    const named = typeof target === "string" ? target : target instanceof Node ? target.project : target && typeof target === "object" && typeof (target as { project?: unknown }).project === "string" ? (target as { project: string }).project : undefined;
    if (named) {
      const state = this.projects.get(named) ?? [...this.projects.values()].find(item => item.project === resolve(item.folder.uri.fsPath, named));
      if (!state) throw new Error(`${named} is not a Runic SDK project of this workspace.`);
      return state;
    }
    const states = [...this.projects.values()];
    if (!states.length) throw new Error("This workspace has no Runic SDK project (a .csproj of a Runic Views Window).");
    if (states.length === 1) return states[0];
    const active = vscode.window.activeTextEditor?.document.uri.fsPath;
    const containing = active ? states.filter(state => !relative(dirname(state.project), active).startsWith("..")) : [];
    if (containing.length === 1) return containing[0];
    return (await vscode.window.showQuickPick(states.map(state => ({ label: basename(state.project), description: vscode.workspace.asRelativePath(dirname(state.project)), detail: `dotnet runic dev ${devStateLabel(state.follower.snapshot)}`, state })), { placeHolder: "Runic SDK project" }))?.state;
  }

  private workspaceRelative(state: ProjectState) { return relative(state.folder.uri.fsPath, state.project).replaceAll("\\", "/"); }

  private resolveTaskProject(definition: RunicTaskDefinition, folder?: vscode.WorkspaceFolder): string | undefined {
    if (definition.project) return isAbsolute(definition.project) ? definition.project : folder ? resolve(folder.uri.fsPath, definition.project) : undefined;
    const states = [...this.projects.values()].filter(state => !folder || state.folder.uri.toString() === folder.uri.toString());
    return states.length === 1 ? states[0].project : undefined;
  }

  private async guard(action: () => Promise<unknown>) {
    try { return await action(); }
    catch (error) { this.log.error(String(error)); void vscode.window.showErrorMessage(errorSummary(error)); }
  }

  // ---- dotnet runic dev ---------------------------------------------------------------------------------------------

  /** A task of the extension's `runic` type; `dev` gets the `$runic-dev` problem matcher, as the template's task does. */
  private task(state: ProjectState, command: RunicTaskDefinition["command"], noWindow = false, definition?: RunicTaskDefinition, toolVersion = projectRunicToolVersion(state.project)): vscode.Task {
    const dotnet = this.host.dotnetPath(state.folder);
    const several = [...this.projects.values()].filter(item => item.folder === state.folder).length > 1;
    const suffix = several ? ` (${basename(state.project)})` : "";
    const cwd = dirname(state.project);
    definition ??= { type: "runic", command, ...(noWindow ? { noWindow } : {}), ...(several ? { project: this.workspaceRelative(state) } : {}) };
    let task: vscode.Task;
    if (command === "dev") {
      // dotnet-runic 0.22: --debug-endpoint (D070.17) and the stop request on dev's input (WIN-06), through a terminal of the
      // extension's own that holds dev's standard input. An older or unknown tool runs as a process task, as before.
      const sdk022 = supportsDevSdk022(toolVersion);
      const args = devArguments(state.project, noWindow, sdk022);
      const execution = sdk022
        ? new vscode.CustomExecution(async () => {
          const problems = new DevProblems(cwd, noWindow);
          state.devProblems = problems;
          this.publishDevProblems();
          const terminal = new DevTerminal(dotnet, args, cwd, this.log, ended => { if (state.devTerminal === ended) state.devTerminal = undefined; },
            line => { if (problems.add(line) && state.devProblems === problems) this.publishDevProblems(); });
          state.devTerminal = terminal;
          return terminal;
        })
        : new vscode.ProcessExecution(dotnet, args, { cwd });
      // The extension's terminal reports dev's problems itself (DevProblems: [dev] Ready drops a fixed build error, which
      // a problem matcher cannot); its matchers only mark the task ready at [dev] Ready. A process task keeps $runic-dev.
      const matcher = sdk022 ? (noWindow ? "$runic-dev-terminal-no-window" : "$runic-dev-terminal") : (noWindow ? "$runic-dev-no-window" : "$runic-dev");
      task = new vscode.Task(definition, state.folder, noWindow ? `dev --no-window${suffix}` : `dev${suffix}`, "runic", execution, matcher);
      task.isBackground = true;
      task.detail = noWindow ? "dotnet runic dev --no-window: the frontend only; F5 starts the Window" : "dotnet runic dev: the development server and the Window";
    } else if (command === "build") {
      task = new vscode.Task(definition, state.folder, `build${suffix}`, "runic",
        new vscode.ProcessExecution(dotnet, ["build", state.project, "-p:GenerateFullPaths=true", "-consoleLoggerParameters:NoSummary;ForceNoAlign"], { cwd }), "$msCompile");
      task.group = vscode.TaskGroup.Build;
      task.detail = "dotnet build";
    } else {
      task = new vscode.Task(definition, state.folder, `doctor${suffix}`, "runic", new vscode.ProcessExecution(dotnet, ["runic", "doctor", "--project", state.project], { cwd }), []);
      task.detail = "dotnet runic doctor";
    }
    return task;
  }

  /**
   * The template's task of this label (`runic: dev`, `runic: dev --no-window`, `build`) when the project is the folder's
   * only Runic SDK project, so the workspace's own tasks.json wins; else the extension's task.
   */
  private async workspaceTask(state: ProjectState, label: string): Promise<vscode.Task | undefined> {
    const own = [...this.projects.values()].filter(item => item.folder.uri.toString() === state.folder.uri.toString());
    if (own.length !== 1) return undefined;
    const tasks = await vscode.tasks.fetchTasks();
    return tasks.find(task => task.name === label && task.source === "Workspace" && typeof task.scope === "object" && (task.scope as vscode.WorkspaceFolder).uri?.toString() === state.folder.uri.toString());
  }

  private runningExecution(state: ProjectState): vscode.TaskExecution | undefined {
    if (state.execution && vscode.tasks.taskExecutions.includes(state.execution)) return state.execution;
    const labels: string[] = [templateTaskLabels.dev, templateTaskLabels.devNoWindow];
    return vscode.tasks.taskExecutions.find(execution => {
      const task = execution.task;
      const sameFolder = typeof task.scope === "object" && (task.scope as vscode.WorkspaceFolder).uri?.toString() === state.folder.uri.toString();
      if (!sameFolder) return false;
      if (task.definition.type === "runic" && task.definition.command === "dev") return this.resolveTaskProject(task.definition as RunicTaskDefinition, state.folder) === state.project;
      return task.source === "Workspace" && labels.includes(task.name);
    });
  }

  async startDev(target: unknown, noWindow: boolean): Promise<ProjectState | undefined> {
    const state = await this.pick(target);
    if (!state) return;
    const snapshot = await state.follower.refresh();
    if (isActive(snapshot)) {
      const owner = snapshot.session?.window?.owner;
      if ((owner === "ide") === noWindow) { void vscode.commands.executeCommand("runic.devSession.focus"); return state; }
      const restart = "Stop and restart";
      const answer = await vscode.window.showWarningMessage(`dotnet runic dev already runs for ${basename(state.project)}${owner === "ide" ? " without the Window" : " with its Window"}.`, { modal: true }, restart);
      if (answer !== restart) return;
      await this.stopDev(state.project, true);
      await this.waitFor(state, current => !isActive(current), 30000);
    }
    const task = await this.workspaceTask(state, noWindow ? templateTaskLabels.devNoWindow : templateTaskLabels.dev)
      ?? this.task(state, "dev", noWindow, undefined, await detectToolVersion(this.host.dotnetPath(state.folder), dirname(state.project), projectRunicToolVersion(state.project)));
    this.log.info(`Starting ${task.source === "Workspace" ? `the workspace task "${task.name}"` : `dotnet runic dev${noWindow ? " --no-window" : ""}`} for ${state.project}`);
    state.execution = await vscode.tasks.executeTask(task);
    return state;
  }

  async stopDev(target: unknown, quiet = false) {
    const state = await this.pick(target);
    if (!state) return;
    const terminal = state.devTerminal;
    const execution = this.runningExecution(state);
    // dotnet-runic 0.22: stop on the input, so dev runs its own stop; the terminal ends when dev has exited.
    if (terminal?.running) { await terminal.stop(); state.execution = undefined; }
    else if (execution) { execution.terminate(); state.execution = undefined; }
    else {
      const snapshot = await state.follower.refresh();
      if (!isActive(snapshot) || !snapshot.session) { if (!quiet) void vscode.window.showInformationMessage(`dotnet runic dev does not run for ${basename(state.project)}.`); return; }
      // A dev started outside VS Code (a terminal): Ctrl+C's signal stops it the same way, with its Window and server.
      if (process.platform === "win32") { void vscode.window.showInformationMessage(`dotnet runic dev (process ${snapshot.session.processId}) was started outside VS Code; stop it with Ctrl+C in its terminal.`); return; }
      const stop = "Stop it";
      if (!quiet && await vscode.window.showWarningMessage(`dotnet runic dev (process ${snapshot.session.processId}) was started outside VS Code. Stop it as Ctrl+C would?`, { modal: true }, stop) !== stop) return;
      try { process.kill(snapshot.session.processId, "SIGINT"); } catch (error) { throw new Error(`Could not stop process ${snapshot.session.processId}: ${errorSummary(error)}`); }
    }
    setTimeout(() => void state.follower.refresh(), 500);
  }

  /** Resolves with the snapshot once `predicate` holds; rejects after `timeout` ms or on cancellation. */
  private waitFor(state: ProjectState, predicate: (snapshot: DevSnapshot) => boolean, timeout: number, token?: vscode.CancellationToken, failWhen?: (snapshot: DevSnapshot) => string | undefined): Promise<DevSnapshot> {
    return new Promise((resolveWait, rejectWait) => {
      const disposables: { dispose(): void }[] = [];
      const finish = (action: () => void) => { clearInterval(timer); clearTimeout(timeoutTimer); for (const item of disposables) item.dispose(); action(); };
      const check = (snapshot: DevSnapshot) => {
        if (predicate(snapshot)) finish(() => resolveWait(snapshot));
        else { const failure = failWhen?.(snapshot); if (failure) finish(() => rejectWait(new Error(failure))); }
      };
      disposables.push(state.follower.onDidChange(check));
      if (token) disposables.push(token.onCancellationRequested(() => finish(() => rejectWait(new vscode.CancellationError()))));
      const timer = setInterval(() => void state.follower.refresh(), 500);
      const timeoutTimer = setTimeout(() => finish(() => rejectWait(new Error(`dotnet runic dev did not get ready within ${Math.round(timeout / 1000)} seconds; see its task terminal.`))), timeout);
      check(state.follower.snapshot);
    });
  }

  private async openBrowser(target: unknown) {
    const state = await this.pick(target);
    if (!state) return;
    const snapshot = await state.follower.refresh();
    const url = snapshot.state === "ready" ? snapshot.session?.frontend?.url ?? snapshot.session?.pages?.find(page => page.url)?.url : undefined;
    if (!url) throw new Error(`dotnet runic dev serves no development server for ${basename(state.project)} (${devStateLabel(snapshot)}).`);
    await vscode.env.openExternal(vscode.Uri.parse(url));
  }

  private async actions() {
    const state = await this.pick();
    if (!state) return;
    const snapshot = await state.follower.refresh();
    const active = isActive(snapshot);
    const items: (vscode.QuickPickItem & { command: string })[] = [
      ...(active ? [{ label: "$(debug-stop) Stop dotnet runic dev", command: "runic.dev.stop" }] : [
        { label: "$(play) Start dotnet runic dev", description: "the development server and the Window", command: "runic.dev.start" },
        { label: "$(play) Start dotnet runic dev --no-window", description: "the frontend only", command: "runic.dev.startNoWindow" }]),
      { label: "$(debug-alt) Debug the Window", description: "F5 with dotnet runic dev --no-window", command: "runic.dev.debugWindow" },
      ...(snapshot.state === "ready" && snapshot.session?.frontend?.url ? [{ label: "$(globe) Open the development server", description: snapshot.session.frontend.url, command: "runic.dev.openBrowser" }] : []),
      { label: "$(checklist) Run dotnet runic doctor", command: "runic.doctor.run" },
      { label: "$(output) Show the Runic output", command: "runic.showOutput" },
    ];
    const choice = await vscode.window.showQuickPick(items, { placeHolder: `${basename(state.project)}: dotnet runic dev ${devStateLabel(snapshot)}` });
    if (choice) await vscode.commands.executeCommand(choice.command, state.project);
  }

  // ---- F5: the Window under the debugger, with dev --no-window --------------------------------------------------------

  /** Starts the template's "Window" configuration when the folder has one, else the extension's `runic` configuration. */
  async debugWindow(target: unknown) {
    const state = await this.pick(target);
    if (!state) return;
    const template = [...this.projects.values()].filter(item => item.folder === state.folder).length === 1
      ? templateWindowConfiguration(vscode.workspace.getConfiguration("launch", state.folder.uri).get("configurations")) : undefined;
    const started = await vscode.debug.startDebugging(state.folder, template ?? { type: "runic", request: "launch", name: "Runic: Window", project: this.workspaceRelative(state) });
    if (!started) this.log.warn(`The debug session for ${state.project} did not start.`);
  }

  private async resolveWindowDebug(folder: vscode.WorkspaceFolder | undefined, configuration: vscode.DebugConfiguration, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration | undefined> {
    // F5 without a launch.json in a Runic SDK workspace gets the Window.
    if (!configuration.type && !configuration.request && !configuration.name) configuration = { type: "runic", request: "launch", name: "Runic: Window" };
    if (configuration.type !== "runic") return configuration;
    try {
      const state = await this.pick(configuration.project ?? (folder ? [...this.projects.values()].find(item => item.folder.uri.toString() === folder.uri.toString())?.project : undefined));
      if (!state) return undefined;
      if (!vscode.extensions.getExtension("ms-dotnettools.csharp")) {
        const install = "Show the C# extension";
        if (await vscode.window.showErrorMessage("Debugging the Window needs the C# extension (ms-dotnettools.csharp), whose debugger runs it.", install) === install)
          void vscode.commands.executeCommand("workbench.extensions.search", "ms-dotnettools.csharp");
        return undefined;
      }
      return await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Runic: Window of ${basename(state.project)}`, cancellable: true }, async (progress, cancel) => {
        // 1. The frontend: dotnet runic dev --no-window, until [dev] Ready (the session file says ready, owner ide).
        let snapshot = await state.follower.refresh();
        if (!(snapshot.state === "ready" && snapshot.session?.window?.owner === "ide")) {
          progress.report({ message: "starting dotnet runic dev --no-window" });
          if (isActive(snapshot) && snapshot.session?.window?.owner !== "ide") {
            const restart = "Restart with --no-window";
            if (await vscode.window.showWarningMessage(`dotnet runic dev runs ${basename(state.project)}'s Window itself. Restart it with --no-window so the debugger starts the Window?`, { modal: true }, restart) !== restart) return undefined;
            await this.stopDev(state.project, true);
            await this.waitFor(state, current => !isActive(current), 30000, cancel);
          }
          if (!isActive(state.follower.snapshot)) await this.startDev(state.project, true);
          const startedAt = Date.now();
          snapshot = await this.waitFor(state, current => current.state === "ready" && current.session?.window?.owner === "ide", 10 * 60 * 1000, cancel,
            current => current.state === "stopped" && Date.parse(current.session?.startedAt ?? "") >= startedAt - 5000 ? "dotnet runic dev stopped; see its task terminal and the Problems panel." : undefined);
        }
        // 2. The build, which keeps dev's development pages in www/ while dev serves the frontend.
        progress.report({ message: "building" });
        const build = await this.workspaceTask(state, templateTaskLabels.build) ?? this.task(state, "build");
        const exitCode = await runTaskToEnd(build, cancel);
        if (exitCode !== 0) throw new Error(`The build failed (exit code ${exitCode}); see the Problems panel.`);
        // 3. The program dotnet run would start, with the environment the session file names.
        progress.report({ message: "starting the debugger" });
        const configurationName = snapshot.session?.configuration ?? "Debug";
        const properties = parseRunProperties(await execText(this.host.dotnetPath(state.folder), runPropertiesArguments(state.project, configurationName), dirname(state.project)));
        const launch = windowLaunchConfiguration(state.project, properties, snapshot.session, configuration.env as Record<string, string> | undefined);
        this.log.info(`Debugging ${launch.program} with ${JSON.stringify(launch.env)}`);
        return { ...launch, name: configuration.name ?? "Runic: Window" } as vscode.DebugConfiguration;
      });
    } catch (error) {
      if (!(error instanceof vscode.CancellationError)) { this.log.error(String(error)); void vscode.window.showErrorMessage(errorSummary(error)); }
      return undefined;
    }
  }

  // ---- dotnet runic doctor ------------------------------------------------------------------------------------------

  async runDoctor(target: unknown): Promise<DoctorReport | undefined> {
    const state = await this.pick(target);
    if (!state) return;
    state.doctorRunning = true; this.doctorView.refresh();
    try {
      const args = doctorArguments(state.project);
      this.log.info(`> dotnet ${args.join(" ")}`);
      const stdout = await vscode.window.withProgress({ location: { viewId: "runic.doctor" }, title: "dotnet runic doctor" }, () =>
        execText(this.host.dotnetPath(state.folder), args, dirname(state.project), true));
      const report = parseDoctor(stdout);
      state.doctor = report; state.doctorError = undefined;
      this.log.info(`doctor: ${doctorSummary(report)}`);
      for (const check of orderedChecks(report)) if (check.status !== "pass") this.log.info(`  ${check.status} ${check.id}: ${check.message}${check.remediation ? ` Fix: ${check.remediation}` : ""}`);
      this.publishDoctorDiagnostics(state, report);
      return report;
    } catch (error) {
      state.doctor = undefined; state.doctorError = errorSummary(error);
      this.doctorDiagnostics.delete(vscode.Uri.file(state.project));
      throw error;
    } finally { state.doctorRunning = false; this.doctorView.refresh(); }
  }

  /** Failing and warning checks become Problems on the project file, with the remediation in the message. */
  private publishDoctorDiagnostics(state: ProjectState, report: DoctorReport) {
    const version = projectRunicToolVersion(state.project);
    this.doctorDiagnostics.set(vscode.Uri.file(state.project), report.checks.filter(check => check.status !== "pass").map(check => {
      const diagnostic = new vscode.Diagnostic(new vscode.Range(0, 0, 0, 0), `${check.message}${check.remediation ? `\nFix: ${check.remediation}` : ""}`,
        check.status === "fail" ? vscode.DiagnosticSeverity.Error : vscode.DiagnosticSeverity.Warning);
      diagnostic.source = doctorSource;
      diagnostic.code = { value: check.id, target: vscode.Uri.parse(doctorHelpUri(version)) };
      return diagnostic;
    }));
  }

  /** The Problems of every project's `dev` terminal (dotnet-runic 0.22 and later), as MSBuild's own lines name them. */
  private publishDevProblems() {
    const byFile = new Map<string, vscode.Diagnostic[]>();
    for (const state of this.projects.values()) {
      if (!state.devProblems) continue;
      const version = projectRunicToolVersion(state.project);
      for (const problem of state.devProblems.problems) {
        const position = new vscode.Position(Math.max(0, problem.line - 1), Math.max(0, problem.column - 1));
        const diagnostic = new vscode.Diagnostic(new vscode.Range(position, position), problem.message,
          problem.severity === "error" ? vscode.DiagnosticSeverity.Error : vscode.DiagnosticSeverity.Warning);
        diagnostic.source = "runic";
        diagnostic.code = isRunicCode(problem.code) ? { value: problem.code, target: vscode.Uri.parse(diagnosticHelpUri(problem.code, version)) } : problem.code;
        const list = byFile.get(problem.file) ?? [];
        list.push(diagnostic);
        byFile.set(problem.file, list);
      }
    }
    this.devDiagnostics.clear();
    for (const [file, diagnostics] of byFile) this.devDiagnostics.set(vscode.Uri.file(file), diagnostics);
  }

  // ---- Code actions -------------------------------------------------------------------------------------------------

  private codeActions(document: vscode.TextDocument, context: vscode.CodeActionContext): vscode.CodeAction[] {
    const actions: vscode.CodeAction[] = [];
    let version: string | undefined | null = null;
    for (const diagnostic of context.diagnostics) {
      const code = codeValue(diagnostic.code as string | number | { value: string | number } | undefined);
      if (diagnostic.source === doctorSource) {
        const rerun = new vscode.CodeAction("Run dotnet runic doctor again", vscode.CodeActionKind.QuickFix);
        rerun.command = { title: rerun.title, command: "runic.doctor.run", arguments: [document.uri.fsPath] };
        rerun.diagnostics = [diagnostic];
        actions.push(rerun);
      } else if (code && isRunicCode(code)) {
        if (version === null) version = document.uri.scheme === "file" ? projectRunicToolVersion(document.uri.fsPath) : undefined;
        const help = new vscode.CodeAction(`Show ${code} in the Runic diagnostics catalog`, vscode.CodeActionKind.QuickFix);
        help.command = { title: help.title, command: "vscode.open", arguments: [vscode.Uri.parse(diagnosticHelpUri(code, version))] };
        help.diagnostics = [diagnostic];
        actions.push(help);
        if (code === "RAPPCLI1009" || code === "RAPPDEV1008" || code === "RAPPDEV1007") {
          const doctor = new vscode.CodeAction("Run dotnet runic doctor", vscode.CodeActionKind.QuickFix);
          doctor.command = { title: doctor.title, command: "runic.doctor.run" };
          doctor.diagnostics = [diagnostic];
          actions.push(doctor);
        }
      }
    }
    return actions;
  }

  // ---- Views --------------------------------------------------------------------------------------------------------

  private perProject(build: (state: ProjectState) => Node[], describe: (state: ProjectState) => string | undefined, contextValue?: (state: ProjectState) => string): Node[] {
    const states = [...this.projects.values()];
    if (states.length === 1) return build(states[0]);
    return states.map(state => node(basename(state.project), { project: state.project, description: describe(state), tooltip: state.project, icon: "project", children: () => build(state), contextValue: contextValue?.(state), id: state.project }));
  }

  private devRoots(): Node[] {
    return this.perProject(state => this.devDetails(state), state => devStateLabel(state.follower.snapshot), state => isActive(state.follower.snapshot) ? "runicProject.active" : "runicProject.stopped");
  }

  private devDetails(state: ProjectState): Node[] {
    const project = state.project;
    const snapshot = state.follower.snapshot;
    const session = snapshot.session;
    const icon = snapshot.state === "ready" ? new vscode.ThemeIcon("pass-filled", new vscode.ThemeColor("testing.iconPassed")) : snapshot.state === "starting" ? new vscode.ThemeIcon("sync~spin") : new vscode.ThemeIcon("circle-outline");
    const items: Node[] = [node(`dotnet runic dev: ${devStateLabel(snapshot)}`, { project, icon, contextValue: isActive(snapshot) ? "runicDev.active" : "runicDev.stopped",
      tooltip: snapshot.exited ? `The session file says "${session?.state}", but process ${session?.processId} is no longer dotnet runic dev, so the session counts as stopped.` : state.follower.path,
      command: session ? { title: "Open the session file", command: "runic.dev.openSessionFile", arguments: [project] } : undefined })];
    if (!session || !isActive(snapshot)) return items;
    const frontend = session.frontend;
    if (frontend?.url) items.push(node(`Development server: ${frontend.url}`, { project, icon: "globe", description: frontend.developmentServer ?? undefined, command: { title: "Open", command: "vscode.open", arguments: [vscode.Uri.parse(frontend.url)] } }));
    else if (frontend) items.push(node("Frontend: watcher", { project, icon: "eye", description: frontend.outputDirectory }));
    const window = session.window;
    if (window?.owner === "ide") items.push(node("Window: started by the IDE", { project, icon: "debug-alt", description: "F5", command: { title: "Debug the Window", command: "runic.dev.debugWindow", arguments: [project] } }));
    else if (window) items.push(node(window.processId ? `Window: process ${window.processId}` : "Window: not running", { project, icon: "window", description: window.starts ? `${window.starts} start${window.starts === 1 ? "" : "s"}` : undefined }));
    for (const page of session.pages ?? []) if (page.url) items.push(node(`Page: ${page.document}`, { project, icon: "file-code", description: page.url, command: { title: "Open", command: "vscode.open", arguments: [vscode.Uri.parse(page.url)] } }));
    for (const endpoint of session.debugEndpoints ?? []) items.push(node(`Debug endpoint: ${endpoint.engine}`, { project, icon: "debug", description: `${endpoint.protocol} ${endpoint.url}` }));
    if (frontend?.generatedDirectory) items.push(node("Generated clients", { project, icon: "folder", description: vscode.workspace.asRelativePath(frontend.generatedDirectory), command: { title: "Reveal", command: "revealInExplorer", arguments: [vscode.Uri.file(frontend.generatedDirectory)] } }));
    return items;
  }

  private viewModelRoots(): Node[] {
    return this.perProject(state => {
      if (state.sourceMapError) return [node(`The development source map could not be read: ${state.sourceMapError}`, { project: state.project, icon: "warning" })];
      if (!state.sourceTree) return [];
      const toNode = (entry: SourceTreeNode): Node => node(entry.label, {
        project: state.project, description: entry.description, tooltip: entry.tooltip, id: `${state.project}#${entry.id}`,
        icon: entry.kind === "viewModel" ? "symbol-class" : memberIcons[entry.kind] ?? "symbol-field",
        contextValue: entry.location ? "runicSourceEntry" : undefined,
        command: entry.location ? { title: "Open C# member", command: "runic.openSourceLocation", arguments: [entry.location] } : undefined,
        children: entry.children.length ? () => entry.children.map(toNode) : undefined, expanded: false,
      });
      return state.sourceTree.map(toNode);
    }, state => state.sourceTree ? `${state.sourceTree.length} ViewModel${state.sourceTree.length === 1 ? "" : "s"}` : "not built");
  }

  private doctorRoots(): Node[] {
    const states = [...this.projects.values()];
    if (!states.some(state => state.doctor || state.doctorError || state.doctorRunning)) return [];
    return this.perProject(state => {
      if (state.doctorRunning) return [node("Running dotnet runic doctor…", { project: state.project, icon: "sync~spin" })];
      if (state.doctorError) return [node(`doctor failed: ${state.doctorError}`, { project: state.project, icon: "error" })];
      const report = state.doctor;
      if (!report) return [node("Not run yet", { project: state.project, icon: "circle-outline", command: { title: "Run", command: "runic.doctor.run", arguments: [state.project] } })];
      return [node(report.healthy ? "Healthy" : "Not healthy", { project: state.project, description: doctorSummary(report), icon: report.healthy ? "pass" : "error" }),
        ...orderedChecks(report).map(check => checkNode(state.project, check))];
    }, state => state.doctor ? doctorSummary(state.doctor) : undefined);
  }

  private async translationRoots(): Promise<Node[]> {
    const projects = [...this.kinds.entries()].flatMap(([folder, kind]) => kind.translationsProjects.map(project => ({ folder, project })));
    if (projects.length === 0) return [];
    const build = async ({ folder, project }: { folder: string; project: string }): Promise<Node[]> => {
      const workspaceFolder = vscode.workspace.getWorkspaceFolder(vscode.Uri.parse(folder));
      if (!workspaceFolder) return [];
      let result: StatusResult | undefined;
      try { result = await this.host.translationsStatus(workspaceFolder, project); }
      catch (error) { return [node(`Status unavailable: ${errorSummary(error)}`, { icon: "warning" })]; }
      if (!result) return [node("Status unavailable until the catalog compiles and the runic-translations tool is restored", { icon: "warning", command: { title: "Show output", command: "runic.translations.showOutput" } })];
      return result.projects.flatMap(entry => entry.locales.map(locale => node(locale.locale, {
        description: `${Number.isInteger(locale.coverage) ? locale.coverage : locale.coverage.toFixed(1)}%${locale.locale === entry.baseLocale ? " (base)" : ""}`,
        tooltip: coverageLine(locale), icon: locale.belowThreshold ? new vscode.ThemeIcon("warning", new vscode.ThemeColor("problemsWarningIcon.foreground")) : locale.missing ? "circle-large-outline" : "pass",
        command: { title: "Show Translation Status", command: "runic.translations.showStatus" },
      })));
    };
    if (projects.length === 1) return build(projects[0]);
    return Promise.all(projects.map(async entry => { const children = await build(entry); return node(vscode.workspace.asRelativePath(dirname(entry.project)) || basename(dirname(entry.project)), { icon: "globe", tooltip: entry.project, children: () => children }); }));
  }

  // ---- For the extension-host test ----------------------------------------------------------------------------------

  api() {
    return {
      whenScanned: () => this.whenScanned(),
      rescan: () => { this.rescan(); return this.whenScanned(); },
      workspaceKind: (folder: vscode.Uri) => this.kinds.get(folder.toString()),
      projects: () => [...this.projects.keys()],
      refreshSession: (project: string) => this.projects.get(project)?.follower.refresh(),
      devState: (project: string) => this.projects.get(project)?.follower.snapshot,
      devLabel: () => this.statusItem.text,
      /** Reads lines as the project's dev terminal does (a new run's problems), without starting dev. */
      devOutput: (project: string, lines: string[], noWindow = false) => {
        const state = this.projects.get(project);
        if (!state) return;
        state.devProblems = new DevProblems(dirname(project), noWindow);
        for (const line of lines) state.devProblems.add(line);
        this.publishDevProblems();
      },
      viewModels: (project: string) => { this.loadSourceMaps(); return this.projects.get(project)?.sourceTree; },
      runDoctor: (project: string) => this.runDoctor(project),
      codeActions: (document: vscode.TextDocument, diagnostics: vscode.Diagnostic[]) => this.codeActions(document, { diagnostics, triggerKind: vscode.CodeActionTriggerKind.Invoke, only: undefined }),
    };
  }

  dispose() {
    if (this.poll) clearInterval(this.poll);
    if (this.rescanTimer) clearTimeout(this.rescanTimer);
    if (this.sourceMapTimer) clearTimeout(this.sourceMapTimer);
    for (const item of this.disposables) item.dispose();
  }
}

const memberIcons: Record<string, string> = { state: "symbol-property", setter: "symbol-property", commandState: "symbol-boolean", command: "symbol-method", interaction: "symbol-event", request: "symbol-interface" };

function checkNode(project: string, check: DoctorCheck): Node {
  const icon = check.status === "fail" ? new vscode.ThemeIcon("error", new vscode.ThemeColor("problemsErrorIcon.foreground"))
    : check.status === "warn" ? new vscode.ThemeIcon("warning", new vscode.ThemeColor("problemsWarningIcon.foreground")) : new vscode.ThemeIcon("pass", new vscode.ThemeColor("testing.iconPassed"));
  const tooltip = new vscode.MarkdownString(); tooltip.appendText(check.message); if (check.remediation) { tooltip.appendMarkdown("\n\n**Fix:** "); tooltip.appendText(check.remediation); }
  return node(check.id, { project, icon, description: check.message, tooltip, contextValue: `runicDoctorCheck.${check.status}`,
    children: check.remediation ? () => [node(check.remediation!, { project, icon: "lightbulb", tooltip: check.remediation! })] : undefined, expanded: check.status !== "pass" });
}

async function openLocation(location: SourceLocation) {
  if (!location?.file) throw new Error("This entry has no source location; build the project with a PDB.");
  const position = new vscode.Position(Math.max(0, location.line - 1), Math.max(0, location.column - 1));
  try { await vscode.window.showTextDocument(vscode.Uri.file(location.file), { selection: new vscode.Range(position, position), preview: true }); }
  catch (error) { throw new Error(`Could not open ${locationText(location)}: ${errorSummary(error)}`); }
}

/**
 * The terminal of a `dotnet runic dev` task for `dotnet-runic` 0.22 and later: it starts `dev` with a piped standard
 * input and `RUNIC_DEV_STOP_ON_INPUT=1`, shows its output (the task's problem matcher reads it), and stops it by
 * writing `stop` and closing the input, ending the process only when `dev` has not exited within 10 seconds (WIN-06).
 * Stop, closing the terminal and Ctrl+C in it all stop `dev` this way; the terminal closes with `dev`'s exit code.
 */
class DevTerminal implements vscode.Pseudoterminal {
  private readonly writeEmitter = new vscode.EventEmitter<string>();
  private readonly closeEmitter = new vscode.EventEmitter<number | void>();
  readonly onDidWrite = this.writeEmitter.event;
  readonly onDidClose = this.closeEmitter.event;
  private child?: ChildProcess;
  private stopping?: Promise<DevStop>;
  private ended = false;

  constructor(private readonly dotnet: string, private readonly args: string[], private readonly cwd: string, private readonly log: vscode.LogOutputChannel, private readonly onEnd: (terminal: DevTerminal) => void,
    private readonly onLine: (line: string) => void = () => {}) {}

  get running(): boolean { return !!this.child && !this.ended; }

  open(): void {
    this.writeEmitter.fire(terminalText(`> ${this.dotnet} ${this.args.join(" ")}\n`));
    const child = spawnDev(this.dotnet, this.args, this.cwd, true);
    this.child = child;
    const write = (data: string) => this.writeEmitter.fire(terminalText(data));
    // Each stream's lines, for the Problems; the two streams interleave whole lines only.
    const output = new LineSplitter(), errors = new LineSplitter();
    const read = (lines: LineSplitter) => (data: string) => { write(data); for (const line of lines.push(data)) this.onLine(line); };
    child.stdout?.setEncoding("utf8"); child.stdout?.on("data", read(output));
    child.stderr?.setEncoding("utf8"); child.stderr?.on("data", read(errors));
    child.stdout?.on("end", () => { for (const line of output.flush()) this.onLine(line); });
    child.stderr?.on("end", () => { for (const line of errors.flush()) this.onLine(line); });
    child.on("error", error => { write(`\nCould not start ${this.dotnet}: ${error.message}\n`); this.end(1); });
    // dev has ended when its process has (its output read for a short while), not when its pipes close.
    onDevEnd(child, (code, signal) => { write(`\ndotnet runic dev exited with ${code ?? signal}.\n`); this.end(code ?? 1); });
  }

  /** Ctrl+C in the terminal stops dev as the Stop command does. */
  handleInput(data: string): void {
    if (data.includes("\x03")) void this.stop();
  }

  /** The terminal was closed or the task terminated: stop dev the same way. */
  close(): void {
    void this.stop();
  }

  stop(): Promise<DevStop> {
    const child = this.child;
    if (!child || this.ended) return Promise.resolve("exited");
    this.stopping ??= stopDevProcess(child, { stopOnInput: true, force: forceStop }).then(how => {
      if (how === "forced") this.log.warn(`dotnet runic dev (process ${child.pid}) did not stop on its input within 10 seconds; ended it by force.`);
      else this.log.info(`dotnet runic dev (process ${child.pid}) stopped on its input.`);
      return how;
    });
    return this.stopping;
  }

  private end(code: number) {
    if (this.ended) return;
    this.ended = true;
    this.closeEmitter.fire(code);
    this.onEnd(this);
  }
}

/** Runs a task and resolves with its process's exit code. */
async function runTaskToEnd(task: vscode.Task, token: vscode.CancellationToken): Promise<number | undefined> {
  const execution = await vscode.tasks.executeTask(task);
  return new Promise((resolveRun, rejectRun) => {
    const done = vscode.tasks.onDidEndTaskProcess(event => { if (event.execution === execution) { done.dispose(); cancel.dispose(); resolveRun(event.exitCode); } });
    const cancel = token.onCancellationRequested(() => { done.dispose(); cancel.dispose(); execution.terminate(); rejectRun(new vscode.CancellationError()); });
  });
}

/** Runs a command and resolves with its standard output; `allowFailure` resolves with it for a non-zero exit too (JSON envelopes). */
function execText(command: string, args: string[], cwd: string, allowFailure = false): Promise<string> {
  return new Promise((resolveRun, rejectRun) => execFile(command, args, { cwd, maxBuffer: 16 * 1024 * 1024, env: { ...process.env, DOTNET_NOLOGO: "1" } }, (error, stdout, stderr) => {
    if (error && !(allowFailure && stdout.trim())) rejectRun(new Error(`${basename(command)} ${args.slice(0, 2).join(" ")} failed: ${(stderr || stdout || error.message).trim().split("\n").at(-1)}`));
    else resolveRun(stdout);
  }));
}
