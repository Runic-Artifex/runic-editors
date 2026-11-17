/**
 * The extension's identity. 0.20 merged the Translations extension (`runic-artifex.runic-translations`) into the one
 * Runic extension (`runic-artifex.runic`); its settings and commands moved from `runicTranslations.*` to `runic.*`.
 * The former settings are still read when the new ones are not set, and the former command IDs still run, so
 * keybindings and settings of the Translations extension keep working. 0.22 renamed the extension to Runic DevKit
 * (`runic-artifex.runic-devkit`, D070.5) before its first Marketplace listing; command IDs and settings stay.
 */
export const extensionId = "runic-artifex.runic-devkit";

/** The extension's former identities, newest first. Each registers the same `runic.*` commands as this one. */
export const formerExtensions: readonly { id: string; name: string }[] = [
  { id: "runic-artifex.runic", name: "Runic" },
  { id: "runic-artifex.runic-translations", name: "Runic MF2 Translations" },
];

/**
 * The one notice for the former extensions that are still installed, or undefined when none is. While one is
 * installed, both register the same `runic.*` commands and start a language server for the same files.
 */
export function formerExtensionsNotice(installed: (id: string) => boolean): { ids: string[]; message: string; action: string } | undefined {
  const found = formerExtensions.filter(extension => installed(extension.id));
  if (!found.length) return undefined;
  const names = found.map(extension => `${extension.name} (${extension.id})`);
  const list = names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
  return {
    ids: found.map(extension => extension.id),
    message: `Runic DevKit replaces ${list}, ${found.length === 1 ? "which is" : "which are"} still installed. Both register the same Runic commands and language server; uninstall ${found.length === 1 ? "it" : "them"} and reload the window.`,
    action: found.length === 1 ? `Uninstall ${found[0].name}` : "Uninstall both",
  };
}

/** New setting → the Translations extension's setting it replaces. */
export const formerSettings: Readonly<Record<string, string>> = {
  "runic.dotnetPath": "runicTranslations.dotnetPath",
  "runic.translations.serverAssembly": "runicTranslations.serverAssembly",
  "runic.translations.trace.server": "runicTranslations.trace.server",
};

/** The Translations commands; each runs as `runic.translations.<name>` and, for compatibility, as `runicTranslations.<name>`. */
export const translationsCommands = [
  "restart", "showOutput", "preview", "previewSamples", "previewLocale", "previewRequestedLocale", "previewTimeZone",
  "renameInput", "renameSlot", "extractGroup", "inlineResource", "renameResource", "exportXliff", "importXliffDryRun",
  "overrideLibraryMessage", "showStatus", "importCatalog",
] as const;
export const commandId = (name: string) => `runic.translations.${name}`;
export const formerCommandId = (name: string) => `runicTranslations.${name}`;

/** What `WorkspaceConfiguration.inspect` returns, reduced to the scopes a user sets. */
export interface SettingInspection<T> { globalValue?: T; workspaceValue?: T; workspaceFolderValue?: T; defaultValue?: T }
const isSet = <T>(inspection: SettingInspection<T> | undefined) =>
  inspection !== undefined && (inspection.globalValue !== undefined || inspection.workspaceValue !== undefined || inspection.workspaceFolderValue !== undefined);

/**
 * A setting's value: the new setting when it is set in any scope, else the former setting when that is set, else the
 * new setting's default. `inspect(key)` and `get(key)` read the configuration of the resource.
 */
export function settingValue<T>(key: string, fallback: T, read: { inspect(key: string): SettingInspection<T> | undefined; get(key: string): T | undefined }): T {
  const former = formerSettings[key];
  if (!isSet(read.inspect(key)) && former && isSet(read.inspect(former))) return read.get(former) ?? fallback;
  return read.get(key) ?? fallback;
}

/** Whether any former `runicTranslations.*` setting is still set, so the extension can say where it moved. */
export function formerSettingsInUse(inspect: (key: string) => SettingInspection<unknown> | undefined): string[] {
  return Object.values(formerSettings).filter(key => isSet(inspect(key)));
}
