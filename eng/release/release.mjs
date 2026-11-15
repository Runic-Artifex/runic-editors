// Release the two VSIX files of the successful CI push run of a commit (eng/release/README.md).
//   find-ci                                        green ci.yml push run on main for GITHUB_SHA and its VSIX artifacts
//   prepare <vsix> <version> <ci-run> <out>        check both VSIX files; write <out>/candidate.json and <out>/SHA256SUMS
//   verify <vsix> <candidate> <version> <ci-run>   check them again against the candidate inventory
//   release-check <version>                        tag and any release must be absent or at this commit (read-only)
//   release <version> <vsix> <sha256sums>          create the release, or finish a draft of this commit
// Adapted from runic-translations-sdk eng/release/ci-artifact.mjs, which released these files with Runic Translations.
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { appendFileSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { ideVersions, vsixMetadata } from "./ide-versions.mjs";

export const root = fileURLToPath(new URL("../..", import.meta.url));
export const REPOSITORY = "Runic-Artifex/runic-editors";
export const CI_WORKFLOW = ".github/workflows/ci.yml";
// The VSIX files CI builds and stamps for a commit; each CI artifact holds one file, released under the same name.
export const VSIX = [
  { artifact: "vscode-vsix", file: "runic.vsix", id: "runic", publisher: "runic-artifex", format: "vscode" },
  { artifact: "visualstudio-vsix", file: "runic-translations-visualstudio.vsix", id: "Runic.Artifex.Translations.Rmf2",
    publisher: "Runic Artifex", format: "visualStudio" },
];
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
// this release (ide-versions.mjs); VS Code is a pre-release exactly for a -preview.N or -rc.N release and always
// carries the Marketplace "Preview" label (preview until 2.0).
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
    const result = spawn("gh", ["api", path, "--jq", '.object.type + " " + .object.sha'], { encoding: "utf8" });
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
  const result = spawn("gh", ["release", "view", tag, "--repo", REPOSITORY, "--json", "isDraft,url,assets,targetCommitish"], { encoding: "utf8" });
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
    if (spawn("gh", args, { stdio: "inherit" }).status !== 0) throw new Error(`Could not ${what} GitHub release ${tag}.`);
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
  } else if (command === "release" && args.length === 3) {
    const [version, vsixDirectory, sums] = args;
    const files = [...VSIX.map(entry => join(vsixDirectory, entry.artifact, entry.file)), sums];
    for (const file of files) assert.ok(existsSync(file), `Missing release asset ${file}`);
    console.log(createRelease(version, head(), files));
  } else throw new Error("Use find-ci, prepare <vsix> <version> <ci-run-id> <output>, verify <vsix> <candidate> <version> <ci-run-id>, release-check <version>, or release <version> <vsix> <sha256sums>");
}

if (import.meta.main) main(process.argv.slice(2)).catch(error => {
  console.log(`::error title=Release check failed::${error.message}`);
  process.exitCode = 1;
});
