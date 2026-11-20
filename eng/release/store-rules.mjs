// The upload rules of both stores that can be checked without a token or a network, run with the stores' own tools
// on the VSIX files CI builds and on the release candidate (release.mjs store-rules, also in the release workflow's
// dry run). eng/release/README.md ("Store rules") lists every rule, the ones only the stores check, and the store
// failures they would have caught.
//
// VS Code Marketplace: @vscode/vsce's own checks, loaded from vscode/node_modules (vscode/bun.lock pins vsce 4.1.1,
// the version the release workflow publishes with), plus the rules `vsce publish --packagePath` applies before it
// uploads, restated from out/publish.js because vsce does not export them.
//
// Visual Studio Marketplace: VsixPublisher has no offline mode (`publish` authenticates first), so
// eng/release/vsixpublisher-check runs VsixPublisher's own `publish` steps up to its first Marketplace request.
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { createRequire } from "node:module";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../..", import.meta.url));
// Microsoft.VSSDK.BuildTools as restored by eng/release/vsixpublisher (the publish job's VsixPublisher).
export const VSIXPUBLISHER = ".cache/nuget/microsoft.vssdk.buildtools/18.11.1098/tools/vssdk/bin/VsixPublisher.exe";
export const VSIXPUBLISHER_CHECK = "eng/release/vsixpublisher-check";
export const PUBLISH_MANIFEST = "visualstudio/publishManifest.json";

// --- VS Code Marketplace (@vscode/vsce) -------------------------------------------------------------------------

const vsceRequire = () => {
  const from = join(root, "vscode/package.json");
  const require = createRequire(from);
  try { require.resolve("@vscode/vsce/package.json"); } catch {
    throw new Error("@vscode/vsce is not installed; run `bun install --frozen-lockfile --ignore-scripts` in vscode first");
  }
  return createRequire(require.resolve("@vscode/vsce/package.json"));
};
// A module of vsce (out/<name>.js) or, with dependency, one of its dependencies (such as semver).
export const vsceModule = (name, { dependency = false } = {}) => vsceRequire()(dependency ? name : `./out/${name}.js`);

// vsce's checks that `vsce package` and `vsce publish --packagePath <vsix> [--pre-release]` apply without a network,
// run on a packaged VSIX (with the package.json the release stamped): [message], empty when they pass.
export async function vscodeStoreProblems(path, { preRelease = false, vsce = vsceModule } = {}) {
  const problems = [];
  const attempt = async (tool, check) => { try { await check(); } catch (error) { problems.push(`${tool}: ${error.message}`); } };
  let vsix;
  // out/zip.js readVSIXPackage (what `vsce publish --packagePath` reads): extension/package.json and the VSIX manifest.
  await attempt("vsce publish", async () => { vsix = await vsce("zip").readVSIXPackage(path); });
  if (!vsix) return problems;
  const { manifest, xmlManifest } = vsix;
  const pack = vsce("package");
  // out/package.js validateManifestForPackaging: engines.vscode, name, version, publisher, pricing,
  // main/browser with activationEvents, @types/vscode not above engines.vscode, no SVG icon, https badges (SVG only
  // from trusted hosts), no `vscode` dependency, extensionKind values, sponsor URL, extensionDependencies/extensionPack IDs.
  await attempt("vsce package", () => pack.validateManifestForPackaging(manifest));
  // out/package.js ManifestProcessor: a pre-release needs engines.vscode >= 1.63.
  await attempt("vsce package", () => new pack.ManifestProcessor(manifest, { preRelease }));
  // ManifestProcessor.onEnd: the refused publisher, and two prompts that abort a non-interactive run.
  if (manifest.publisher === "vscode-samples") problems.push("vsce package: the 'vscode-samples' publisher is not allowed");
  if (!manifest.repository) problems.push("vsce package: package.json has no repository (vsce prompts, which aborts in CI)");
  if (manifest.activationEvents?.includes("*")) problems.push("vsce package: '*' activation (vsce prompts, which aborts in CI)");
  // out/publish.js publish(): --pre-release only for a VSIX packaged as a pre-release.
  const properties = xmlManifest?.PackageManifest?.Metadata?.[0]?.Properties?.[0]?.Property ?? [];
  const packagedPreRelease = properties.some(property => property.$?.Id === "Microsoft.VisualStudio.Code.PreRelease");
  if (preRelease && !packagedPreRelease)
    problems.push("vsce publish: Cannot use '--pre-release' flag with a package that was not packaged as pre-release");
  // out/publish.js validateManifestForPublishing: no proposed API, no semver prerelease version, a valid publisher.
  if (manifest.enableProposedApi) problems.push("vsce publish: Extensions using proposed API (enableProposedApi: true) can't be published to the Marketplace");
  if (manifest.enabledApiProposals?.length)
    problems.push(`vsce publish: Extensions using unallowed proposed API (enabledApiProposals: [${manifest.enabledApiProposals}]) can't be published to the Marketplace`);
  if (vsce("semver", { dependency: true }).prerelease(manifest.version)) problems.push(`vsce publish: The VS Marketplace doesn't support prerelease versions: '${manifest.version}'`);
  await attempt("vsce publish", () => vsce("validation").validatePublisher(manifest.publisher));
  return problems;
}
// --- Visual Studio Marketplace (VsixPublisher) ------------------------------------------------------------------

// Runs VsixPublisher's offline `publish` steps on a VSIX (eng/release/vsixpublisher-check): { status, output }, where
// a failure's status is VsixPublisher's error code (24 for VsixPub0024) and its output VsixPublisher's message.
export function vsixPublisherCheck(path, { vsixPublisher = resolve(root, VSIXPUBLISHER), publishManifest = resolve(root, PUBLISH_MANIFEST),
  uploadPackage, spawn = spawnSync } = {}) {
  if (!existsSync(vsixPublisher)) throw new Error(`${vsixPublisher} does not exist; run \`dotnet restore eng/release/vsixpublisher --locked-mode\` first`);
  const result = spawn("dotnet", ["run", "--project", resolve(root, VSIXPUBLISHER_CHECK), "--configuration", "Release", "-p:RestoreLockedMode=true",
    "--", vsixPublisher, resolve(path), publishManifest, ...(uploadPackage ? [uploadPackage] : [])], { cwd: root, encoding: "utf8" });
  if (result.error) throw new Error(`Could not run ${VSIXPUBLISHER_CHECK}: ${result.error.message}`);
  return { status: result.status, output: `${result.stdout ?? ""}${result.stderr ?? ""}`.trim() };
}

// --- Both ---------------------------------------------------------------------------------------------------------

// The VSIX of one store against that store's offline upload rules; throws with every problem, or returns a summary.
export async function checkStoreRules(store, path, { preRelease = false, vsixPublisher, publishManifest } = {}) {
  if (store === "vscode") {
    const problems = await vscodeStoreProblems(path, { preRelease });
    if (problems.length) throw new Error(`${path} breaks the VS Code Marketplace upload rules: ${problems.join("; ")}`);
    return `${path}: passes vsce's package and publish checks for the VS Code Marketplace${preRelease ? " (pre-release)" : ""}.`;
  }
  if (store === "visualstudio") {
    const { status, output } = vsixPublisherCheck(path, { vsixPublisher, publishManifest });
    if (status !== 0) throw new Error(`${path} breaks the Visual Studio Marketplace upload rules (exit ${status}): ${output}`);
    return output.split("\n").at(-1);
  }
  throw new Error(`Unknown store ${store}`);
}
