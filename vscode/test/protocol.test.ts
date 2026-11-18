import { test, expect } from "bun:test";
import { ServerCommands, advertisedCommands, commandSource, refactoringRoute, renameOffset, runicProtocol, type Refactoring } from "../src/protocol.js";
import { isContentModified, outdatedActionMessage, resolveOutdatedOnce } from "../src/refactor-retry.js";

// The initialize results of Translations 0.13 (no experimental.runic) and 0.14 with the opt-in, as the servers send them.
const commands = ["runic.extractGroup", "runic.inlineResource", "runic.markReviewed", "runic.preview", "runic.renameInput", "runic.renameSlot", "runic.renameResource", "runic.renderPreview"];
const v1 = { capabilities: { renameProvider: true, executeCommandProvider: { commands } }, serverInfo: { name: "Runic RMF2", version: "1" } };
const v2 = { capabilities: { renameProvider: { prepareProvider: true }, executeCommandProvider: { commands },
  experimental: { runic: { protocol: 2, commands, requests: ["runic/documentInfo", "runic/libraryCatalogs", "runic/libraryDocument", "runic/message"] } } },
  serverInfo: { name: "Runic RMF2", version: "0.14.0" } };

test("the protocol is experimental.runic.protocol, and 1 for a server that advertises none", () => {
  expect(runicProtocol(v2)).toBe(2);
  expect(runicProtocol(v1)).toBe(1);
  expect(runicProtocol(undefined)).toBe(1);
  expect(runicProtocol({ capabilities: {} })).toBe(1);
  // A later revision is reported as such; malformed values fall back to protocol 1.
  expect(runicProtocol({ capabilities: { experimental: { runic: { protocol: 3 } } } })).toBe(3);
  for (const protocol of ["2", 0, 1.5, null, -2]) expect(runicProtocol({ capabilities: { experimental: { runic: { protocol } } } })).toBe(1);
  expect(runicProtocol({ capabilities: { experimental: { runic: true } } })).toBe(1);
  expect(runicProtocol({ capabilities: { experimental: "runic" } })).toBe(1);
});

test("advertised commands are the executeCommandProvider's strings", () => {
  expect(advertisedCommands(v1)).toEqual(commands);
  expect(advertisedCommands(v2)).toEqual(commands);
  expect(advertisedCommands({ capabilities: { executeCommandProvider: { commands: ["runic.preview", 3, null] } } })).toEqual(["runic.preview"]);
  expect(advertisedCommands({ capabilities: {} })).toEqual([]);
  expect(advertisedCommands(undefined)).toEqual([]);
});

test("protocol 1 runs the runic.* commands; protocol 2 uses rename and the code action of each kind", () => {
  const all: Refactoring[] = ["renameInput", "renameSlot", "renameResource", "extractGroup", "inlineResource"];
  for (const refactoring of all) expect(refactoringRoute(1, refactoring)).toEqual({ kind: "command", command: `runic.${refactoring}` });
  for (const protocol of [2, 3]) {
    expect(refactoringRoute(protocol, "renameInput")).toEqual({ kind: "rename" });
    expect(refactoringRoute(protocol, "renameSlot")).toEqual({ kind: "rename" });
    expect(refactoringRoute(protocol, "renameResource")).toEqual({ kind: "rename" });
    expect(refactoringRoute(protocol, "extractGroup")).toEqual({ kind: "codeAction", codeActionKind: "refactor.extract", apply: "first" });
    expect(refactoringRoute(protocol, "inlineResource")).toEqual({ kind: "codeAction", codeActionKind: "refactor.inline", apply: "ifSingle" });
  }
});

test("the rename position is the first character of an input or slot name", () => {
  const message = "payment =\n  .input {$count :integer}\n  .input {$counter :string}\n  * {{{#action ref=retry}Retry{/action} {#icon ref=|retry-later|/} {#link ref = terms}x{/link} $count.total {$count}}}";
  const at = (part: "Input" | "Slot", name: string) => { const offset = renameOffset(message, part, name); return offset === undefined ? undefined : message.slice(offset, offset + name.length + 1); };
  expect(at("Input", "count")).toBe("count ");
  expect(renameOffset(message, "Input", "count")).toBe(message.indexOf("$count") + 1);
  expect(at("Input", "counter")).toBe("counter ");
  expect(renameOffset(message, "Input", "missing")).toBeUndefined();
  // A slot is an option value of ref=, quoted or not; `retry` is not `retry-later`.
  expect(renameOffset(message, "Slot", "retry")).toBe(message.indexOf("ref=retry") + 4);
  expect(renameOffset(message, "Slot", "retry-later")).toBe(message.indexOf("|retry-later|") + 1);
  expect(renameOffset(message, "Slot", "terms")).toBe(message.indexOf("terms}"));
  expect(renameOffset("{#a xref=retry}", "Slot", "retry")).toBeUndefined();
  expect(renameOffset("{$x :string ref=$retry}", "Slot", "retry")).toBeUndefined();
  // Names are matched literally.
  expect(renameOffset("{$a.b} {$aXb}", "Input", "a.b")).toBe(2);
  expect(renameOffset("{$aXb}", "Input", "a.b")).toBeUndefined();
});

test("server commands are registered once across clients and route by their source URI", async () => {
  const registered: string[] = [];
  const handlers = new Map<string, (...args: unknown[]) => Promise<unknown>>();
  let disposed = 0;
  const routed: [string, unknown[]][] = [];
  const registry = new ServerCommands((command, run) => {
    if (handlers.has(command)) throw new Error(`command '${command}' already exists`);
    registered.push(command); handlers.set(command, run);
    return { dispose: () => { disposed++; handlers.delete(command); } };
  }, async (command, args) => { routed.push([command, args]); return commandSource(args); });
  // Two workspace folders, each with its own client, advertise the same commands.
  registry.add(advertisedCommands(v1));
  registry.add(advertisedCommands(v2));
  expect(registry.add(["runic.preview", "runic.future"])).toEqual([]);
  expect(registered).toEqual([...commands, "runic.future"]);
  expect(registry.commands).toEqual([...commands, "runic.future"].sort());
  expect(await handlers.get("runic.markReviewed")!("file:///b/de.rmf2", "payment", "de")).toBe("file:///b/de.rmf2");
  expect(routed).toEqual([["runic.markReviewed", ["file:///b/de.rmf2", "payment", "de"]]]);
  registry.dispose();
  expect(disposed).toBe(commands.length + 1);
  expect(registry.commands).toEqual([]);
  // After a dispose (deactivation), registering again starts over without a duplicate.
  registry.add(["runic.preview"]);
  expect(handlers.has("runic.preview")).toBe(true);
});

test("a command another extension registered is reported, and the others are registered", () => {
  const taken = new Set(["runic.preview"]);
  const registry = new ServerCommands(command => {
    if (taken.has(command)) throw new Error(`command '${command}' already exists`);
    taken.add(command);
    return { dispose: () => taken.delete(command) };
  }, async () => undefined);
  expect(registry.add(["runic.preview", "runic.markReviewed"])).toEqual(["runic.preview: command 'runic.preview' already exists"]);
  expect(registry.commands).toEqual(["runic.markReviewed"]);
});

test("a command without a source URI is refused", () => {
  expect(() => commandSource([])).toThrow("names no source document");
  expect(() => commandSource([3])).toThrow("names no source document");
  expect(commandSource(["file:///a/en.rmf2", ["shop"]])).toBe("file:///a/en.rmf2");
});

// A code action resolve the server rejects as outdated (#32): once more, then a warning instead of a silent no-op.
class Outdated extends Error {}
const action = { title: "Inline into ../en.rmf2" };
function host() {
  const warnings: string[] = [];
  return { warnings, outdated: (error: unknown) => error instanceof Outdated, warn: (message: string) => { warnings.push(message); }, pause: 1 };
}

test("an outdated resolve runs once more and its result is used", async () => {
  const h = host(); let calls = 0;
  const resolved = await resolveOutdatedOnce(action, async () => { if (++calls === 1) throw new Outdated(); return { ...action, edit: true }; }, h);
  expect(resolved).toEqual({ ...action, edit: true });
  expect(calls).toBe(2);
  expect(h.warnings).toEqual([]);
});

test("a resolve that stays outdated warns with the action's title and applies nothing", async () => {
  const h = host(); let calls = 0;
  const resolved = await resolveOutdatedOnce(action, async () => { calls++; throw new Outdated(); }, h);
  expect(resolved).toBe(action);
  expect(calls).toBe(2);
  expect(h.warnings).toEqual([outdatedActionMessage(action.title)]);
  expect(h.warnings[0]).toContain('"Inline into ../en.rmf2" was not applied');
});

test("other resolve errors and first-time successes pass through unchanged", async () => {
  const h = host(); let calls = 0;
  const failure = new Error("Use an identifier.");
  await expect(resolveOutdatedOnce(action, async () => { calls++; throw failure; }, h)).rejects.toBe(failure);
  expect(calls).toBe(1);
  expect(await resolveOutdatedOnce(action, async () => undefined, h)).toBe(action);
  expect(h.warnings).toEqual([]);
});

test("ContentModified is the JSON-RPC code -32801", () => {
  expect(isContentModified({ code: -32801, message: "Document content changed while the request was pending." })).toBe(true);
  expect(isContentModified({ code: -32803 })).toBe(false);
  expect(isContentModified(new Error("x"))).toBe(false);
  expect(isContentModified(undefined)).toBe(false);
});
