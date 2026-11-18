/**
 * A code action whose resolve the server rejects as outdated (runic-editors#32). The server builds a refactoring's edit
 * on `codeAction/resolve` and answers ContentModified when any revision arrives while the resolve is pending, such as
 * the `workspace/didChangeWatchedFiles` of files saved just before (#9). vscode-languageclient turns that answer into a
 * CancellationError, and VS Code then applies nothing, without a message. The resolve is run once more, after a short
 * pause for a burst of watched-file events; if the server still rejects it, the user is told why.
 */

/** LSP ContentModified. */
export const contentModified = -32801;

export interface OutdatedResolveHost {
  /** Whether the error is the server's outdated answer (and not a cancellation by the user or the editor). */
  outdated(error: unknown): boolean;
  /** Shows the message to the user (a warning) and logs it. */
  warn(message: string): void;
  /** The pause before the second resolve, in milliseconds. */
  pause?: number;
}

/** The warning for a code action that stayed outdated. */
export function outdatedActionMessage(title: string): string {
  return `Runic: "${title}" was not applied because the files changed while the language server prepared it. Run it again.`;
}

/** Whether an error is a JSON-RPC ContentModified answer (a ResponseError with that code). */
export function isContentModified(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && error.code === contentModified;
}

/**
 * Resolves a code action, a second time when the first answer is outdated. Returns the resolved action, or the
 * unresolved `item` after warning when the second answer is outdated too. Other errors are rethrown unchanged.
 */
export async function resolveOutdatedOnce<T extends { title: string }>(item: T, resolve: () => PromiseLike<T | null | undefined>, host: OutdatedResolveHost): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    try { return (await resolve()) ?? item; }
    catch (error) {
      if (!host.outdated(error)) throw error;
      if (attempt >= 2) { host.warn(outdatedActionMessage(item.title)); return item; }
      await new Promise(resolveDelay => setTimeout(resolveDelay, host.pause ?? 250));
    }
  }
}
