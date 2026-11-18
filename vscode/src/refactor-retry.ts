/**
 * A code action whose resolve the server rejects as outdated (runic-editors#32). The server builds a refactoring's edit
 * on `codeAction/resolve` and answers ContentModified when any revision arrives while the resolve is pending, such as
 * the `workspace/didChangeWatchedFiles` of files saved just before (#9). vscode-languageclient turns that answer into a
 * CancellationError, and VS Code then applies nothing, without a message. The resolve is run once more, after a short
 * pause for a burst of watched-file events; if the server still rejects it, the user is told why.
 *
 * A resolve the server refuses for another reason (a `RequestFailed` answer such as Extract Group from a catalog where
 * a locale lacks the group) also applied nothing silently: vscode-languageclient only logs it. It is not retried; the
 * user is shown the server's reason (runic-editors#38). A cancellation by the user or the editor stays silent.
 */

/** LSP ContentModified. */
export const contentModified = -32801;

export interface OutdatedResolveHost {
  /** Whether the error is the server's outdated answer (and not a cancellation by the user or the editor). */
  outdated(error: unknown): boolean;
  /** Whether the error is a cancellation by the user or the editor, which is rethrown without a message. */
  cancelled?(error: unknown): boolean;
  /** Shows the message to the user (a warning) and logs it. */
  warn(message: string): void;
  /** Shows a refusal's message (a warning) and logs it; `warn` when not given. */
  refused?(message: string): void;
  /** The pause before the second resolve, in milliseconds. */
  pause?: number;
}

/** The warning for a code action that stayed outdated. */
export function outdatedActionMessage(title: string): string {
  return `Runic: "${title}" was not applied because the files changed while the language server prepared it. Run it again.`;
}

/** The warning for a code action the server refused, with the server's reason. */
export function refusedActionMessage(title: string, error: unknown): string {
  const reason = (error instanceof Error ? error.message : typeof error === "object" && error !== null && "message" in error ? String(error.message) : String(error)).trim();
  return `Runic: "${title}" was not applied${reason ? `: ${reason.replace(/^Error: /, "")}` : "."}`;
}

/** Whether an error is a JSON-RPC ContentModified answer (a ResponseError with that code). */
export function isContentModified(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && error.code === contentModified;
}

/**
 * Sends a request that only reads, again after a short pause while the server answers it with ContentModified (a
 * save's watched-file event arrived while it was pending), at most `attempts` times; then the last error is thrown.
 * A palette refactoring asks for the message at the cursor this way, right after the user saved (runic-editors#40).
 */
export async function retryContentModified<T>(request: () => PromiseLike<T>, attempts = 4, pause = 150): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    try { return await request(); }
    catch (error) {
      const modified = isContentModified(error) || /content changed while the request was pending/i.test(error instanceof Error ? error.message : String(error));
      if (!modified || attempt >= attempts) throw error;
      await new Promise(resolveDelay => setTimeout(resolveDelay, pause));
    }
  }
}

/**
 * The code actions of a range, asked for again while there are none, at most `attempts` times. VS Code's listing
 * reads a server's ContentModified answer as no actions, so a refactoring run right after a save would apply nothing
 * (runic-editors#40). Returns the last listing, which may be empty.
 */
export async function untilListed<T>(list: () => PromiseLike<readonly T[] | undefined>, attempts = 6, pause = 250): Promise<readonly T[]> {
  for (let attempt = 1; ; attempt++) {
    const actions = (await list()) ?? [];
    if (actions.length > 0 || attempt >= attempts) return actions;
    await new Promise(resolveDelay => setTimeout(resolveDelay, pause));
  }
}

/**
 * Resolves a code action, a second time when the first answer is outdated. Returns the resolved action, or the
 * unresolved `item` (which applies nothing) after warning when the second answer is outdated too or the server refused
 * it. Only a cancellation is rethrown unchanged.
 */
export async function resolveOutdatedOnce<T extends { title: string }>(item: T, resolve: () => PromiseLike<T | null | undefined>, host: OutdatedResolveHost): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    try { return (await resolve()) ?? item; }
    catch (error) {
      if (host.cancelled?.(error)) throw error;
      if (!host.outdated(error)) { (host.refused ?? host.warn)(refusedActionMessage(item.title, error)); return item; }
      if (attempt >= 2) { host.warn(outdatedActionMessage(item.title)); return item; }
      await new Promise(resolveDelay => setTimeout(resolveDelay, host.pause ?? 250));
    }
  }
}
