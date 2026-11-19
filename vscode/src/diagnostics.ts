/**
 * Runic diagnostic codes from `dotnet runic` (`RAPPCLI`, `RAPPDEV`, `RAPPPKG`, `RCLI`), the Bridge generator and build
 * targets (`RUNICBRIDGE`), and the Vite plugin (`RUNICP`: frontend errors after `dev`'s `[vite] ` label, runic-sdk 0.23).
 * They reach the Problems panel through the `$msCompile`-based problem matchers of the `dev` and build tasks; each has
 * an entry in runic-sdk's diagnostics catalog under its lowercase code.
 */
export const runicCodePattern = /^(?:RUNICBRIDGE|RAPPCLI|RAPPDEV|RAPPPKG|RCLI|RUNICP)\d{3,4}$/;

export function isRunicCode(code: string): boolean {
  return runicCodePattern.test(code);
}

/**
 * The code's entry in docs/diagnostics.md at the release tag of the project's `dotnet-runic` tool (`v<version>`, as the
 * tool's own `helpUri` links), or on `main` when the version is unknown.
 */
export function diagnosticHelpUri(code: string, toolVersion?: string): string {
  const ref = toolVersion && /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(toolVersion) ? `v${toolVersion}` : "main";
  return `https://github.com/Runic-Artifex/runic-sdk/blob/${ref}/docs/diagnostics.md#${code.toLowerCase()}`;
}

/** The README section of a doctor check (its ids are listed under "Doctor JSON output"). */
export function doctorHelpUri(toolVersion?: string): string {
  const ref = toolVersion && /^\d+\.\d+\.\d+/.test(toolVersion) ? `v${toolVersion}` : "main";
  return `https://github.com/Runic-Artifex/runic-sdk/blob/${ref}/tools/dotnet-runic/README.md#doctor-json-output`;
}

/** The diagnostic code as VS Code stores it: a string, a number, or `{ value, target }`. */
export function codeValue(code: string | number | { value: string | number } | undefined): string | undefined {
  if (code === undefined) return undefined;
  return String(typeof code === "object" ? code.value : code);
}
