/**
 * The extension's identity. 0.20 merged the Translations extension (`runic-artifex.runic-translations`) into the one
 * Runic extension (`runic-artifex.runic`); its settings and commands moved from `runicTranslations.*` to `runic.*`.
 * The former settings are still read when the new ones are not set, and the former command IDs still run, so
 * keybindings and settings of the Translations extension keep working.
 */
export const extensionId = "runic-artifex.runic";
export const formerExtensionId = "runic-artifex.runic-translations";

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
