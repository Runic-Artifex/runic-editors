/**
 * The Runic protocol of the Translations language server (`experimental.runic`, D070.1). The extension asks for
 * protocol 2 with `initializationOptions.runicProtocol`; a server that speaks it advertises
 * `capabilities.experimental.runic.protocol` in its initialize result. Translations 0.13 advertises nothing and
 * speaks protocol 1: its `runic.*` refactoring commands return a `WorkspaceEdit` for the client to apply.
 */
export const requestedProtocol = 2;

interface InitializeResultLike { capabilities?: { experimental?: unknown; executeCommandProvider?: { commands?: unknown } } }

/** The protocol the server speaks: `experimental.runic.protocol`, or 1 when the server does not advertise one. */
export function runicProtocol(result: InitializeResultLike | undefined): number {
  const experimental = result?.capabilities?.experimental;
  const runic = experimental && typeof experimental === "object" ? (experimental as { runic?: unknown }).runic : undefined;
  const protocol = runic && typeof runic === "object" ? (runic as { protocol?: unknown }).protocol : undefined;
  return typeof protocol === "number" && Number.isInteger(protocol) && protocol >= 1 ? protocol : 1;
}

/** The commands the server advertises in `executeCommandProvider` (both protocols advertise its `runic.*` commands). */
export function advertisedCommands(result: InitializeResultLike | undefined): string[] {
  const commands = result?.capabilities?.executeCommandProvider?.commands;
  return Array.isArray(commands) ? commands.filter((command): command is string => typeof command === "string") : [];
}

export type Refactoring = "renameInput" | "renameSlot" | "renameResource" | "extractGroup" | "inlineResource";

/**
 * How a palette refactoring runs. Under protocol 2 it takes the standard path: the editor's rename (F2) at the symbol,
 * or the code action of its kind (the light bulb), and the server applies the edit through `workspace/applyEdit`.
 * Under protocol 1 the command asks for its arguments and applies the `WorkspaceEdit` the `runic.*` command returns.
 */
export type RefactoringRoute =
  | { kind: "rename" }
  | { kind: "codeAction"; codeActionKind: "refactor.extract" | "refactor.inline"; apply: "first" | "ifSingle" }
  | { kind: "command"; command: `runic.${Refactoring}` };

export function refactoringRoute(protocol: number, refactoring: Refactoring): RefactoringRoute {
  if (protocol < 2) return { kind: "command", command: `runic.${refactoring}` };
  if (refactoring === "extractGroup") return { kind: "codeAction", codeActionKind: "refactor.extract", apply: "first" };
  // One action per ancestor resource of the same locale: VS Code applies a single candidate and lists several.
  if (refactoring === "inlineResource") return { kind: "codeAction", codeActionKind: "refactor.inline", apply: "ifSingle" };
  return { kind: "rename" };
}

const escape = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
/** MF2 name characters that may follow a name's last character (`$count.total` is another variable). */
const nameContinues = "[\\p{L}\\p{N}_.\\-]";

/**
 * The offset in `text` (a message's source) where the standard rename of an input (`$name`) or a slot reference
 * (`ref=name`, `ref=|name|`) starts: the first character of the name, which is where the server's prepareRename
 * finds the symbol. Undefined when the message does not mention it.
 */
export function renameOffset(text: string, part: "Input" | "Slot", name: string): number | undefined {
  const pattern = part === "Input"
    ? new RegExp(`\\$(${escape(name)})(?!${nameContinues})`, "u")
    : new RegExp(`(?<![\\p{L}\\p{N}_.\\-])ref\\s*=\\s*(\\|?)(${escape(name)})\\1(?!${nameContinues})`, "u");
  const match = pattern.exec(text);
  if (!match) return undefined;
  const value = part === "Input" ? match[1] : match[2];
  return match.index + match[0].lastIndexOf(value);
}

export interface DisposableLike { dispose(): void }

/**
 * The server's `runic.*` commands as editor commands, registered once for all workspace folders. vscode-languageclient
 * would register a server's advertised commands for each client, and the second folder's client fails to start on
 * the duplicate; the extension disables that feature and routes each command to the client of the document its first
 * argument names (every `runic.*` command takes the source URI first). Code actions such as "Mark reviewed" run
 * through these.
 */
export class ServerCommands implements DisposableLike {
  private readonly registered = new Map<string, DisposableLike>();
  constructor(
    private readonly registerCommand: (command: string, run: (...args: unknown[]) => Promise<unknown>) => DisposableLike,
    private readonly route: (command: string, args: unknown[]) => Promise<unknown>,
  ) {}
  /**
   * Registers the commands not yet registered. A command another extension already registered (a former identity of
   * this one, identity.ts) is skipped and reported, so the folder's client still starts.
   */
  add(commands: readonly string[]): string[] {
    const failures: string[] = [];
    for (const command of commands) {
      if (this.registered.has(command)) continue;
      try { this.registered.set(command, this.registerCommand(command, (...args) => this.route(command, args))); }
      catch (error) { failures.push(`${command}: ${error instanceof Error ? error.message : String(error)}`); }
    }
    return failures;
  }
  get commands(): string[] { return [...this.registered.keys()].sort(); }
  dispose(): void {
    for (const registration of this.registered.values()) registration.dispose();
    this.registered.clear();
  }
}

/** The source URI a `runic.*` command's arguments start with. */
export function commandSource(args: readonly unknown[]): string {
  const source = args[0];
  if (typeof source !== "string" || source.length === 0) throw new Error("The Runic command names no source document.");
  return source;
}
