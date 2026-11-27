import * as vscode from "vscode";
import { execFile, type ChildProcess } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { basename, dirname, isAbsolute, relative, resolve } from "node:path";
import { detectToolVersion, devArguments, devExitMessage, forceStop, onDevEnd, probeToolsMissing, spawnDev, stopDevProcess, supportsDevSdk022, terminalText, toolRestoreArguments, toolsMissing, type DevStop } from "./dev-process.js";
import { dependencyFixes, addWorkspaceMember, propertyLine, removeJsonProperty, type DependencyFix } from "./dependency-fixes.js";
import { dependencyLabel, dependencyList, dependencyStateLabel, dependencyWatchLabel, frontendGraphPaths, frontendInstall, installCommand, needsBuild, projectFrontendDirectory, readFrontendGraph, unbuiltDependencies, withDevFailures, withFailedPackage, withOwners, type DependencyList, type DevDependency, type FrontendInstall } from "./dependencies.js";
import { DevProblems, LineSplitter, uniqueProblems } from "./dev-problems.js";
import { DevSessionFollower, devSessionPath, devStateLabel, fileSystemSource, isActive, sessionFailure, sessionTime, type DevSessionFailure, type DevSnapshot } from "./dev-session.js";
import { codeValue, diagnosticHelpUri, doctorHelpUri, isRunicCode } from "./diagnostics.js";
import { doctorArguments, doctorSummary, orderedChecks, parseDoctor, type DoctorCheck, type DoctorReport } from "./doctor.js";
import { locationText, newestSourceMap, parseSourceMap, sourceMapTree, type SourceLocation, type SourceTreeNode } from "./source-map.js";
import { coverageLine, errorSummary, type StatusResult } from "./status.js";
import { buildTasks, devExtraArguments, hasTask, parseRunProperties, requestedProfile, runPropertiesArguments, substitutePath, taskReference, templateTaskLabels, templateWindowConfiguration, windowDebugConfigurations, windowLaunchConfiguration } from "./window-debug.js";
import { launchSettingsPath, parseEnvFile, readLaunchProfiles, selectLaunchProfile, type LaunchProfile } from "./launch-settings.js";
import { detectWorkspace, formerCoreTool, projectCoreTool, projectRunicToolVersion, type WorkspaceKind } from "./workspace.js";

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
  /**
   * When the last `dev` task of the project started (milliseconds since the epoch): a stopped session's `failure` from
   * before it no longer applies, also when the new `dev` fails before it writes its own session file.
   */
  devTaskStartedAt?: number;
  /** The frontend packages the page depends on: from the running session, else the build's graph file (S8). */
  dependencies?: DependencyList;
  /** The frontend's install, when its packages are not installed (a fresh clone); DevKit only offers it. */
  install?: FrontendInstall;
  /** How the last `dev` of DevKit's own terminal ended: its exit code and its last output lines. */
  devExit?: DevExit;
  /** F5 waits for the `dev` it started, and reports that `dev`'s early exit itself. */
  f5Waiting?: boolean;
}

/** A `dev` that ended: its exit code (undefined when VS Code reported none) and its last output lines. */
interface DevExit { code: number | undefined; lines: string[] }

/** F5's `dev` exited before its session was ready; `missingTools` when the project's local tools are not restored. */
class DevExitError extends Error {
  constructor(message: string, readonly project: string, readonly missingTools: boolean) { super(message); }
}

interface RunicTaskDefinition extends vscode.TaskDefinition { type: "runic"; command: "dev" | "doctor" | "build"; noWindow?: boolean; project?: string }

const pollInterval = 2000;
const doctorSource = "runic doctor";

/** A node of the Runic views; `project` names the Runic SDK project it belongs to. */
class Node extends vscode.TreeItem {
  children?: () => Node[];
  /** The folder of a frontend dependency's row. */
  packageDirectory?: string;
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
  /** The fresh-clone offer: install the frontend packages, or build the dependency packages; it runs only on a click (D081.4). */
  private readonly dependencyItem = vscode.window.createStatusBarItem("runic.dependencies", vscode.StatusBarAlignment.Left, 98);
  /** What the dependency status item offers while it shows (the host test reads it). */
  private dependencyOffer?: { text: string; command: string; project: string };
  /** The last "Restore tools" offer (the host test reads it): the project and the message it was shown with. */
  private toolRestoreOffer?: { project: string; message: string };
  /** The message of the last F5 that failed before the debugger started (the host test reads it). */
  private f5Failure?: string;
  /** The Runic SDK projects that are not Window projects (libraries, tests): not `dev` targets. */
  private libraries: string[] = [];
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
    const sessionWatcher = vscode.workspace.createFileSystemWatcher("**/obj/{runic,rax}/dev-session.json");
    const sourceMapWatcher = vscode.workspace.createFileSystemWatcher("**/obj/**/runic-bridge/sources.json");
    const projectWatcher = vscode.workspace.createFileSystemWatcher("**/{*.csproj,runic.json}");
    const graphWatcher = vscode.workspace.createFileSystemWatcher("**/obj/{runic,rax}/frontend-graph.json");
    const reloadDependencies = () => this.loadDependencies();
    const refreshSessions = () => { void this.refreshSessions(); };
    const reloadSourceMaps = () => { if (this.sourceMapTimer) clearTimeout(this.sourceMapTimer); this.sourceMapTimer = setTimeout(() => { this.sourceMapTimer = undefined; this.loadSourceMaps(); }, 200); };
    const rescan = () => this.scheduleRescan();
    this.dependencyItem.name = "Runic frontend dependencies";
    this.disposables.push(this.log, this.doctorDiagnostics, this.devDiagnostics, this.statusItem, this.dependencyItem, sessionWatcher, sourceMapWatcher, projectWatcher, graphWatcher,
      graphWatcher.onDidCreate(reloadDependencies), graphWatcher.onDidChange(reloadDependencies), graphWatcher.onDidDelete(reloadDependencies),
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
        provideDebugConfigurations: folder => this.debugConfigurations(folder),
        resolveDebugConfiguration: (folder, configuration, token) => this.resolveWindowDebug(folder, configuration, token),
      }),
      // Run and Debug's list: one entry per Window project and launch profile (design/ide-run-debug E2).
      vscode.debug.registerDebugConfigurationProvider("runic", {
        provideDebugConfigurations: folder => this.dynamicDebugConfigurations(folder),
      }, vscode.DebugConfigurationProviderTriggerKind.Dynamic),
      // A new dev, whoever starts it (F5's preLaunchTask, Terminal > Run Task, DevKit): the last one's failure goes.
      vscode.tasks.onDidStartTask(event => {
        for (const state of this.projects.values()) {
          if (!this.isDevTask(state, event.execution.task)) continue;
          state.devTaskStartedAt = Date.now();
          // DevKit's own terminal starts a new reader itself; a task VS Code runs has none, and the last one's are stale.
          if (!(event.execution.task.execution instanceof vscode.CustomExecution)) state.devProblems = undefined;
          this.publishDevProblems();
          this.loadDependencies();
        }
      }),
      vscode.tasks.onDidEndTask(event => {
        for (const state of this.projects.values()) if (state.execution === event.execution) { state.execution = undefined; void state.follower.refresh(); }
        // A build or an install may have built or installed the frontend packages.
        this.loadDependencies();
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
      vscode.commands.registerCommand("runic.dependencies.build", (target?: unknown) => this.guard(() => this.buildDependencies(target))),
      vscode.commands.registerCommand("runic.dependencies.install", (target?: unknown) => this.guard(() => this.installDependencies(target))),
      vscode.commands.registerCommand("runic.dependencies.openPackage", (target?: unknown, property?: string) => this.guard(() => this.openPackage(target, property))),
      vscode.commands.registerCommand("runic.dependencies.show", (label?: string) => this.guard(() => this.showDependency(label))),
      vscode.commands.registerCommand("runic.tools.restore", (target?: unknown) => this.guard(() => this.restoreTools(target))),
      vscode.commands.registerCommand("runic.dependencies.run", (cwd: string, args: string[]) => this.guard(() => this.runPackageManager(cwd, args))),
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
    const libraries: string[] = [];
    for (const folder of vscode.workspace.workspaceFolders ?? []) {
      if (folder.uri.scheme !== "file") continue;
      const kind = detectWorkspace(folder.uri.fsPath);
      this.kinds.set(folder.uri.toString(), kind);
      libraries.push(...kind.sdkProjects.filter(project => !kind.windowProjects.includes(project)));
      for (const project of kind.windowProjects) {
        seen.add(project);
        if (!this.projects.has(project)) {
          const state: ProjectState = { project, folder, follower: new DevSessionFollower(project, fileSystemSource, devSessionPath(project, projectCoreTool(project)?.tool), message => this.log.info(message)) };
          state.follower.onDidChange(snapshot => this.sessionChanged(state, snapshot));
          this.projects.set(project, state);
        }
      }
    }
    for (const project of [...this.projects.keys()]) if (!seen.has(project)) { this.projects.delete(project); this.doctorDiagnostics.delete(vscode.Uri.file(project)); this.publishDevProblems(); }
    this.libraries = libraries.sort();
    const kinds = [...this.kinds.values()];
    void vscode.commands.executeCommand("setContext", "runic.sdkProject", kinds.some(kind => kind.sdkProjects.length > 0));
    void vscode.commands.executeCommand("setContext", "runic.translationsProject", kinds.some(kind => kind.translationsProjects.length > 0));
    this.loadSourceMaps();
    this.scanned = this.refreshSessions().then(() => this.loadDependencies());
    this.devView.refresh(); this.doctorView.refresh(); this.translationsView.refresh();
    this.updateStatus();
  }

  private async refreshSessions() {
    await Promise.all([...this.projects.values()].map(state => state.follower.refresh()));
  }

  private sessionChanged(state: ProjectState, snapshot: DevSnapshot) {
    this.log.info(`${basename(state.project)}: dotnet runic dev ${devStateLabel(snapshot)}${snapshot.session ? ` (process ${snapshot.session.processId})` : ""}`);
    void vscode.commands.executeCommand("setContext", "runic.devActive", [...this.projects.values()].some(item => isActive(item.follower.snapshot)));
    this.loadDependencies();
    // A stopped session's failure, or a new session without one.
    this.publishDevProblems();
    this.updateStatus();
  }

  /**
   * Reads each Window project's frontend dependencies (the running session's `dependencies`, else the graph file) and
   * whether its frontend's packages are installed, then updates the Dev Session view and the fresh-clone offer. It
   * reads files only; nothing is installed or built here (D081.4).
   */
  private loadDependencies() {
    const owners = this.libraries.flatMap(project => {
      let projectText = "";
      try { projectText = readFileSync(project, "utf8"); } catch { return []; }
      const directory = projectFrontendDirectory(project, projectText);
      return directory ? [{ project, directory }] : [];
    });
    for (const state of this.projects.values()) {
      const snapshot = state.follower.snapshot;
      const tool = projectCoreTool(state.project)?.tool;
      // A package whose build failed in the last dev still has an earlier build's exports on disk, so its own state may read
      // "built"; the errors of that dev, which the Problems panel shows, say it failed.
      // The session file's failure (runic-sdk 0.24) also names the package, however dev was started.
      const failure = this.sessionFailure(state);
      // A project whose first dev failed before any build has no graph yet: the failure's package gets a row of its own.
      const list = withFailedPackage(dependencyList(snapshot.session, isActive(snapshot), () => readFrontendGraph(frontendGraphPaths(state.project, tool))), failure, state.project);
      state.dependencies = withDevFailures(withOwners(list, owners),
        [...state.devProblems?.problems ?? [], ...failure?.diagnostics ?? []], failure);
      state.install = undefined;
      if (isActive(snapshot)) continue;
      let projectText = "";
      try { projectText = readFileSync(state.project, "utf8"); } catch { /* Unreadable: Frontend beside it. */ }
      const frontend = state.dependencies?.workspace?.application ?? snapshot.session?.frontend?.directory ?? projectFrontendDirectory(state.project, projectText);
      const install = frontend ? frontendInstall(frontend, state.folder.uri.fsPath) : undefined;
      if (install?.needed) state.install = install;
    }
    this.devView.refresh();
    this.updateDependencyStatus();
  }

  /** The folder of the frontend package that `dev` labels a line with (`[shared-ui]`, the package's folder name). */
  private packageDirectory(state: ProjectState, label: string): string | undefined {
    return state.dependencies?.dependencies.find(item => basename(item.directory) === label)?.directory;
  }

  /** The status bar's offer: install the frontend packages of a fresh clone, else build its unbuilt dependency packages. */
  private updateDependencyStatus() {
    const states = [...this.projects.values()];
    const installing = states.find(state => state.install?.lockFile);
    const unbuilt = states.map(state => ({ state, packages: unbuiltDependencies(state.dependencies) })).find(entry => entry.packages.length > 0);
    if (installing?.install) {
      const install = installing.install;
      this.dependencyItem.text = "$(package) Install frontend dependencies";
      this.dependencyItem.tooltip = `${basename(installing.project)}: the frontend packages are not installed. Click to run '${installCommand(install.packageManager).join(" ")}' in ${install.root}; DevKit never runs it by itself.`;
      this.dependencyItem.command = { title: "Install frontend dependencies", command: "runic.dependencies.install", arguments: [installing.project] };
      this.dependencyItem.show();
      this.dependencyOffer = { text: this.dependencyItem.text, command: "runic.dependencies.install", project: installing.project };
    } else if (unbuilt) {
      const names = unbuilt.packages.map(item => item.ownerName ? `${item.ownerName}'s package` : dependencyLabel(item));
      this.dependencyItem.text = `$(tools) Build frontend dependencies`;
      this.dependencyItem.tooltip = `${names.join(", ")} ${names.length === 1 ? "is" : "are"} not built. Click to run dotnet build ${basename(unbuilt.state.project)}, which builds ${names.length === 1 ? "it" : "them"}.`;
      this.dependencyItem.command = { title: "Build frontend dependencies", command: "runic.dependencies.build", arguments: [unbuilt.state.project] };
      this.dependencyItem.show();
      this.dependencyOffer = { text: this.dependencyItem.text, command: "runic.dependencies.build", project: unbuilt.state.project };
    } else { this.dependencyItem.hide(); this.dependencyOffer = undefined; }
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
    if (!states.length) throw new Error(this.libraries.length
      ? `This workspace has no Window project that dotnet runic dev can run; ${this.libraries.map(project => basename(project)).join(", ")} ${this.libraries.length === 1 ? "is a library or test project" : "are library or test projects"}.`
      : "This workspace has no Runic SDK project (a .csproj of a Runic Views Window).");
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
  private task(state: ProjectState, command: RunicTaskDefinition["command"], noWindow = false, definition?: RunicTaskDefinition, extra: readonly string[] = [], toolVersion = projectRunicToolVersion(state.project)): vscode.Task {
    const dotnet = this.host.dotnetPath(state.folder);
    // `dotnet runic` (dotnet-runic) or `dotnet rax` (RunicArtifex.Tool), by what the project's tool manifest lists.
    const verb = (projectCoreTool(state.project)?.tool ?? formerCoreTool).verb;
    const several = [...this.projects.values()].filter(item => item.folder === state.folder).length > 1;
    const suffix = several ? ` (${basename(state.project)})` : "";
    const cwd = dirname(state.project);
    definition ??= { type: "runic", command, ...(noWindow ? { noWindow } : {}), ...(several ? { project: this.workspaceRelative(state) } : {}) };
    let task: vscode.Task;
    if (command === "dev") {
      // dotnet-runic 0.22: --debug-endpoint (D070.17) and the stop request on dev's input (WIN-06), through a terminal of the
      // extension's own that holds dev's standard input. An older or unknown tool runs as a process task, as before.
      const sdk022 = supportsDevSdk022(toolVersion);
      const args = [...devArguments(state.project, noWindow, sdk022, verb), ...extra];
      const execution = sdk022
        ? new vscode.CustomExecution(async () => {
          const problems = new DevProblems(cwd, noWindow, label => this.packageDirectory(state, label));
          state.devProblems = problems;
          this.publishDevProblems();
          state.devExit = undefined;
          const terminal = new DevTerminal(dotnet, args, cwd, this.log, (ended, code, lines) => {
            if (state.devTerminal !== ended) return;
            state.devTerminal = undefined; state.devExit = { code, lines };
            // A dev that the tool's launcher could not start (a fresh clone): offer the restore, unless F5 reports it.
            if (code !== 0 && !state.f5Waiting && toolsMissing(lines)) this.offerToolRestore(state.project, devExitMessage(code, lines, true));
          },
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
      task.detail = noWindow ? `dotnet ${verb} dev --no-window: the frontend only; F5 starts the Window` : `dotnet ${verb} dev: the development server and the Window`;
    } else if (command === "build") {
      task = new vscode.Task(definition, state.folder, `build${suffix}`, "runic",
        new vscode.ProcessExecution(dotnet, ["build", state.project, "-p:GenerateFullPaths=true", "-consoleLoggerParameters:NoSummary;ForceNoAlign"], { cwd }), "$msCompile");
      task.group = vscode.TaskGroup.Build;
      task.detail = "dotnet build";
    } else {
      task = new vscode.Task(definition, state.folder, `doctor${suffix}`, "runic", new vscode.ProcessExecution(dotnet, [verb, "doctor", "--project", state.project], { cwd }), []);
      task.detail = `dotnet ${verb} doctor`;
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
    return vscode.tasks.taskExecutions.find(execution => this.isDevTask(state, execution.task, [templateTaskLabels.dev, templateTaskLabels.devNoWindow]));
  }

  /** Whether a task runs the project's `dev`: DevKit's `runic` dev task, or one of the template's `dev` tasks of its folder. */
  private isDevTask(state: ProjectState, task: vscode.Task, labels: readonly string[] = [templateTaskLabels.dev, templateTaskLabels.devNoWindow, templateTaskLabels.devBrowser]): boolean {
    const sameFolder = typeof task.scope === "object" && (task.scope as vscode.WorkspaceFolder).uri?.toString() === state.folder.uri.toString();
    if (!sameFolder) return false;
    if (task.definition.type === "runic" && task.definition.command === "dev") return this.resolveTaskProject(task.definition as RunicTaskDefinition, state.folder) === state.project;
    return task.source === "Workspace" && labels.includes(task.name);
  }

  /**
   * The failure that stopped the project's last `dev`, from its session file (runic-sdk 0.24, D087), or undefined: while
   * `dev` runs, after a stop without one, for an older tool (no `failure`), and once another `dev` task has started.
   */
  private sessionFailure(state: ProjectState): DevSessionFailure | undefined {
    const session = state.follower.snapshot.session;
    const failure = sessionFailure(session);
    if (!failure || state.devTaskStartedAt === undefined) return failure;
    // dev writes its startedAt after the task started; a second for the clocks' rounding.
    return sessionTime(session?.startedAt) >= state.devTaskStartedAt - 1000 ? failure : undefined;
  }

  /** `extra` are more `dev` options (a configuration's `devArgs`); with them DevKit runs its own task, not the template's. */
  async startDev(target: unknown, noWindow: boolean, extra: readonly string[] = []): Promise<ProjectState | undefined> {
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
    const task = (extra.length ? undefined : await this.workspaceTask(state, noWindow ? templateTaskLabels.devNoWindow : templateTaskLabels.dev))
      ?? this.task(state, "dev", noWindow, undefined, extra, await detectToolVersion(this.host.dotnetPath(state.folder), dirname(state.project), projectRunicToolVersion(state.project), (projectCoreTool(state.project)?.tool ?? formerCoreTool).verb));
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

  /** Resolves with the snapshot once `predicate` holds; rejects after `timeout` ms, on cancellation, or with `abort`'s error. */
  private waitFor(state: ProjectState, predicate: (snapshot: DevSnapshot) => boolean, timeout: number, token?: vscode.CancellationToken, failWhen?: (snapshot: DevSnapshot) => string | undefined, abort?: Promise<Error>): Promise<DevSnapshot> {
    return new Promise((resolveWait, rejectWait) => {
      const disposables: { dispose(): void }[] = [];
      const finish = (action: () => void) => { clearInterval(timer); clearTimeout(timeoutTimer); for (const item of disposables) item.dispose(); action(); };
      const check = (snapshot: DevSnapshot) => {
        if (predicate(snapshot)) finish(() => resolveWait(snapshot));
        else { const failure = failWhen?.(snapshot); if (failure) finish(() => rejectWait(new Error(failure))); }
      };
      disposables.push(state.follower.onDidChange(check));
      if (token) disposables.push(token.onCancellationRequested(() => finish(() => rejectWait(new vscode.CancellationError()))));
      let done = false;
      disposables.push({ dispose: () => { done = true; } });
      // A session that got ready just before dev exited still wins: the snapshot is read once more first.
      void abort?.then(async error => { const last = await state.follower.refresh(); if (!done) { if (predicate(last)) finish(() => resolveWait(last)); else finish(() => rejectWait(error)); } });
      const timer = setInterval(() => void state.follower.refresh(), 500);
      const timeoutTimer = setTimeout(() => finish(() => rejectWait(new Error(`dotnet runic dev did not get ready within ${Math.round(timeout / 1000)} seconds; see its task terminal.`))), timeout);
      check(state.follower.snapshot);
    });
  }

  /**
   * Watches the `dev` task F5 started: once it ends, `failure` resolves with a {@link DevExitError} that names its exit
   * code and last output lines, and says whether the project's local tools are missing (its output, else a probe of
   * `dotnet <verb> --version`).
   */
  private watchDevExit(state: ProjectState, execution: vscode.TaskExecution): { failure: Promise<Error>; dispose(): void } {
    const disposables: vscode.Disposable[] = [];
    const failure = new Promise<Error>(resolveFailure => {
      let processCode: number | undefined;
      const ended = async () => {
        for (const item of disposables) item.dispose();
        const exit: DevExit = state.devExit ?? { code: processCode, lines: [] };
        const dotnet = this.host.dotnetPath(state.folder);
        const missing = toolsMissing(exit.lines) || (exit.code !== 0 && await probeToolsMissing(dotnet, dirname(state.project), (projectCoreTool(state.project)?.tool ?? formerCoreTool).verb));
        const message = devExitMessage(exit.code, exit.lines, missing);
        for (const line of exit.lines) this.log.info(`  dev: ${line}`);
        resolveFailure(new DevExitError(message, state.project, missing));
      };
      disposables.push(
        vscode.tasks.onDidEndTaskProcess(event => { if (event.execution === execution) processCode = event.exitCode; }),
        vscode.tasks.onDidEndTask(event => { if (event.execution === execution) void ended(); }));
      if (!vscode.tasks.taskExecutions.includes(execution)) void ended();
    });
    return { failure, dispose: () => { for (const item of disposables) item.dispose(); } };
  }

  /**
   * Offers `dotnet tool restore` behind a click (D081.4): the error names the failure and the button runs the restore.
   */
  private offerToolRestore(project: string, message: string) {
    this.toolRestoreOffer = { project, message };
    const restore = "Restore tools";
    void vscode.window.showErrorMessage(message, restore).then(answer => { if (answer === restore) void vscode.commands.executeCommand("runic.tools.restore", project); });
  }

  /** "Restore tools": `dotnet tool restore` in the project's directory as a task; F5 can run again once it has passed. */
  async restoreTools(target: unknown): Promise<number | undefined> {
    const state = await this.pick(target);
    if (!state) return;
    const cwd = dirname(state.project);
    const task = new vscode.Task({ type: "runic-tools", command: "restore", cwd }, state.folder, `tool restore (${basename(cwd)})`, "runic",
      new vscode.ProcessExecution(this.host.dotnetPath(state.folder), toolRestoreArguments(), { cwd }), []);
    task.detail = `dotnet tool restore in ${cwd}`;
    this.log.info(`> dotnet ${toolRestoreArguments().join(" ")} (in ${cwd})`);
    const source = new vscode.CancellationTokenSource();
    try {
      const code = await runTaskToEnd(task, source.token);
      if (code === 0) { this.toolRestoreOffer = undefined; void vscode.window.showInformationMessage(`The local tools of ${basename(state.project)} are restored; press F5 to debug the Window again.`); }
      else void vscode.window.showErrorMessage(`dotnet tool restore failed (exit code ${code}); see its task terminal.`);
      return code;
    } finally { source.dispose(); }
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
      ...(state.install?.lockFile ? [{ label: "$(package) Install frontend dependencies", description: installCommand(state.install.packageManager).join(" "), command: "runic.dependencies.install" }] : []),
      ...(state.dependencies?.dependencies.length ? [{ label: "$(tools) Build frontend dependencies", description: `dotnet build ${basename(state.project)}`, command: "runic.dependencies.build" }] : []),
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
      // The profile, envFile and devArgs are read first, so a misspelt profile stops F5 before dev starts.
      const workspaceFolder = (folder ?? state.folder).uri.fsPath;
      const settingsFile = typeof configuration.launchSettingsFilePath === "string" && configuration.launchSettingsFilePath.trim()
        ? substitutePath(configuration.launchSettingsFilePath, workspaceFolder, homedir()) : launchSettingsPath(state.project);
      const profile = selectLaunchProfile(readLaunchProfiles(state.project, settingsFile), requestedProfile(configuration), settingsFile);
      const envFile = typeof configuration.envFile === "string" && configuration.envFile.trim() ? readEnvFile(substitutePath(configuration.envFile, workspaceFolder, homedir())) : undefined;
      const devExtra = devExtraArguments(configuration, profile);
      if (!hasCoreclrDebugger()) {
        const install = "Show the C# extension";
        if (await vscode.window.showErrorMessage("Debugging the Window needs the C# extension (ms-dotnettools.csharp), whose debugger runs it.", install) === install)
          void vscode.commands.executeCommand("workbench.extensions.search", "ms-dotnettools.csharp");
        return undefined;
      }
      return await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Runic: Window of ${basename(state.project)}`, cancellable: true }, async (progress, cancel) => {
        // 1. The frontend: dotnet runic dev --no-window, until [dev] Ready (the session file says ready, owner ide).
        let snapshot = await state.follower.refresh();
        let startedDev = false;
        if (!(snapshot.state === "ready" && snapshot.session?.window?.owner === "ide")) {
          progress.report({ message: "starting dotnet runic dev --no-window" });
          if (isActive(snapshot) && snapshot.session?.window?.owner !== "ide") {
            const restart = "Restart with --no-window";
            if (await vscode.window.showWarningMessage(`dotnet runic dev runs ${basename(state.project)}'s Window itself. Restart it with --no-window so the debugger starts the Window?`, { modal: true }, restart) !== restart) return undefined;
            await this.stopDev(state.project, true);
            await this.waitFor(state, current => !isActive(current), 30000, cancel);
          }
          // F5 ends as soon as the dev it started exits, also before dev writes a session file (a fresh clone without its tools).
          let ended: { failure: Promise<Error>; dispose(): void } | undefined;
          state.f5Waiting = true;
          try {
            if (!isActive(state.follower.snapshot)) {
              await this.startDev(state.project, true, devExtra); startedDev = true;
              if (state.execution) ended = this.watchDevExit(state, state.execution);
            }
            const startedAt = Date.now();
            snapshot = await this.waitFor(state, current => current.state === "ready" && current.session?.window?.owner === "ide", 10 * 60 * 1000, cancel,
              current => current.state === "stopped" && Date.parse(current.session?.startedAt ?? "") >= startedAt - 5000 ? "dotnet runic dev stopped; see its task terminal and the Problems panel." : undefined, ended?.failure);
          } finally { ended?.dispose(); state.f5Waiting = false; }
        }
        // 2. The build, which keeps dev's development pages in www/ while dev serves the frontend. VS Code runs it as the
        // resolved configuration's preLaunchTask: Restart (Ctrl+Shift+F5) reuses the resolved configuration without
        // calling this resolver again and runs only its tasks, so the build must be one of them to rebuild on Restart.
        const build = await this.workspaceTask(state, templateTaskLabels.build) ?? this.task(state, "build");
        const ownPreLaunchTask = hasTask(configuration.preLaunchTask);
        if (ownPreLaunchTask) {
          // The configuration's own preLaunchTask stays VS Code's to run after this build; Restart builds first.
          progress.report({ message: "building" });
          const exitCode = await runTaskToEnd(build, cancel);
          if (exitCode !== 0) throw new Error(`The build failed (exit code ${exitCode}); see the Problems panel.`);
        }
        // 3. The program dotnet run would start, with the environment the session file names.
        progress.report({ message: "starting the debugger" });
        const configurationName = snapshot.session?.configuration ?? "Debug";
        const properties = parseRunProperties(await execText(this.host.dotnetPath(state.folder), runPropertiesArguments(state.project, configurationName), dirname(state.project)));
        if (devExtra.length && !startedDev) this.log.info(`dotnet runic dev already runs, so ${devExtra.join(" ")} (devArgs) applies the next time DevKit starts it.`);
        const launch: Record<string, unknown> = { ...windowLaunchConfiguration(state.project, properties, snapshot.session, { configuration, profile, envFile }), ...buildTasks(taskReference(build), configuration) };
        this.log.info(`Debugging ${launch.program} ${JSON.stringify(launch.args)} in ${launch.cwd}${profile ? ` with launch profile "${profile.name}"` : ""}; variables ${Object.keys(launch.env as Record<string, string>).join(", ")}; the task "${build.name}" builds it, also on Restart`);
        return launch as vscode.DebugConfiguration;
      });
    } catch (error) {
      if (!(error instanceof vscode.CancellationError)) this.f5Failure = error instanceof Error ? error.message : String(error);
      if (error instanceof DevExitError && error.missingTools) { this.log.error(error.message); this.offerToolRestore(error.project, error.message); }
      else if (!(error instanceof vscode.CancellationError)) { this.log.error(String(error)); void vscode.window.showErrorMessage(errorSummary(error)); }
      return undefined;
    }
  }

  /**
   * Run and Debug's dynamic `runic` entries. VS Code asks the provider once per launch scope: each folder, and once more
   * without a folder for the user settings' launch (and a workspace file's); those get none, so every entry shows once,
   * under its folder.
   */
  private dynamicDebugConfigurations(folder: vscode.WorkspaceFolder | undefined): vscode.DebugConfiguration[] {
    return folder ? this.debugConfigurations(folder) : [];
  }

  /** The `runic` entries of Run and Debug (dynamic) and of a new launch.json (initial): one per Window project and profile. */
  private debugConfigurations(folder: vscode.WorkspaceFolder | undefined): vscode.DebugConfiguration[] {
    const states = [...this.projects.values()].filter(state => !folder || state.folder.uri.toString() === folder.uri.toString());
    return windowDebugConfigurations(states.map(state => {
      let profiles: LaunchProfile[] = [];
      try { profiles = readLaunchProfiles(state.project); } catch (error) { this.log.warn(String(error)); }
      return { project: state.project, relative: this.workspaceRelative(state), profiles };
    })) as vscode.DebugConfiguration[];
  }

  // ---- dotnet runic doctor ------------------------------------------------------------------------------------------

  async runDoctor(target: unknown): Promise<DoctorReport | undefined> {
    const state = await this.pick(target);
    if (!state) return;
    state.doctorRunning = true; this.doctorView.refresh();
    try {
      const args = doctorArguments(state.project, (projectCoreTool(state.project)?.tool ?? formerCoreTool).verb);
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

  // ---- Frontend dependencies (S8) ------------------------------------------------------------------------------------

  /**
   * "Build frontend dependencies": `dotnet build` of the Window project (the template's `build` task when it has one).
   * The Views targets build every frontend package the page depends on, after the workspace's locked install, so the
   * command has no install or build logic of its own.
   */
  async buildDependencies(target: unknown) {
    const state = await this.pick(target);
    if (!state) return;
    const task = await this.workspaceTask(state, templateTaskLabels.build) ?? this.task(state, "build");
    this.log.info(`Building the frontend dependencies of ${state.project} with ${task.source === "Workspace" ? `the workspace task "${task.name}"` : "dotnet build"}`);
    await vscode.tasks.executeTask(task);
  }

  /**
   * "Install frontend dependencies" of a fresh clone: the locked install the Views targets run (`bun install
   * --frozen-lockfile --ignore-scripts`, `pnpm install --frozen-lockfile --ignore-scripts`, `npm ci --ignore-scripts`)
   * at the workspace root, as a task, after a confirmation that names it. Only on a click (D081.4); `dotnet build` and
   * F5 install through MSBuild anyway.
   */
  async installDependencies(target: unknown) {
    const state = await this.pick(target);
    if (!state) return;
    this.loadDependencies();
    let projectText = "";
    try { projectText = readFileSync(state.project, "utf8"); } catch { /* Frontend beside it. */ }
    const frontend = state.dependencies?.workspace?.application ?? state.follower.snapshot.session?.frontend?.directory ?? projectFrontendDirectory(state.project, projectText);
    const install = frontend ? frontendInstall(frontend, state.folder.uri.fsPath) : undefined;
    if (!install) throw new Error(`${basename(state.project)} has no frontend package.json to install.`);
    if (!install.lockFile) throw new Error(`The frontend workspace ${install.root} has no lock file, so its install would not be locked (RUNICBRIDGE026). Run '${install.packageManager} install' there and commit the lock file.`);
    const args = installCommand(install.packageManager);
    const run = "Install";
    if (await vscode.window.showInformationMessage(`Run '${args.join(" ")}' in ${install.root}?`, { modal: true, detail: "It installs the frontend packages from the lock file without running their install scripts, as dotnet build does." }, run) !== run) return;
    await this.runPackageManager(install.root, args);
  }

  /** Runs a package manager command (an install) as a task in a folder; its terminal shows the output. */
  async runPackageManager(cwd: string, args: string[]) {
    if (!Array.isArray(args) || !args.length || !/^(npm|pnpm|bun)$/.test(args[0])) throw new Error("Only npm, pnpm or bun runs here.");
    const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(cwd)) ?? vscode.TaskScope.Workspace;
    const task = new vscode.Task({ type: "runic-install", command: args.join(" "), cwd }, folder, `${args.slice(0, 2).join(" ")} (${basename(cwd)})`, "runic", new vscode.ShellExecution(args[0], args.slice(1), { cwd }), []);
    task.detail = `${args.join(" ")} in ${cwd}`;
    this.log.info(`> ${args.join(" ")} (in ${cwd})`);
    await vscode.tasks.executeTask(task);
  }

  /** Opens a dependency's package.json (a view row, or a path), at a property when given. */
  async openPackage(target: unknown, property?: string) {
    const directory = target instanceof Node ? target.packageDirectory : typeof target === "string" ? target : undefined;
    if (!directory) return;
    const file = /\.(json|ya?ml)$/i.test(directory) ? directory : resolve(directory, "package.json");
    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(file));
    const line = property ? propertyLine(document.getText(), property) : 0;
    await vscode.window.showTextDocument(document, { selection: new vscode.Range(line, 0, line, 0), preview: true });
  }

  /** Shows the Dev Session view, and the package.json of the dependency a RAPPDEV1018 line names (`[shared-ui]`). */
  async showDependency(label?: string) {
    await vscode.commands.executeCommand("runic.devSession.focus");
    if (!label) return;
    const dependency = [...this.projects.values()].flatMap(state => state.dependencies?.dependencies ?? [])
      .find(item => item.package === label || basename(item.directory) === label);
    if (dependency) await this.openPackage(dependency.directory);
  }

  /** The code action of one dependency fix (dependency-fixes.ts) for a diagnostic of `document`. */
  private dependencyAction(fix: DependencyFix, document: vscode.TextDocument, diagnostic: vscode.Diagnostic): vscode.CodeAction | undefined {
    const action = new vscode.CodeAction(fix.title, vscode.CodeActionKind.QuickFix);
    action.diagnostics = [diagnostic];
    switch (fix.kind) {
      case "open": action.command = { title: fix.title, command: "runic.dependencies.openPackage", arguments: [fix.file, fix.property] }; break;
      case "build": action.command = { title: fix.title, command: "runic.dependencies.build", arguments: [this.projects.has(document.uri.fsPath) ? document.uri.fsPath : undefined] }; break;
      case "run": action.command = { title: fix.title, command: "runic.dependencies.run", arguments: [fix.cwd, fix.args] }; break;
      case "url": action.command = { title: fix.title, command: "vscode.open", arguments: [vscode.Uri.parse(fix.url)] }; break;
      case "showDependency": action.command = { title: fix.title, command: "runic.dependencies.show", arguments: [fix.label] }; break;
      case "removePackageManager": case "addWorkspaceMember": {
        let text: string;
        try { text = readFileSync(fix.file, "utf8"); } catch { return undefined; }
        const change = fix.kind === "removePackageManager" ? removeJsonProperty(text, "packageManager") : addWorkspaceMember(text, fix.member);
        if (!change) return undefined;
        const uri = vscode.Uri.file(fix.file);
        const position = (offset: number) => { const lines = text.slice(0, offset).split("\n"); return new vscode.Position(lines.length - 1, lines.at(-1)!.length); };
        action.edit = new vscode.WorkspaceEdit();
        action.edit.replace(uri, new vscode.Range(position(change.start), position(change.end)), change.text);
        break;
      }
    }
    return action;
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

  /**
   * The Problems of every project's `dev` terminal (dotnet-runic 0.22 and later), as MSBuild's own lines name them, and
   * of a stopped session's `failure` (runic-sdk 0.24, D087), which VS Code's problem matcher drops when the task that ran
   * `dev` ends. When DevKit's terminal read the same `dev`, a problem both name shows once.
   */
  private publishDevProblems() {
    const byFile = new Map<string, vscode.Diagnostic[]>();
    for (const state of this.projects.values()) {
      const problems = uniqueProblems([...state.devProblems?.problems ?? [], ...this.sessionFailure(state)?.diagnostics ?? []]);
      if (!problems.length) continue;
      const version = projectRunicToolVersion(state.project);
      for (const problem of problems) {
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
        const fixes = dependencyFixes(code, diagnostic.message).map(fix => this.dependencyAction(fix, document, diagnostic)).filter((action): action is vscode.CodeAction => !!action);
        if (fixes.length) fixes[0].isPreferred = true;
        actions.push(...fixes);
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
    if (!this.projects.size && this.libraries.length)
      return [node("No Window project", { icon: "info", description: this.libraries.map(project => basename(project)).join(", "),
        tooltip: "dotnet runic dev runs a Window project (RunicApplicationFrontendWindowProject, or an executable). The projects found here are libraries or tests; open the folder of the Window project that references them." })];
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
    if (!session || !isActive(snapshot)) return [...items, ...this.dependencyRows(state)];
    const frontend = session.frontend;
    if (frontend?.url) items.push(node(`Development server: ${frontend.url}`, { project, icon: "globe", description: frontend.developmentServer ?? undefined, command: { title: "Open", command: "vscode.open", arguments: [vscode.Uri.parse(frontend.url)] } }));
    else if (frontend) items.push(node("Frontend: watcher", { project, icon: "eye", description: frontend.outputDirectory }));
    const window = session.window;
    if (window?.owner === "ide") items.push(node("Window: started by the IDE", { project, icon: "debug-alt", description: "F5", command: { title: "Debug the Window", command: "runic.dev.debugWindow", arguments: [project] } }));
    else if (window) items.push(node(window.processId ? `Window: process ${window.processId}` : "Window: not running", { project, icon: "window", description: window.starts ? `${window.starts} start${window.starts === 1 ? "" : "s"}` : undefined }));
    for (const page of session.pages ?? []) if (page.url) items.push(node(`Page: ${page.document}`, { project, icon: "file-code", description: page.url, command: { title: "Open", command: "vscode.open", arguments: [vscode.Uri.parse(page.url)] } }));
    for (const endpoint of session.debugEndpoints ?? []) items.push(node(`Debug endpoint: ${endpoint.engine}`, { project, icon: "debug", description: `${endpoint.protocol} ${endpoint.url}` }));
    if (frontend?.generatedDirectory) items.push(node("Generated clients", { project, icon: "folder", description: vscode.workspace.asRelativePath(frontend.generatedDirectory), command: { title: "Reveal", command: "revealInExplorer", arguments: [vscode.Uri.file(frontend.generatedDirectory)] } }));
    return [...items, ...this.dependencyRows(state)];
  }

  /**
   * The fresh-clone install offer, the workspace's problems the graph shows, and the dependency tree: one row per
   * frontend package with its owner, state and watcher, and a failure's message below it.
   */
  private dependencyRows(state: ProjectState): Node[] {
    const project = state.project;
    const rows: Node[] = [];
    const install = state.install;
    if (install) rows.push(node("Frontend packages: not installed", { project, icon: new vscode.ThemeIcon("package", new vscode.ThemeColor("problemsWarningIcon.foreground")),
      description: install.lockFile ? `${install.packageManager} · Install` : `${install.packageManager} · no lock file`,
      tooltip: install.lockFile ? `Click to run '${installCommand(install.packageManager).join(" ")}' in ${install.root}. dotnet build and F5 also install them.`
        : `${install.root} has no lock file, so the build stops with RUNICBRIDGE026. Run '${install.packageManager} install' there and commit the lock file.`,
      command: install.lockFile ? { title: "Install frontend dependencies", command: "runic.dependencies.install", arguments: [project] } : undefined }));
    const list = state.dependencies;
    const workspace = list?.workspace;
    if (workspace && !workspace.member) rows.push(node("Frontend: not a member of its workspace", { project, icon: new vscode.ThemeIcon("warning", new vscode.ThemeColor("problemsWarningIcon.foreground")),
      description: workspace.root, tooltip: `${workspace.application} is below the workspace at ${workspace.root}, whose workspaces don't include it (RUNICBRIDGE087).`,
      command: { title: "Open package.json", command: "runic.dependencies.openPackage", arguments: [workspace.root, "workspaces"] } }));
    if (workspace && !workspace.lockFile && workspace.root !== workspace.application) rows.push(node("Frontend workspace: no lock file", { project, icon: new vscode.ThemeIcon("warning", new vscode.ThemeColor("problemsWarningIcon.foreground")),
      description: workspace.root, tooltip: `The workspace at ${workspace.root} has no lock file, so its install would not be locked (RUNICBRIDGE026).` }));
    if (!list?.dependencies.length) return rows;
    const unbuilt = list.dependencies.filter(needsBuild).length;
    const source = list.source === "session" ? "from dotnet runic dev" : list.source === "graph" ? "from the last build" : "from the last dev session";
    rows.push(node("Frontend dependencies", { project, icon: "references", contextValue: "runicDependencies", id: `${project}#dependencies`,
      description: `${list.dependencies.length} package${list.dependencies.length === 1 ? "" : "s"}${unbuilt ? `, ${unbuilt} not built` : ""}`,
      tooltip: `The frontend packages ${basename(project)}'s page imports, dependencies first, ${source}. dotnet build builds them.`,
      children: () => list.dependencies.map(dependency => dependencyNode(project, dependency)) }));
    return rows;
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
        state.devProblems = new DevProblems(dirname(project), noWindow, label => this.packageDirectory(state, label));
        for (const line of lines) state.devProblems.add(line);
        this.publishDevProblems();
        this.loadDependencies();
      },
      viewModels: (project: string) => { this.loadSourceMaps(); return this.projects.get(project)?.sourceTree; },
      runDoctor: (project: string) => this.runDoctor(project),
      libraries: () => [...this.libraries],
      /** Run and Debug's `runic` entries for a workspace folder (the dynamic and initial providers). */
      debugConfigurations: (folder: vscode.Uri) => this.debugConfigurations(vscode.workspace.getWorkspaceFolder(folder)),
      /** Run and Debug's dynamic list for a launch scope: a folder, or undefined for the user settings' launch. */
      dynamicDebugConfigurations: (folder?: vscode.Uri) => this.dynamicDebugConfigurations(folder ? vscode.workspace.getWorkspaceFolder(folder) : undefined),
      toolRestoreOffer: () => this.toolRestoreOffer,
      f5Failure: () => this.f5Failure,
      restoreTools: (project: string) => this.restoreTools(project),
      dependencies: (project: string) => { this.loadDependencies(); return this.projects.get(project)?.dependencies; },
      install: (project: string) => this.projects.get(project)?.install,
      dependencyStatus: () => this.dependencyOffer,
      /** The Dev Session view's rows, as label, description and children. */
      devTree: async () => { const flat = (items: Node[]): unknown[] => items.map(item => ({ label: item.label, description: item.description, children: flat(item.children?.() ?? []) })); return flat(await this.devRoots()); },
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

const dependencyIcons: Record<string, vscode.ThemeIcon> = {
  built: new vscode.ThemeIcon("pass", new vscode.ThemeColor("testing.iconPassed")),
  building: new vscode.ThemeIcon("sync~spin"),
  failed: new vscode.ThemeIcon("error", new vscode.ThemeColor("problemsErrorIcon.foreground")),
  "missing-output": new vscode.ThemeIcon("warning", new vscode.ThemeColor("problemsWarningIcon.foreground")),
  source: new vscode.ThemeIcon("file-code"),
  "not-built": new vscode.ThemeIcon("circle-slash"),
};

/** One frontend package of the dependency tree; its row opens its package.json. */
function dependencyNode(project: string, dependency: DevDependency): Node {
  const watch = dependencyWatchLabel(dependency);
  const tooltip = new vscode.MarkdownString();
  tooltip.appendMarkdown(`**${dependencyLabel(dependency)}** (${dependency.kind})`);
  tooltip.appendText(`\n\n${dependency.owner ? `The frontend package of ${dependency.ownerName ?? basename(dependency.owner)} (${dependency.owner})` : "A workspace package that no .NET project owns"}.\n${dependency.directory}`);
  if (dependency.specifier) tooltip.appendText(`\nImported as ${dependency.specifier}`);
  if (dependency.buildCommand) tooltip.appendText(`\nBuild: ${dependency.buildCommand}`);
  if (dependency.watchCommand) tooltip.appendText(`\nWatch: ${dependency.watchCommand}`);
  if (dependency.state === "missing-output") tooltip.appendText(`\nMissing: ${dependency.exports.filter(file => !existsSync(file)).join(", ") || "an exported file"}. Build frontend dependencies builds it.`);
  if (dependency.message) tooltip.appendText(`\n\n${dependency.message}`);
  const item = node(dependencyLabel(dependency), {
    project, icon: dependencyIcons[dependency.state] ?? "circle-outline", tooltip, contextValue: `runicDependency.${dependency.state}`, id: `${project}#dependency:${dependency.directory}`,
    description: [dependency.ownerName, dependencyStateLabel(dependency), watch].filter(Boolean).join(" · "),
    command: { title: "Open package.json", command: "runic.dependencies.openPackage", arguments: [dependency.directory] },
    children: dependency.message ? () => [node(dependency.message!, { project, icon: "error", tooltip: dependency.message! })] : undefined,
  });
  item.packageDirectory = dependency.directory;
  return item;
}

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

  /** The last lines of dev's output, for the message of an early exit. */
  private readonly lastLines: string[] = [];

  constructor(private readonly dotnet: string, private readonly args: string[], private readonly cwd: string, private readonly log: vscode.LogOutputChannel, private readonly onEnd: (terminal: DevTerminal, code: number, lines: string[]) => void,
    private readonly onLine: (line: string) => void = () => {}) {}

  get running(): boolean { return !!this.child && !this.ended; }

  open(): void {
    this.writeEmitter.fire(terminalText(`> ${this.dotnet} ${this.args.join(" ")}\n`));
    const child = spawnDev(this.dotnet, this.args, this.cwd, true);
    this.child = child;
    const write = (data: string) => this.writeEmitter.fire(terminalText(data));
    // Each stream's lines, for the Problems; the two streams interleave whole lines only.
    const output = new LineSplitter(), errors = new LineSplitter();
    const keep = (line: string) => { if (line.trim()) { this.lastLines.push(line.trimEnd()); if (this.lastLines.length > lastLineCount) this.lastLines.shift(); } this.onLine(line); };
    const read = (lines: LineSplitter) => (data: string) => { write(data); for (const line of lines.push(data)) keep(line); };
    child.stdout?.setEncoding("utf8"); child.stdout?.on("data", read(output));
    child.stderr?.setEncoding("utf8"); child.stderr?.on("data", read(errors));
    child.stdout?.on("end", () => { for (const line of output.flush()) keep(line); });
    child.stderr?.on("end", () => { for (const line of errors.flush()) keep(line); });
    child.on("error", error => { write(`\nCould not start ${this.dotnet}: ${error.message}\n`); this.lastLines.push(`Could not start ${this.dotnet}: ${error.message}`); this.end(1); });
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
    // The state first: F5 reads the exit code and lines when VS Code reports the task's end.
    this.onEnd(this, code, [...this.lastLines]);
    this.closeEmitter.fire(code);
  }
}

/** How many of dev's last output lines an early exit's message keeps. */
const lastLineCount = 20;

/** Runs a task and resolves with its process's exit code. */
async function runTaskToEnd(task: vscode.Task, token: vscode.CancellationToken): Promise<number | undefined> {
  const execution = await vscode.tasks.executeTask(task);
  return new Promise((resolveRun, rejectRun) => {
    const done = vscode.tasks.onDidEndTaskProcess(event => { if (event.execution === execution) { done.dispose(); cancel.dispose(); resolveRun(event.exitCode); } });
    const cancel = token.onCancellationRequested(() => { done.dispose(); cancel.dispose(); execution.terminate(); rejectRun(new vscode.CancellationError()); });
  });
}

/**
 * Whether an installed extension contributes the `coreclr` debugger the Window runs under: the C# extension
 * (ms-dotnettools.csharp), or another build of it with the same debug type.
 */
function hasCoreclrDebugger(): boolean {
  return vscode.extensions.all.some(extension => (extension.packageJSON as { contributes?: { debuggers?: { type?: string }[] } } | undefined)?.contributes?.debuggers?.some(debugger_ => debugger_.type === "coreclr"));
}

function readEnvFile(file: string): Record<string, string> {
  try { return parseEnvFile(readFileSync(file, "utf8")); }
  catch (error) { throw new Error(`The configuration's envFile ${file} could not be read: ${errorSummary(error)}`); }
}

/** Runs a command and resolves with its standard output; `allowFailure` resolves with it for a non-zero exit too (JSON envelopes). */
function execText(command: string, args: string[], cwd: string, allowFailure = false): Promise<string> {
  return new Promise((resolveRun, rejectRun) => execFile(command, args, { cwd, maxBuffer: 16 * 1024 * 1024, env: { ...process.env, DOTNET_NOLOGO: "1" } }, (error, stdout, stderr) => {
    if (error && !(allowFailure && stdout.trim())) rejectRun(new Error(`${basename(command)} ${args.slice(0, 2).join(" ")} failed: ${(stderr || stdout || error.message).trim().split("\n").at(-1)}`));
    else resolveRun(stdout);
  }));
}
