import { expect, test } from "bun:test";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CI_WORKFLOW, LAST_TRANSLATIONS_RELEASE, REPOSITORY, VSIX, assertNewVersion, createRelease, findCiArtifacts, githubPrerelease, prepare,
  releaseCheck, releaseNotes, scanVsix, selectArtifact, selectCiRun, sha256Sums, verify } from "./release.mjs";
import { writeVsixFixtures } from "./vsix-fixtures.mjs";

const sha = "a".repeat(40);
const run = (id, extra = {}) => ({ id, head_sha: sha, event: "push", head_branch: "main", path: CI_WORKFLOW,
  repository: { full_name: REPOSITORY }, head_repository: { full_name: REPOSITORY }, status: "completed", conclusion: "success",
  html_url: `https://github.com/${REPOSITORY}/actions/runs/${id}`, ...extra });
const workflow = name => Bun.YAML.parse(readFileSync(new URL(`../../.github/workflows/${name}`, import.meta.url), "utf8"));

test("selects the newest successful push run of ci.yml on main for the exact commit", () => {
  expect(selectCiRun([run(1), run(3), run(2, { conclusion: "failure" })], { repository: REPOSITORY, sha }).id).toBe(3);
  for (const other of [{ head_sha: "b".repeat(40) }, { event: "workflow_dispatch" }, { event: "pull_request" }, { event: "merge_group" },
    { head_branch: "feature" }, { path: ".github/workflows/release.yml" }, { head_repository: { full_name: "fork/runic-editors" } }])
    expect(() => selectCiRun([run(9, other)], { repository: REPOSITORY, sha })).toThrow("No push run");
});

test("fails clearly when CI for the commit is missing, running or failed, or an artifact is unusable", () => {
  expect(() => selectCiRun([], { repository: REPOSITORY, sha })).toThrow(`No push run of ${CI_WORKFLOW} on main exists for ${sha}`);
  expect(() => selectCiRun([run(4, { status: "in_progress", conclusion: null })], { repository: REPOSITORY, sha })).toThrow("still in_progress");
  expect(() => selectCiRun([run(5, { conclusion: "failure" })], { repository: REPOSITORY, sha })).toThrow("concluded failure");
  const artifact = { id: 70, name: "vscode-vsix", expired: false, workflow_run: { id: 7, head_sha: sha } };
  expect(selectArtifact([artifact, { ...artifact, id: 71, name: "visualstudio-vsix" }], run(7), "visualstudio-vsix").id).toBe(71);
  expect(() => selectArtifact([artifact], run(7), "visualstudio-vsix")).toThrow("no single visualstudio-vsix artifact");
  expect(() => selectArtifact([artifact, { ...artifact, id: 72 }], run(7), "vscode-vsix")).toThrow("no single");
  expect(() => selectArtifact([{ ...artifact, expired: true }], run(7), "vscode-vsix")).toThrow("expired");
  expect(() => selectArtifact([{ ...artifact, workflow_run: { id: 7, head_sha: "c".repeat(40) } }], run(7), "vscode-vsix")).toThrow();
  expect(() => selectArtifact([{ ...artifact, id: undefined }], run(7), "vscode-vsix")).toThrow("no id");
});

test("queries GitHub for push runs of the commit and returns the VSIX artifact ids in a fixed order", async () => {
  const urls = [];
  const fetchImpl = async url => {
    urls.push(new URL(url));
    const artifacts = VSIX.map(({ artifact }, index) => ({ id: 80 + index, name: artifact, expired: false, workflow_run: { id: 8, head_sha: sha } }));
    return Response.json(url.includes("/artifacts") ? { artifacts: artifacts.reverse() } : { workflow_runs: [run(8)] });
  };
  expect(await findCiArtifacts({ repository: REPOSITORY, sha, token: "t", fetchImpl })).toEqual({ runId: "8", runUrl: run(8).html_url, vsixArtifactIds: "80,81" });
  expect(urls[0].pathname).toBe(`/repos/${REPOSITORY}/actions/workflows/ci.yml/runs`);
  expect(Object.fromEntries(urls[0].searchParams)).toMatchObject({ head_sha: sha, event: "push", branch: "main" });
  await expect(findCiArtifacts({ repository: REPOSITORY, sha, token: "t", fetchImpl: async () => new Response("", { status: 403 }) })).rejects.toThrow("failed: 403");
});

test("each VSIX must carry its identity and the version CI stamps for the release", () => {
  const vsix = mkdtempSync(join(tmpdir(), "runic-editors-vsix-"));
  const check = (version, overrides) => { rmSync(vsix, { recursive: true, force: true }); writeVsixFixtures(vsix, version, overrides); return () => scanVsix(vsix, version); };
  try {
    expect(check("0.12.0")().map(p => p.identity)).toEqual([
      { id: "runic-translations", version: "0.12.1000", publisher: "runic-artifex" },
      { id: "Runic.Artifex.Translations.Rmf2", version: "0.12.0.1000", publisher: "Runic Artifex" }]);
    expect(check("1.0.0-rc.2")().map(p => p.identity.version)).toEqual(["1.0.502", "1.0.0.502"]);
    // The unstamped placeholder of the committed manifests is never released.
    expect(check("0.12.0", { vscode: { version: "0.0.1", packageVersion: "0.0.1" } })).toThrow("declares version 0.0.1, not 0.12.1000");
    expect(check("0.12.0", { visualStudio: { version: "0.0.1" } })).toThrow("declares version 0.0.1, not 0.12.0.1000");
    expect(check("0.12.0", { vscode: { packageVersion: "0.0.1" } })).toThrow("bundles package.json version 0.0.1");
    expect(check("1.0.0-rc.2", { vscode: { preRelease: false } })).toThrow("is not marked pre-release");
    expect(check("0.12.0", { vscode: { preRelease: true } })).toThrow("is marked pre-release");
    expect(check("0.12.0", { vscode: { preview: false } })).toThrow("lacks the Marketplace Preview flag");
    expect(check("0.12.0", { vscode: { packagePreview: false } })).toThrow("bundles package.json preview false");
    expect(check("0.12.0", { visualStudio: { id: "Other" } })).toThrow("declares extension Other");
    expect(check("0.12.0", { vscode: { publisher: "someone" } })).toThrow("declares publisher someone");
    expect(() => scanVsix(vsix, "0.12.0-beta.1")).toThrow("cannot express");
  } finally { rmSync(vsix, { recursive: true, force: true }); }
});

test("the candidate inventory and SHA256SUMS cover exactly the two VSIX files of the version, commit and CI run", () => {
  const vsix = mkdtempSync(join(tmpdir(), "runic-editors-vsix-"));
  try {
    writeVsixFixtures(vsix, "0.12.0");
    const candidate = prepare(vsix, "0.12.0", sha, "42");
    expect(candidate).toMatchObject({ repository: REPOSITORY, version: "0.12.0", source: sha, ciRunId: "42" });
    const sums = sha256Sums(candidate.vsix).trimEnd().split("\n");
    expect(sums.map(line => line.split("  ")[1])).toEqual(VSIX.map(entry => entry.file));
    for (const line of sums) expect(line).toMatch(/^[0-9a-f]{64}  \S+\.vsix$/);
    verify(vsix, candidate, sha, "0.12.0", "42");
    expect(() => verify(vsix, candidate, "b".repeat(40), "0.12.0", "42")).toThrow("Candidate is for");
    expect(() => verify(vsix, candidate, sha, "0.12.0", "43")).toThrow("CI run");
    expect(() => verify(vsix, { ...candidate, vsix: candidate.vsix.map(entry => ({ ...entry, sha256: "0".repeat(64) })) }, sha, "0.12.0", "42")).toThrow("differ");
    expect(() => prepare(vsix, "0.12.0", sha, "run")).toThrow("CI run id");
  } finally { rmSync(vsix, { recursive: true, force: true }); }
});

test("a release must map above the versions Runic Translations already released the extensions with", () => {
  expect(LAST_TRANSLATIONS_RELEASE).toBe("0.11.0");
  for (const version of ["0.8.0", "0.11.0", "0.10.5"]) expect(() => assertNewVersion(version)).toThrow("is not above 0.11.0");
  for (const version of ["0.11.1", "0.12.0", "0.20.0", "1.0.0-rc.1"]) expect(() => assertNewVersion(version)).not.toThrow();
  expect(() => releaseCheck("0.11.0", sha, fakeGh().spawn)).toThrow("is not above 0.11.0");
});

const fakeGh = ({ release = false, tag, draft = false, assets = [], target = sha, annotated = false, tagError = false, releaseError = false } = {}) => {
  const calls = [];
  const spawn = (command, args) => {
    calls.push([command, ...args]);
    if (args[0] === "release" && args[1] === "view") {
      if (releaseError) return { status: 1, stdout: "", stderr: "HTTP 502: Bad Gateway" };
      return release ? { status: 0, stdout: JSON.stringify({ url: "u", isDraft: draft, targetCommitish: target, assets: assets.map(name => ({ name })) }) }
        : { status: 1, stdout: "", stderr: "release not found" };
    }
    if (args[0] === "api") {
      if (tagError) return { status: 1, stdout: "", stderr: "gh: Server Error (HTTP 502)" };
      if (!tag) return { status: 1, stdout: "", stderr: "gh: Not Found (HTTP 404)" };
      if (annotated && args[1].includes("/git/ref/tags/")) return { status: 0, stdout: `tag ${"e".repeat(40)}\n` };
      return { status: 0, stdout: `commit ${tag}\n` };
    }
    return { status: 0 };
  };
  return { spawn, calls };
};
const writes = calls => calls.some(c => c[1] === "release" && ["create", "upload", "edit"].includes(c[2]));
const files = ["a/vscode-vsix/runic-translations.vsix", "a/visualstudio-vsix/runic-translations-visualstudio.vsix", "r/SHA256SUMS"];

test("the release check only reads and accepts only a release of this exact commit", () => {
  const { spawn, calls } = fakeGh();
  expect(releaseCheck("1.2.3", sha, spawn)).toContain("Would create");
  expect(calls.map(c => c.slice(0, 3))).toEqual([["gh", "release", "view"], ["gh", "api", `repos/${REPOSITORY}/git/ref/tags/v1.2.3`]]);
  expect(releaseCheck("1.2.3", sha, fakeGh({ tag: sha }).spawn)).toContain("Would create");
  expect(releaseCheck("1.2.3", sha, fakeGh({ release: true, tag: sha }).spawn)).toContain("already exists for");
  expect(releaseCheck("1.2.3", sha, fakeGh({ release: true, draft: true }).spawn)).toContain("resume the draft");
  expect(() => releaseCheck("1.2.3", sha, fakeGh({ tag: "b".repeat(40) }).spawn)).toThrow("belongs to");
  expect(() => releaseCheck("1.2.3", sha, fakeGh({ release: true, draft: true, target: "main" }).spawn)).toThrow("targets main");
  expect(() => releaseCheck("1.2.3", sha, fakeGh({ release: true }).spawn)).toThrow("no tag");
  for (const [options, message] of [[{ tagError: true }, "Tag lookup for v1.2.3 failed"], [{ releaseError: true }, "Release lookup for v1.2.3 failed"]]) {
    const failing = fakeGh(options);
    expect(() => createRelease("1.2.3", sha, files, failing.spawn)).toThrow(message);
    expect(writes(failing.calls)).toBe(false);
  }
  const annotated = fakeGh({ tag: sha, annotated: true });
  expect(releaseCheck("1.2.3", sha, annotated.spawn)).toContain("Would create");
});

test("a rerun keeps a release of this commit, never modifies a published one and resumes a matching draft", () => {
  let { spawn, calls } = fakeGh();
  expect(createRelease("1.2.3", sha, files, spawn, undefined)).toContain("Created");
  const create = calls.at(-1);
  expect(create.slice(0, 3)).toEqual(["gh", "release", "create"]);
  expect(create.slice(4, 7)).toEqual(files);
  expect(create[create.indexOf("--target") + 1]).toBe(sha);
  expect(create).toContain("--generate-notes");
  ({ spawn, calls } = fakeGh({ release: true, tag: sha, assets: ["runic-translations.vsix"] }));
  expect(createRelease("1.2.3", sha, files, spawn)).toContain("kept unchanged without runic-translations-visualstudio.vsix, SHA256SUMS");
  expect(writes(calls)).toBe(false);
  ({ spawn, calls } = fakeGh({ release: true, draft: true, assets: ["runic-translations.vsix"] }));
  expect(createRelease("1.2.3", sha, files, spawn)).toContain("Completed");
  expect(calls.slice(-2).map(c => c.slice(0, 3))).toEqual([["gh", "release", "upload"], ["gh", "release", "edit"]]);
  expect(calls.at(-1)).toContain("--draft=false");
});

test("GitHub marks 0.y.z and prereleases as prereleases and a stable version from 1.0.0 as a release", () => {
  for (const version of ["0.12.0", "1.0.0-rc.1", "1.1.0-preview.1"]) expect([version, githubPrerelease(version)]).toEqual([version, true]);
  for (const version of ["1.0.0", "1.0.1", "2.0.0"]) expect([version, githubPrerelease(version)]).toEqual([version, false]);
  const { spawn, calls } = fakeGh();
  createRelease("1.0.0", sha, files, spawn, undefined);
  expect(calls.at(-1)).not.toContain("--prerelease");
});

test("hand-written release notes are used only when the version has a notes file", () => {
  const directory = mkdtempSync(join(tmpdir(), "runic-editors-notes-"));
  try {
    writeFileSync(join(directory, "1.2.3.md"), "# Notes\n");
    expect(releaseNotes("1.2.3", directory)).toBe(join(directory, "1.2.3.md"));
    expect(releaseNotes("1.2.4", directory)).toBeUndefined();
    const { spawn, calls } = fakeGh();
    createRelease("1.2.3", sha, files, spawn, join(directory, "1.2.3.md"));
    expect(calls.at(-1)[calls.at(-1).indexOf("--notes-file") + 1]).toBe(join(directory, "1.2.3.md"));
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

test("CI uploads both VSIX artifacts for the release and requires every job in verify", () => {
  const { jobs } = workflow("ci.yml");
  for (const { artifact, file } of VSIX) {
    const upload = Object.values(jobs).flatMap(job => job.steps ?? []).find(step => step.with?.name === artifact);
    expect(upload.with.path.endsWith(`/artifacts/${file}`)).toBe(true);
    expect(upload.with["retention-days"]).toBe(30);
  }
  expect(jobs.verify.needs.sort()).toEqual(Object.keys(jobs).filter(name => name !== "verify").sort());
  expect(jobs.verify.if).toBe("always()");
  // The host test runs the language server of the pinned Translations release from NuGet, never a source build.
  const vscode = jobs.vscode.steps.map(step => step.run ?? "").join("\n");
  expect(vscode).toContain("dotnet tool restore");
  expect(vscode).not.toMatch(/runic-translations-sdk|dotnet build/);
  const visualStudio = jobs.visualstudio.steps.map(step => step.run ?? "");
  expect(visualStudio.findIndex(run => run.startsWith("msbuild visualstudio/"))).toBeLessThan(visualStudio.indexOf("python visualstudio/package.py --configuration Release"));
  expect(visualStudio.join("\n")).not.toContain("RunicIdeReleaseVersion");
});

test("the release is dispatch-only, a dry run by default, and only the publish job can write, attest or release", () => {
  const release = workflow("release.yml");
  expect(Object.keys(release.on)).toEqual(["workflow_dispatch"]);
  expect(release.on.workflow_dispatch.inputs["dry-run"]).toMatchObject({ type: "boolean", default: true });
  expect(release.permissions).toEqual({ contents: "read" });
  const { candidate, publish } = release.jobs;
  expect(candidate.if).toContain("github.ref == 'refs/heads/main'");
  expect(candidate.permissions).toEqual({ contents: "read", actions: "read" });
  expect(candidate.steps.some(step => step.uses?.startsWith("actions/attest") || / release "\$VERSION"/.test(step.run ?? ""))).toBe(false);
  expect(publish.if).toBe("${{ !inputs.dry-run }}");
  expect(publish.permissions).toEqual({ contents: "write", "id-token": "write", attestations: "write", actions: "read" });
  for (const permission of ["id-token", "attestations", "contents"])
    expect(Object.entries(release.jobs).filter(([, job]) => job.permissions?.[permission] === "write").map(([name]) => name)).toEqual(["publish"]);
  // Downloads by id from the CI run the candidate job selected.
  for (const [job, source] of [[candidate, "steps.ci"], [publish, "needs.candidate"]]) {
    const download = job.steps.find(step => step.with?.["artifact-ids"]);
    expect(download.with["artifact-ids"]).toBe(`\${{ ${source}.outputs.vsix-artifact-ids }}`);
    expect(download.with.path).toBe("artifacts/vsix");
  }
  const steps = publish.steps;
  const index = predicate => steps.findIndex(predicate);
  const attest = index(step => step.uses?.startsWith("actions/attest-build-provenance@"));
  expect(steps[attest].uses).toMatch(/@[0-9a-f]{40}$/);
  expect(steps[attest].with["subject-path"].trim().split("\n")).toEqual(["artifacts/vsix/*/*.vsix", "artifacts/release/SHA256SUMS"]);
  expect(index(step => step.run?.includes("sha256sum --check --strict"))).toBeLessThan(index(step => step.run?.includes("release.mjs verify")));
  expect(index(step => step.run?.includes("release.mjs verify"))).toBeLessThan(attest);
  expect(index(step => step.run === 'bun eng/release/release.mjs release "$VERSION" artifacts/vsix artifacts/release/SHA256SUMS')).toBeGreaterThan(attest);
});
