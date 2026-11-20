// Release the two VSIX files of the successful CI push run of a commit (eng/release/README.md).
//   find-ci                                        green ci.yml push run on main for GITHUB_SHA and its VSIX artifacts
//   prepare <vsix> <version> <ci-run> <out>        check both VSIX files; write <out>/candidate.json and <out>/SHA256SUMS
//   verify <vsix> <candidate> <version> <ci-run>   check them again against the candidate inventory
//   release-check <version> [<stores>]             tag and any release must be absent or at this commit; with <stores>
//                                                  a single store, a published release at an ancestor commit is a
//                                                  store-only dispatch instead (read-only; writes mode, release-commit)
//   fetch-release <version> <commit> <vsix> <download> [<expected-sums>]
//                                                  download the published release's VSIX files, check them against its
//                                                  SHA256SUMS and their build-provenance attestations
//   release <version> <vsix> <sha256sums>          create the release, or finish a draft of this commit
//   store-check <version> <vsix> [store...]        is the version listed on the stores, and as these bytes? (read-only)
//   store-publish <stores> <version> <vsix> [--dry-run]
//                                                  check, print and (without --dry-run) run each store's publish command
//   store-verify <version> <vsix> [store...] [--wait <seconds>]
//                                                  after publishing: each store lists these bytes with the right flag
// Adapted from runic-translations-sdk eng/release/ci-artifact.mjs, which released these files with Runic Translations.
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { appendFileSync, copyFileSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { ideVersions, releaseVersion, vsixMetadata } from "./ide-versions.mjs";

export const root = fileURLToPath(new URL("../..", import.meta.url));
export const REPOSITORY = "Runic-Artifex/runic-editors";
export const CI_WORKFLOW = ".github/workflows/ci.yml";
// The workflow whose publish job attests the release files; it runs only from main.
export const RELEASE_WORKFLOW = ".github/workflows/release.yml";
// Explicit .exe on Windows, where the Visual Studio Marketplace job runs fetch-release.
const GH = process.platform === "win32" ? "gh.exe" : "gh";
// The VSIX files CI builds and stamps for a commit; each CI artifact holds one file, released under the same name.
// Both extensions are Runic DevKit since 0.22 (D070.5); until 0.21 they were runic.vsix (runic-artifex.runic) and
// runic-translations-visualstudio.vsix (Runic.Artifex.Translations.Rmf2).
export const VSIX = [
  { artifact: "vscode-vsix", file: "runic-devkit.vsix", id: "runic-devkit", publisher: "runic-artifex", format: "vscode" },
  { artifact: "visualstudio-vsix", file: "runic-devkit-vs.vsix", id: "RunicArtifex.DevKit.VisualStudio",
    publisher: "Runic Artifex", format: "visualStudio" },
];
// The store identities (D070.5, D070.12). They are permanent once published; the marketplace publish jobs and the
// Visual Studio publishManifest.json (identity.internalName) take them from here.
export const MARKETPLACE = {
  publisher: "runic-artifex",
  vscode: { itemName: "runic-artifex.runic-devkit" },
  visualStudio: { internalName: "runic-devkit-vs", itemName: "runic-artifex.runic-devkit-vs" },
};
// The stores the release workflow publishes to (D070.11; Open VSX is deferred). The Visual Studio Marketplace has no
// pre-release channel and VsixPublisher overwrites a listed version, so it gets final versions only.
export const STORES = {
  vscode: { title: "VS Code Marketplace", vsix: VSIX[0], format: "vscode", itemName: MARKETPLACE.vscode.itemName,
    target: "Microsoft.VisualStudio.Code", asset: "Microsoft.VisualStudio.Services.VSIXPackage" },
  visualstudio: { title: "Visual Studio Marketplace", vsix: VSIX[1], format: "visualStudio", itemName: MARKETPLACE.visualStudio.itemName,
    target: "Microsoft.VisualStudio.Ide", asset: "Microsoft.VisualStudio.Ide.Payload", finalOnly: true },
};
export const PUBLISH_MANIFEST = "visualstudio/publishManifest.json";
export const VSCE = "vscode/node_modules/@vscode/vsce/vsce";
// Microsoft.VSSDK.BuildTools as restored by eng/release/vsixpublisher (NuGet.config keeps packages in .cache/nuget).
export const VSIXPUBLISHER = ".cache/nuget/microsoft.vssdk.buildtools/18.11.1098/tools/vssdk/bin/VsixPublisher.exe";
// Runic Translations attached both extensions to its own releases up to this version (the VS Code extension as
// 0.11.1000, the Visual Studio one as 0.11.0.1000), so a release from this repository must map above it.
export const LAST_TRANSLATIONS_RELEASE = "0.11.0";
export const SUMS = "SHA256SUMS";
const SCHEMA = "runic.editors.release-candidate/1";
const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");
const compareVersions = (a, b) => {
  const [x, y] = [a, b].map(version => ideVersions(version).visualStudio.split(".").map(Number));
  return x.map((part, index) => part - y[index]).find(difference => difference !== 0) ?? 0;
};

export function assertNewVersion(version) {
  assert.ok(compareVersions(version, LAST_TRANSLATIONS_RELEASE) > 0,
    `${version} is not above ${LAST_TRANSLATIONS_RELEASE}, the last version Runic Translations released these extensions with; set a higher version in eng/version.json.`);
}

export function selectCiRun(runs, { repository, sha }) {
  const candidates = runs.filter(run => run.head_sha === sha && run.event === "push" && run.head_branch === "main"
    && run.path?.split("@")[0] === CI_WORKFLOW && run.repository?.full_name === repository
    && run.head_repository?.full_name === repository).sort((a, b) => b.id - a.id);
  const green = candidates.find(run => run.status === "completed" && run.conclusion === "success");
  if (green) return green;
  const [latest] = candidates;
  if (!latest) throw new Error(`No push run of ${CI_WORKFLOW} on main exists for ${sha}. Release only a commit on main whose CI succeeded.`);
  if (latest.status !== "completed") throw new Error(`CI for ${sha} is still ${latest.status} (${latest.html_url}). Dispatch again after it succeeds.`);
  throw new Error(`CI for ${sha} concluded ${latest.conclusion} (${latest.html_url}). Rerun its failed jobs; a release needs a successful run.`);
}

export function selectArtifact(artifacts, run, name) {
  const found = artifacts.filter(artifact => artifact.name === name);
  if (found.length !== 1) throw new Error(`CI run ${run.html_url} has no single ${name} artifact.`);
  const [artifact] = found;
  if (artifact.expired) throw new Error(`The ${name} artifact of ${run.html_url} has expired. Rerun all jobs of that CI run to regenerate it.`);
  assert.equal(artifact.workflow_run?.id, run.id, "Artifact belongs to a different run");
  assert.equal(artifact.workflow_run?.head_sha, run.head_sha, "Artifact belongs to a different commit");
  assert.ok(Number.isSafeInteger(artifact.id) && artifact.id > 0, "Artifact has no id");
  return artifact;
}

export async function findCiArtifacts({ repository, sha, token, fetchImpl = fetch }) {
  assert.match(sha, /^[a-f0-9]{40}$/, "Expected a full commit SHA");
  const api = async path => {
    const response = await fetchImpl(`https://api.github.com/repos/${repository}/${path}`, { headers: {
      accept: "application/vnd.github+json", authorization: `Bearer ${token}`, "x-github-api-version": "2022-11-28" } });
    if (!response.ok) throw new Error(`GitHub API ${path.split("?")[0]} failed: ${response.status}`);
    return response.json();
  };
  const query = new URLSearchParams({ head_sha: sha, event: "push", branch: "main", per_page: "100" });
  const run = selectCiRun((await api(`actions/workflows/ci.yml/runs?${query}`)).workflow_runs, { repository, sha });
  const artifacts = (await api(`actions/runs/${run.id}/artifacts?per_page=100`)).artifacts;
  // Download by id: re-uploading under the same name creates a new id, so the bytes downloaded later are exactly
  // the artifacts selected here.
  const vsix = VSIX.map(entry => selectArtifact(artifacts, run, entry.artifact));
  return { runId: String(run.id), runUrl: run.html_url, vsixArtifactIds: vsix.map(entry => entry.id).join(",") };
}

// Exactly the two extensions, one per CI artifact directory, with their identities and the version CI stamped for
// this release (ide-versions.mjs); VS Code is a pre-release exactly for a -preview.N or -rc.N release and, since
// 0.23, never carries the Marketplace "Preview" label (D077.4).
export function scanVsix(directory, version) {
  const versions = ideVersions(version);
  assert.deepEqual(readdirSync(directory).sort(), VSIX.map(entry => entry.artifact).sort(), "VSIX directory must contain exactly the extension artifacts");
  return VSIX.map(({ artifact, file, id, publisher, format }) => {
    assert.deepEqual(readdirSync(join(directory, artifact)), [file], `${artifact} must contain only ${file}`);
    const path = resolve(directory, artifact, file);
    assert.ok(lstatSync(path).isFile(), `${file} must be a regular file`);
    const { packageVersion, packagePreview, preRelease, preview, ...identity } = vsixMetadata(path);
    assert.equal(identity.id, id, `${file} declares extension ${identity.id}`);
    assert.equal(identity.publisher, publisher, `${file} declares publisher ${identity.publisher}`);
    assert.equal(identity.version, versions[format], `${file} declares version ${identity.version}, not ${versions[format]} for ${version}`);
    if (format === "vscode") {
      assert.equal(packageVersion, versions.vscode, `${file} bundles package.json version ${packageVersion}, not ${versions.vscode}`);
      assert.equal(preRelease, versions.vscodePreRelease, `${file} ${preRelease ? "is" : "is not"} marked pre-release`);
      assert.equal(preview, versions.vscodePreview, `${file} ${preview ? "has" : "lacks"} the Marketplace Preview flag`);
      assert.equal(packagePreview, versions.vscodePreview, `${file} bundles package.json preview ${packagePreview}`);
    }
    return { name: artifact, file: `${artifact}/${file}`, sha256: sha256(readFileSync(path)), identity };
  });
}

export const sha256Sums = vsix => vsix.map(entry => `${entry.sha256}  ${basename(entry.file)}\n`).join("");

export function prepare(vsixDirectory, version, source, ciRunId) {
  assert.match(ciRunId ?? "", /^[1-9][0-9]*$/, "Expected a CI run id");
  assertNewVersion(version);
  return { schema: SCHEMA, repository: REPOSITORY, version, source, ciRunId, vsix: scanVsix(vsixDirectory, version) };
}

export function verify(vsixDirectory, candidate, source, version, ciRunId) {
  assert.equal(candidate.schema, SCHEMA, "Not a runic-editors release candidate");
  assert.equal(candidate.repository, REPOSITORY, "Not a runic-editors release candidate");
  assert.equal(candidate.source, source, `Candidate is for ${candidate.source}, not ${source}`);
  assert.equal(candidate.version, version, `Candidate is for version ${candidate.version}, not ${version}`);
  assert.equal(candidate.ciRunId, ciRunId, `Candidate is for CI run ${candidate.ciRunId}, not ${ciRunId}`);
  assert.deepEqual(scanVsix(vsixDirectory, candidate.version), candidate.vsix, "VSIX files differ from the candidate inventory");
}

const ghFailure = (what, result) => new Error(`${what} failed: ${(result.stderr ?? "").trim() || `exit ${result.status ?? "unavailable"}`}`);

// The commit a tag points at, or undefined only when GitHub answers 404 for the tag ref. git/ref/tags/<tag> matches
// exactly that tag; annotated tags are followed to their commit. Any other error fails.
export function tagCommit(tag, spawn = spawnSync) {
  let path = `repos/${REPOSITORY}/git/ref/tags/${tag}`;
  for (let depth = 0; depth < 5; depth++) {
    const result = spawn(GH, ["api", path, "--jq", '.object.type + " " + .object.sha'], { encoding: "utf8" });
    if (result.status !== 0) {
      if (depth === 0 && /HTTP 404/.test(result.stderr ?? "")) return undefined;
      throw ghFailure(`Tag lookup for ${tag}`, result);
    }
    const [type, sha] = result.stdout.trim().split(" ");
    assert.match(sha ?? "", /^[a-f0-9]{40}$/, `Tag ${tag} lookup returned no object`);
    if (type === "commit") return sha;
    assert.equal(type, "tag", `Tag ${tag} points at a ${type}, not a commit`);
    path = `repos/${REPOSITORY}/git/tags/${sha}`;
  }
  throw new Error(`Tag ${tag} nests too many annotated tags`);
}

// The release for a tag (drafts included), or undefined only when gh reports it not found.
function findRelease(tag, spawn) {
  const result = spawn(GH, ["release", "view", tag, "--repo", REPOSITORY, "--json", "isDraft,url,assets,targetCommitish"], { encoding: "utf8" });
  if (result.status === 0) return JSON.parse(result.stdout);
  if (/release not found|HTTP 404/.test(result.stderr ?? "")) return undefined;
  throw ghFailure(`Release lookup for ${tag}`, result);
}

// The release of this exact tag and commit (a draft targeting this commit included), undefined if absent. Fails for
// a release or tag of any other commit and any lookup error.
function existingRelease(version, source, spawn) {
  const tag = `v${version}`;
  const found = findRelease(tag, spawn);
  const tagged = tagCommit(tag, spawn);
  if (tagged !== undefined && tagged !== source) throw new Error(`Tag ${tag} belongs to ${tagged}, not ${source}.`);
  if (!found) return undefined;
  if (found.isDraft) {
    if (found.targetCommitish !== source) throw new Error(`Draft release ${tag} (${found.url}) targets ${found.targetCommitish}, not ${source}.`);
    return found;
  }
  if (tagged === undefined) throw new Error(`GitHub release ${tag} (${found.url}) has no tag; delete it first.`);
  return found;
}

// Every release before 1.0 is a GitHub prerelease, and from 1.0 every version with a prerelease suffix.
export const githubPrerelease = version => /^0\./.test(version) || version.includes("-");

export function releaseCheck(version, source, spawn = spawnSync) {
  assertNewVersion(version);
  const found = existingRelease(version, source, spawn);
  if (found?.isDraft) return `Would resume the draft release v${version} for ${source} (${found.url}).`;
  return found ? `GitHub release v${version} already exists for ${source} (${found.url}); a rerun keeps it.`
    : `Would create the ${githubPrerelease(version) ? "prerelease" : "release"} v${version} at ${source}.`;
}

// Whether `base` is `head` or an ancestor of it, from GitHub's comparison (the workflow checkout is shallow).
export function isAncestor(base, head, spawn = spawnSync) {
  if (base === head) return true;
  const result = spawn(GH, ["api", `repos/${REPOSITORY}/compare/${base}...${head}?per_page=1`, "--jq", ".status"], { encoding: "utf8" });
  if (result.status !== 0) throw ghFailure(`Comparing ${base}...${head}`, result);
  const status = result.stdout.trim();
  assert.ok(["ahead", "behind", "diverged", "identical"].includes(status), `Comparing ${base}...${head} returned ${status || "nothing"}`);
  return status === "ahead" || status === "identical";
}

// What a dispatch does (read-only):
// - "release": the GitHub release v<version> of the dispatched commit, created (or kept, or a draft resumed) from its
//   CI artifacts by the publish job, then the selected stores. `version` must be the one in eng/version.json, and a
//   tag or release of another commit fails (releaseCheck).
// - "store-only": `stores` is a single store and v<version> is a published GitHub release whose tag commit is the
//   dispatched commit or an ancestor of it. No publish job and no new GitHub release: the store job publishes the
//   release's own files (fetchRelease) with the workflow code of the dispatched commit. This retries a store after a
//   fix lands on main; `all` and `none` never take this path.
export function releasePlan(version, stores, source, { spawn = spawnSync, projectVersion = releaseVersion() } = {}) {
  assertNewVersion(version);
  selectStores(stores, version);
  const tag = `v${version}`;
  if (Object.hasOwn(STORES, stores)) {
    const found = findRelease(tag, spawn);
    if (found && !found.isDraft) {
      const tagged = tagCommit(tag, spawn);
      if (tagged === undefined) throw new Error(`GitHub release ${tag} (${found.url}) has no tag; delete it first.`);
      if (!isAncestor(tagged, source, spawn))
        throw new Error(`Tag ${tag} is at ${tagged}, which is not an ancestor of ${source}; a store-only dispatch runs from a main commit that contains the release.`);
      return { mode: "store-only", commit: tagged, message: `Store-only: GitHub release ${tag} (${found.url}) is published at ${tagged}, ${tagged === source ? "the dispatched commit" : `an ancestor of ${source}`}; `
        + `the ${STORES[stores].title} job would publish that release's files, checked against its ${SUMS} and attestations, and no GitHub release is created.` };
    }
    if (found?.isDraft && found.targetCommitish !== source)
      throw new Error(`GitHub release ${tag} (${found.url}) is a draft for ${found.targetCommitish}, not published; a store-only dispatch publishes only the files of a published release.`);
  }
  if (version !== projectVersion) throw new Error(`${version} is not the version in eng/version.json (${projectVersion}); a GitHub release is created only from the commit that sets its version.`);
  return { mode: "release", commit: source, message: releaseCheck(version, source, spawn) };
}

// The sha256 of each VSIX file a SHA256SUMS text lists; it must list exactly the two VSIX files, once each.
export function parseSums(text, what = SUMS) {
  const entries = text.split("\n").filter(line => line.trim()).map(line => {
    const match = /^([0-9a-f]{64}) [ *](\S+)$/.exec(line);
    if (!match) throw new Error(`${what} has a malformed line: ${line}`);
    return [match[2], match[1]];
  });
  const names = entries.map(([name]) => name).sort();
  assert.deepEqual(names, VSIX.map(entry => entry.file).sort(), `${what} must list exactly ${VSIX.map(entry => entry.file).join(" and ")}`);
  return Object.fromEntries(entries);
}

const emptyOrAbsent = directory => !existsSync(directory) || readdirSync(directory).length === 0;

// The files of the published GitHub release v<version> whose tag is at `commit`, for a store job: downloads both VSIX
// files and SHA256SUMS into `downloadDirectory`, checks the files against SHA256SUMS and SHA256SUMS against
// `expectedSums` (the candidate job's), verifies each file's build-provenance attestation (this repository's release
// workflow, run on main at `commit`, on a GitHub-hosted runner), and lays the VSIX files out as
// <vsix>/<artifact>/<file> for store-publish, checking their identities and versions. Fails unless every check passes.
export function fetchRelease(version, commit, vsixDirectory, downloadDirectory, { spawn = spawnSync, expectedSums, log = console.log } = {}) {
  assert.match(commit ?? "", /^[a-f0-9]{40}$/, "Expected the full commit SHA of the release tag");
  const tag = `v${version}`;
  const found = findRelease(tag, spawn);
  if (!found) throw new Error(`GitHub release ${tag} does not exist.`);
  if (found.isDraft) throw new Error(`GitHub release ${tag} (${found.url}) is not published; the stores get only the files of a published release.`);
  const tagged = tagCommit(tag, spawn);
  if (tagged !== commit) throw new Error(`Tag ${tag} is at ${tagged ?? "no commit"}, not ${commit}.`);
  const names = [...VSIX.map(entry => entry.file), SUMS];
  const present = new Set((found.assets ?? []).map(asset => asset.name));
  const missing = names.filter(name => !present.has(name));
  if (missing.length) throw new Error(`GitHub release ${tag} (${found.url}) has no ${missing.join(", ")}.`);
  for (const directory of [downloadDirectory, vsixDirectory]) assert.ok(emptyOrAbsent(directory), `${directory} must be empty`);
  mkdirSync(downloadDirectory, { recursive: true });
  const download = spawn(GH, ["release", "download", tag, "--repo", REPOSITORY, "--dir", downloadDirectory, ...names.flatMap(name => ["--pattern", name])], { encoding: "utf8" });
  if (download.status !== 0) throw ghFailure(`Downloading the files of ${tag}`, download);
  assert.deepEqual(readdirSync(downloadDirectory).sort(), [...names].sort(), `The download of ${tag} must be exactly ${names.join(", ")}`);
  const sums = parseSums(readFileSync(join(downloadDirectory, SUMS), "utf8"), `${SUMS} of ${tag}`);
  for (const { file } of VSIX) {
    const actual = sha256(readFileSync(join(downloadDirectory, file)));
    if (actual !== sums[file]) throw new Error(`${file} of ${tag} has sha256 ${actual}, but its ${SUMS} lists ${sums[file]}.`);
  }
  if (expectedSums !== undefined) {
    const expected = parseSums(expectedSums, "The candidate job's SHA256SUMS");
    for (const { file } of VSIX) if (expected[file] !== sums[file])
      throw new Error(`${file} of ${tag} has sha256 ${sums[file]}, but the candidate job checked ${expected[file]}.`);
  }
  for (const name of names) {
    const result = spawn(GH, ["attestation", "verify", join(downloadDirectory, name), "--repo", REPOSITORY, "--signer-workflow", `${REPOSITORY}/${RELEASE_WORKFLOW}`,
      "--source-ref", "refs/heads/main", "--source-digest", commit, "--deny-self-hosted-runners"], { encoding: "utf8" });
    if (result.status !== 0) throw ghFailure(`Verifying the build-provenance attestation of ${name} of ${tag} (${RELEASE_WORKFLOW} on main at ${commit})`, result);
  }
  for (const { artifact, file } of VSIX) {
    mkdirSync(join(vsixDirectory, artifact), { recursive: true });
    copyFileSync(join(downloadDirectory, file), join(vsixDirectory, artifact, file));
  }
  for (const entry of scanVsix(vsixDirectory, version)) assert.equal(entry.sha256, sums[basename(entry.file)], `${entry.file} changed while copying`);
  log(`GitHub release ${tag} (${found.url}) at ${commit}: ${names.join(", ")} downloaded; the VSIX files match its ${SUMS}`
    + `${expectedSums === undefined ? "" : " and the candidate job's"}, and each file has a build-provenance attestation by ${REPOSITORY}/${RELEASE_WORKFLOW} on main at ${commit}.`);
  for (const { file } of VSIX) log(`${sums[file]}  ${file}`);
  return { tag, url: found.url, sums };
}

// Hand-written notes for a version, if eng/release/notes/<version>.md exists; gh puts them before the generated list.
export function releaseNotes(version, directory = resolve(root, "eng/release/notes")) {
  const path = join(directory, `${version}.md`);
  return existsSync(path) ? path : undefined;
}

// A rerun after a partial run keeps a release of this exact tag and commit, uploads only assets a draft is missing
// and publishes it. A published release is never changed (immutable releases reject new assets).
export function createRelease(version, source, files, spawn = spawnSync, notes = releaseNotes(version)) {
  const tag = `v${version}`;
  const found = existingRelease(version, source, spawn);
  const gh = (args, what) => {
    if (spawn(GH, args, { stdio: "inherit" }).status !== 0) throw new Error(`Could not ${what} GitHub release ${tag}.`);
  };
  const prerelease = githubPrerelease(version);
  if (!found) gh(["release", "create", tag, ...files, "--repo", REPOSITORY, "--target", source, ...(prerelease ? ["--prerelease"] : []), "--generate-notes",
    ...(notes ? ["--notes-file", notes] : [])], "create");
  else {
    const present = new Set((found.assets ?? []).map(asset => asset.name));
    const missing = files.filter(file => !present.has(basename(file)));
    if (!missing.length && !found.isDraft) return `GitHub release ${tag} already exists for ${source} with every asset.`;
    if (!found.isDraft) {
      const names = missing.map(file => basename(file)).join(", ");
      console.log(`::warning title=Published release kept unchanged::GitHub release ${tag} is published without ${names}; published releases are not modified.`);
      return `GitHub release ${tag} already exists for ${source}; kept unchanged without ${names}.`;
    }
    if (missing.length) gh(["release", "upload", tag, ...missing, "--repo", REPOSITORY], "complete");
    gh(["release", "edit", tag, "--repo", REPOSITORY, "--draft=false", `--prerelease=${prerelease}`], "publish");
  }
  return `${found ? "Completed" : "Created"} GitHub release ${tag} at ${source}.`;
}

// --- Stores -----------------------------------------------------------------------------------------------------

const GALLERY = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";
// IncludeVersions | IncludeFiles | IncludeVersionProperties | IncludeAssetUri
const GALLERY_FLAGS = 0x1 | 0x2 | 0x10 | 0x80;
const PRE_RELEASE_PROPERTY = "Microsoft.VisualStudio.Code.PreRelease";
const compareDotted = (a, b) => {
  const [x, y] = [a, b].map(version => version.split(".").map(Number));
  for (let index = 0; index < Math.max(x.length, y.length); index++) {
    const difference = (x[index] ?? 0) - (y[index] ?? 0);
    if (difference) return difference;
  }
  return 0;
};

// The stores a `stores` input selects for a version: all, none or one store name. Visual Studio only for a final version.
export function selectStores(input, version) {
  const names = input === "all" ? Object.keys(STORES) : input === "none" ? [] : [input];
  for (const name of names) assert.ok(STORES[name], `Unknown store ${name}; use all, none, ${Object.keys(STORES).join(" or ")}`);
  const skipped = names.filter(name => STORES[name].finalOnly && version.includes("-"));
  return { stores: names.filter(name => !skipped.includes(name)), skipped };
}

// The store's public listing of our extension (versions with their pre-release flag and download URL), or undefined
// when the store does not list it. Fails when the store answers for another extension.
export async function galleryListing(name, fetchImpl = fetch) {
  const store = STORES[name];
  const response = await fetchImpl(GALLERY, { method: "POST", headers: { "content-type": "application/json",
    accept: "application/json;api-version=7.2-preview.1" }, body: JSON.stringify({ filters: [{ criteria: [
    { filterType: 7, value: store.itemName }, { filterType: 8, value: store.target }], pageSize: 1 }], flags: GALLERY_FLAGS }) });
  if (!response.ok) throw new Error(`${store.title} query for ${store.itemName} failed: ${response.status}`);
  const extensions = (await response.json()).results?.[0]?.extensions;
  assert.ok(Array.isArray(extensions), `${store.title} returned no result list for ${store.itemName}`);
  if (!extensions.length) return undefined;
  assert.equal(extensions.length, 1, `${store.title} returned several extensions for ${store.itemName}`);
  const [extension] = extensions;
  const itemName = `${extension.publisher?.publisherName}.${extension.extensionName}`;
  assert.equal(itemName.toLowerCase(), store.itemName.toLowerCase(), `${store.title} answered for ${itemName}, not ${store.itemName}`);
  return { itemName, versions: (extension.versions ?? []).map(entry => ({ version: entry.version,
    preRelease: (entry.properties ?? []).some(property => property.key === PRE_RELEASE_PROPERTY && property.value === "true"),
    download: entry.files?.find(file => file.assetType === store.asset)?.source })) };
}

async function downloadSha256(url, fetchImpl) {
  assert.ok(url, "The store lists the version without a package download");
  const response = await fetchImpl(url);
  if (!response.ok) throw new Error(`Downloading ${url} failed: ${response.status}`);
  return sha256(Buffer.from(await response.arrayBuffer()));
}

// The file a store publishes, with its identity checked against the store identity: the VS Code VSIX names
// publisher.name itself; the Visual Studio Marketplace takes its identity from publishManifest.json.
export function storePackage(name, vsixDirectory, version, manifestPath = resolve(root, PUBLISH_MANIFEST)) {
  const store = STORES[name];
  const path = resolve(vsixDirectory, store.vsix.artifact, store.vsix.file);
  const metadata = vsixMetadata(path);
  const versions = ideVersions(version);
  assert.equal(metadata.version, versions[store.format], `${store.vsix.file} declares version ${metadata.version}, not ${versions[store.format]}`);
  if (name === "vscode") {
    assert.equal(`${metadata.publisher}.${metadata.id}`, store.itemName, `${store.vsix.file} is ${metadata.publisher}.${metadata.id}, not ${store.itemName}`);
    assert.equal(metadata.preRelease, versions.vscodePreRelease, `${store.vsix.file} pre-release flag differs from ${version}`);
  } else {
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
    assert.equal(`${manifest.publisher}.${manifest.identity?.internalName}`, store.itemName, `${PUBLISH_MANIFEST} names ${manifest.publisher}.${manifest.identity?.internalName}, not ${store.itemName}`);
  }
  return { path, version: versions[store.format], preRelease: name === "vscode" && versions.vscodePreRelease, sha256: sha256(readFileSync(path)) };
}

// Read-only: "publish" when the store does not list this version and it is above every listed one; "skip" when the
// store lists exactly these bytes with the right pre-release flag. Anything else fails.
export async function storeCheck(name, vsixDirectory, version, { fetchImpl = fetch } = {}) {
  const store = STORES[name];
  const local = storePackage(name, vsixDirectory, version);
  const listing = await galleryListing(name, fetchImpl);
  if (!listing) return { action: "publish", message: `${store.title}: ${store.itemName} is not listed yet; ${local.version} would be its first version.` };
  const listed = listing.versions.filter(entry => entry.version === local.version);
  if (listed.length) {
    for (const entry of listed) {
      assert.equal(entry.preRelease, local.preRelease, `${store.title} lists ${local.version} ${entry.preRelease ? "as" : "not as"} a pre-release`);
      const remote = await downloadSha256(entry.download, fetchImpl);
      assert.equal(remote, local.sha256, `${store.title} lists ${local.version} with other bytes (sha256 ${remote}, release ${local.sha256}); a version cannot be published twice.`);
    }
    return { action: "skip", message: `${store.title}: ${store.itemName} ${local.version} is already published with these bytes; skipping.` };
  }
  const newest = listing.versions.map(entry => entry.version).sort(compareDotted).at(-1);
  if (newest && compareDotted(local.version, newest) <= 0) throw new Error(`${store.title} already lists ${store.itemName} ${newest}, above ${local.version}.`);
  return { action: "publish", message: `${store.title}: ${store.itemName} ${local.version} is not listed (newest ${newest ?? "none"}); it would be published.` };
}

// Trusted publishing for VsixPublisher (D072.25). The `-oidc` exchange of VsixPublisher (Microsoft.VSSDK.BuildTools
// 18.11.1098 and 18.12.1606, the same code) POSTs the GitHub OIDC token to _apis/gallery/token without an api-version,
// and the Marketplace refuses it (VsixPub0047, VssVersionNotSpecifiedException). So the Visual Studio job exchanges the token itself, exactly as @vscode/vsce 4.1.1
// does for `vsce publish --oidc` (out/oidc.js), and hands the short-lived credential to VsixPublisher through
// -personalAccessToken, its only token input: its own -oidc path puts the exchanged credential into the same
// VssBasicCredential. Set this to true only together with a Microsoft.VSSDK.BuildTools newer than 18.12.1606 whose -oidc
// is fixed (eng/release/vsixpublisher pins 18.11.1098, because 18.12.1606 also fails every authentication with
// VsixPub0031: its VsixPublisher.exe.config lacks the MSAL binding redirects).
export const VSIXPUBLISHER_NATIVE_OIDC = false;
export const MARKETPLACE_URL = "https://marketplace.visualstudio.com";
// The audience, endpoint, api-version and headers of vsce 4.1.1's exchange.
export const OIDC_AUDIENCE = "marketplace.visualstudio.com";
export const TOKEN_EXCHANGE = `${MARKETPLACE_URL}/_apis/gallery/token?api-version=7.2-preview.1`;
// Stands in for the exchanged token wherever the VsixPublisher command is shown; the token itself is never printed.
export const TOKEN_PLACEHOLDER = "<marketplace-token-from-github-oidc>";

// The exact publish command of a store. Both use GitHub OIDC trusted publishing: vsce and (with nativeOidc)
// VsixPublisher exchange the token themselves; otherwise VsixPublisher gets the token marketplaceToken exchanged.
export function publishCommand(name, local, { vsixPublisher = resolve(root, VSIXPUBLISHER), manifest = resolve(root, PUBLISH_MANIFEST),
  nativeOidc = VSIXPUBLISHER_NATIVE_OIDC, token = TOKEN_PLACEHOLDER } = {}) {
  if (name === "vscode") return ["node", resolve(root, VSCE), "publish", "--packagePath", local.path, "--oidc", "--skip-duplicate",
    ...(local.preRelease ? ["--pre-release"] : [])];
  return [vsixPublisher, "publish", "-payload", local.path, "-publishManifest", manifest, ...(nativeOidc ? ["-oidc"] : ["-personalAccessToken", token])];
}

async function requestJson(operation, url, init, fetchImpl) {
  let response, body;
  try {
    response = await fetchImpl(url, init);
    body = await response.text();
  } catch (error) { throw new Error(`${operation} failed: ${error?.message ?? error}`); }
  if (!response.ok) {
    let details = body.trim().slice(0, 500);
    try {
      const parsed = JSON.parse(body);
      const message = parsed?.message ?? parsed?.error_description ?? parsed?.error;
      if (typeof message === "string" && message) details = message;
    } catch { /* Keep the plain body. */ }
    throw new Error(`${operation} failed with ${response.status}${response.statusText ? ` ${response.statusText}` : ""}${details ? `: ${details}` : "."}`);
  }
  try { return JSON.parse(body); } catch { throw new Error(`${operation} returned no JSON.`); }
}

// GitHub OIDC trusted publishing as vsce does it: the job's GitHub OIDC token for the Marketplace audience, exchanged
// for a short-lived Marketplace credential of the publisher. Masks both tokens in the job log before anything else
// can print them, and returns the credential; nothing stores it.
export async function marketplaceToken({ env = process.env, fetchImpl = fetch, publisher = MARKETPLACE.publisher, log = console.log } = {}) {
  const { ACTIONS_ID_TOKEN_REQUEST_URL: requestUrl, ACTIONS_ID_TOKEN_REQUEST_TOKEN: requestToken } = env;
  if (!requestUrl || !requestToken) throw new Error("GitHub Actions provides no OIDC token request (ACTIONS_ID_TOKEN_REQUEST_URL, ACTIONS_ID_TOKEN_REQUEST_TOKEN); the job needs `permissions: id-token: write`.");
  let tokenUrl;
  try { tokenUrl = new URL(requestUrl); } catch { throw new Error("GitHub Actions provides an invalid ACTIONS_ID_TOKEN_REQUEST_URL."); }
  tokenUrl.searchParams.set("audience", OIDC_AUDIENCE);
  const oidc = (await requestJson("GitHub Actions OIDC token request", tokenUrl.toString(), { method: "GET",
    headers: { Accept: "application/json", Authorization: `Bearer ${requestToken}` } }, fetchImpl))?.value;
  if (typeof oidc !== "string" || !/^\S+$/.test(oidc)) throw new Error("GitHub Actions OIDC token request returned no token.");
  log(`::add-mask::${oidc}`);
  const credential = (await requestJson("Marketplace OIDC token exchange", TOKEN_EXCHANGE, { method: "POST", headers: { Accept: "application/json",
    Authorization: `FederatedToken ${oidc}`, "Content-Type": "application/json", "User-Agent": "release.mjs" },
    body: JSON.stringify({ publisherName: publisher }) }, fetchImpl))?.credential;
  if (typeof credential !== "string" || !/^\S+$/.test(credential)) throw new Error("Marketplace OIDC token exchange returned no credential.");
  log(`::add-mask::${credential}`);
  log(`Exchanged the job's GitHub OIDC token (audience ${OIDC_AUDIENCE}) for a short-lived ${publisher} Marketplace token: POST ${TOKEN_EXCHANGE}.`);
  return credential;
}

// Checks each selected store, prints its exact publish command and runs it unless this is a dry run. A dry run
// contacts only the stores' public read APIs.
export async function storePublish(input, vsixDirectory, version, { dryRun = true, fetchImpl = fetch, spawn = spawnSync, log = console.log,
  env = process.env, vsixPublisher, nativeOidc = VSIXPUBLISHER_NATIVE_OIDC } = {}) {
  const { stores, skipped } = selectStores(input, version);
  for (const name of skipped) log(`${STORES[name].title}: skipped, it gets final versions only (no pre-release channel) and ${version} is not one.`);
  if (!stores.length && !skipped.length) log("No store selected.");
  for (const name of stores) {
    const check = await storeCheck(name, vsixDirectory, version, { fetchImpl });
    log(check.message);
    if (check.action !== "publish") continue;
    const local = storePackage(name, vsixDirectory, version);
    // Shown with TOKEN_PLACEHOLDER: the exchanged token never reaches the log.
    let command = publishCommand(name, local, { vsixPublisher, nativeOidc });
    const shown = command.map(part => (/[\s"']/.test(part) ? JSON.stringify(part) : part)).join(" ");
    const exchange = name === "visualstudio" && !nativeOidc;
    if (dryRun) {
      if (exchange) log(`Dry run, would exchange the job's GitHub OIDC token (audience ${OIDC_AUDIENCE}) for a short-lived Marketplace token at POST ${TOKEN_EXCHANGE} and pass it as ${TOKEN_PLACEHOLDER} (VsixPublisher -oidc fails with VsixPub0047).`);
      log(`Dry run, would run: ${shown}`);
      continue;
    }
    if (name === "visualstudio") assert.ok(existsSync(command[0]), `VsixPublisher.exe not found at ${command[0]}; restore eng/release/vsixpublisher first`);
    // Trusted publishing only: never hand a stored token to the publish tools. VsixPublisher gets the token exchanged
    // here and no means to request another one.
    const { VSCE_PAT: _vscePat, ...childEnv } = env;
    if (exchange) {
      command = publishCommand(name, local, { vsixPublisher, nativeOidc, token: await marketplaceToken({ env, fetchImpl, log }) });
      delete childEnv.ACTIONS_ID_TOKEN_REQUEST_URL;
      delete childEnv.ACTIONS_ID_TOKEN_REQUEST_TOKEN;
    }
    log(`Running: ${shown}`);
    const result = spawn(command[0], command.slice(1), { stdio: "inherit", env: childEnv });
    if (result.status !== 0) throw new Error(`${STORES[name].title} publish failed (exit ${result.status ?? result.error?.message ?? "unavailable"})${exchange
      ? "; VsixPublisher had the Marketplace token exchanged from the job's GitHub OIDC token (eng/release/README.md, \"Publishing to the Visual Studio Marketplace\")" : ""}.`);
  }
  return stores;
}

// After publishing: each store lists the version with the right pre-release flag, and its package download has the
// release's bytes. The stores scan a new version before listing it, so this waits up to `waitSeconds`.
export async function storeVerify(names, vsixDirectory, version, { fetchImpl = fetch, waitSeconds = 0, intervalSeconds = 30,
  sleep = seconds => new Promise(done => setTimeout(done, seconds * 1000)), log = console.log } = {}) {
  for (const name of names) {
    const store = STORES[name];
    const local = storePackage(name, vsixDirectory, version);
    for (let waited = 0; ; waited += intervalSeconds) {
      const entries = (await galleryListing(name, fetchImpl))?.versions.filter(entry => entry.version === local.version) ?? [];
      if (entries.length) {
        for (const entry of entries) {
          assert.equal(entry.preRelease, local.preRelease, `${store.title} lists ${local.version} ${entry.preRelease ? "as" : "not as"} a pre-release`);
          assert.equal(await downloadSha256(entry.download, fetchImpl), local.sha256, `${store.title} serves other bytes for ${local.version} than the release`);
        }
        log(`${store.title}: ${store.itemName} ${local.version} is listed${local.preRelease ? " as a pre-release" : ""} with the release's bytes.`);
        break;
      }
      if (waited >= waitSeconds) throw new Error(`${store.title} does not list ${store.itemName} ${local.version}${waitSeconds ? ` after ${waitSeconds} s` : ""}.`);
      await sleep(intervalSeconds);
    }
  }
}

const head = () => execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim();

async function main([command, ...args]) {
  if (command === "find-ci") {
    const { GITHUB_REPOSITORY: repository, GITHUB_SHA: sha, GH_TOKEN: token, GITHUB_OUTPUT: output } = process.env;
    assert.ok(repository && sha && token && output, "Run in GitHub Actions with GH_TOKEN");
    const found = await findCiArtifacts({ repository, sha, token });
    appendFileSync(output, `run-id=${found.runId}\nvsix-artifact-ids=${found.vsixArtifactIds}\n`);
    console.log(`Reusing VSIX artifacts ${found.vsixArtifactIds} from ${found.runUrl}`);
  } else if (command === "prepare" && args.length === 4) {
    const [vsixDirectory, version, ciRunId, output] = args;
    const candidate = prepare(vsixDirectory, version, head(), ciRunId);
    mkdirSync(output, { recursive: true });
    writeFileSync(join(output, "candidate.json"), `${JSON.stringify(candidate, null, 2)}\n`);
    writeFileSync(join(output, SUMS), sha256Sums(candidate.vsix));
    console.log(`Verified ${candidate.vsix.length} VSIX files for ${version} at ${candidate.source}`);
  } else if (command === "verify" && args.length === 4) {
    const [vsixDirectory, candidate, version, ciRunId] = args;
    verify(vsixDirectory, JSON.parse(readFileSync(candidate, "utf8")), head(), version, ciRunId);
    console.log(`The VSIX files match the candidate inventory for ${version}`);
  } else if (command === "release-check" && args.length === 1) {
    console.log(releaseCheck(args[0], head()));
  } else if (command === "release-check" && args.length === 2) {
    const plan = releasePlan(args[0], args[1], head());
    console.log(plan.message);
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `mode=${plan.mode}\nrelease-commit=${plan.commit}\n`);
  } else if (command === "fetch-release" && (args.length === 4 || args.length === 5)) {
    const [version, commit, vsixDirectory, downloadDirectory, expected] = args;
    fetchRelease(version, commit, vsixDirectory, downloadDirectory, { expectedSums: expected === undefined ? undefined : readFileSync(expected, "utf8") });
  } else if (command === "release" && args.length === 3) {
    const [version, vsixDirectory, sums] = args;
    const files = [...VSIX.map(entry => join(vsixDirectory, entry.artifact, entry.file)), sums];
    for (const file of files) assert.ok(existsSync(file), `Missing release asset ${file}`);
    console.log(createRelease(version, head(), files));
  } else if (command === "store-check" && args.length >= 2) {
    const [version, vsixDirectory, ...names] = args;
    for (const name of names.length ? names : selectStores("all", version).stores) console.log((await storeCheck(name, vsixDirectory, version)).message);
  } else if (command === "store-publish" && (args.length === 3 || (args.length === 4 && args[3] === "--dry-run"))) {
    const [stores, version, vsixDirectory] = args;
    await storePublish(stores, vsixDirectory, version, { dryRun: args[3] === "--dry-run", vsixPublisher: process.env.VSIXPUBLISHER || undefined });
  } else if (command === "store-verify" && args.length >= 2) {
    const [version, vsixDirectory, ...rest] = args;
    const wait = rest.indexOf("--wait");
    const waitSeconds = wait < 0 ? 0 : Number(rest.splice(wait, 2)[1]);
    assert.ok(Number.isInteger(waitSeconds) && waitSeconds >= 0, "--wait takes whole seconds");
    await storeVerify(rest.length ? rest : selectStores("all", version).stores, vsixDirectory, version, { waitSeconds });
  } else throw new Error("Use find-ci, prepare <vsix> <version> <ci-run-id> <output>, verify <vsix> <candidate> <version> <ci-run-id>, release-check <version> [<stores>], fetch-release <version> <commit> <vsix> <download> [<expected-sums>], release <version> <vsix> <sha256sums>, store-check <version> <vsix> [store...], store-publish <all|none|store> <version> <vsix> [--dry-run] or store-verify <version> <vsix> [store...] [--wait <seconds>]");
}

if (import.meta.main) main(process.argv.slice(2)).catch(error => {
  console.log(`::error title=Release check failed::${error.message}`);
  process.exitCode = 1;
});
