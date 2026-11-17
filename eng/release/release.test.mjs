import { expect, test } from "bun:test";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { createHash } from "node:crypto";
import { CI_WORKFLOW, LAST_TRANSLATIONS_RELEASE, MARKETPLACE, PUBLISH_MANIFEST, REPOSITORY, STORES, VSCE, VSIX, VSIXPUBLISHER, assertNewVersion, createRelease,
  findCiArtifacts, galleryListing, githubPrerelease, prepare, publishCommand, releaseCheck, releaseNotes, root, scanVsix, selectArtifact, selectCiRun,
  selectStores, sha256Sums, storeCheck, storePackage, storePublish, storeVerify, verify } from "./release.mjs";
import { MAX_KEYWORDS, checkVscodeListing, listingBaseUrls, markdownProblems, pngSize } from "./listing.mjs";
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
      { id: "runic-devkit", version: "0.12.1000", publisher: "runic-artifex" },
      { id: "RunicArtifex.DevKit.VisualStudio", version: "0.12.0.1000", publisher: "Runic Artifex" }]);
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

test("both extensions are Runic DevKit, with the store identities D070.5 fixed", () => {
  // Store identities are permanent once published: change them only with a new decision record.
  expect(VSIX.map(({ file, id, publisher }) => [file, id, publisher])).toEqual([
    ["runic-devkit.vsix", "runic-devkit", "runic-artifex"],
    ["runic-devkit-vs.vsix", "RunicArtifex.DevKit.VisualStudio", "Runic Artifex"]]);
  expect(MARKETPLACE).toEqual({ publisher: "runic-artifex", vscode: { itemName: "runic-artifex.runic-devkit" },
    visualStudio: { internalName: "runic-devkit-vs", itemName: "runic-artifex.runic-devkit-vs" } });
  expect(`${MARKETPLACE.publisher}.${VSIX[0].id}`).toBe(MARKETPLACE.vscode.itemName);
  expect(`${MARKETPLACE.publisher}.${MARKETPLACE.visualStudio.internalName}`).toBe(MARKETPLACE.visualStudio.itemName);
  const vscode = JSON.parse(readFileSync(new URL("../../vscode/package.json", import.meta.url), "utf8"));
  expect(`${vscode.publisher}.${vscode.name}`).toBe(MARKETPLACE.vscode.itemName);
  expect(vscode.displayName).toBe("Runic DevKit");
  const manifest = readFileSync(new URL("../../visualstudio/source.extension.vsixmanifest", import.meta.url), "utf8");
  expect(manifest).toContain(`<Identity Id="${VSIX[1].id}"`);
  expect(manifest).toContain("<DisplayName>Runic DevKit</DisplayName>");
  const ci = workflow("ci.yml").jobs;
  expect(ci.vscode.steps.find(step => step.with?.name === "vscode-vsix").with.path).toBe(`vscode/artifacts/${VSIX[0].file}`);
  expect(ci.visualstudio.steps.find(step => step.with?.name === "visualstudio-vsix").with.path).toBe(`visualstudio/artifacts/${VSIX[1].file}`);
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
const files = ["a/vscode-vsix/runic-devkit.vsix", "a/visualstudio-vsix/runic-devkit-vs.vsix", "r/SHA256SUMS"];

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
  ({ spawn, calls } = fakeGh({ release: true, tag: sha, assets: ["runic-devkit.vsix"] }));
  expect(createRelease("1.2.3", sha, files, spawn)).toContain("kept unchanged without runic-devkit-vs.vsix, SHA256SUMS");
  expect(writes(calls)).toBe(false);
  ({ spawn, calls } = fakeGh({ release: true, draft: true, assets: ["runic-devkit.vsix"] }));
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
  for (const permission of ["attestations", "contents"])
    expect(Object.entries(release.jobs).filter(([, job]) => job.permissions?.[permission] === "write").map(([name]) => name)).toEqual(["publish"]);
  expect(Object.entries(release.jobs).filter(([, job]) => job.permissions?.["id-token"] === "write").map(([name]) => name))
    .toEqual(["publish", "marketplace-vscode", "marketplace-visualstudio"]);
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

// --- Stores ---------------------------------------------------------------------------------------------------------

const GALLERY = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";
const hash = bytes => createHash("sha256").update(bytes).digest("hex");
// A fake Marketplace: `listed` maps a store to its versions ({ version, preRelease, bytes }), or to undefined.
const fakeStores = (listed = {}, { publisher = "runic-artifex", status = 200 } = {}) => {
  const queries = [];
  const fetchImpl = async (url, init) => {
    if (url !== GALLERY) {
      const [name, version] = new URL(url).pathname.split("/").slice(-2);
      return new Response(listed[name].find(entry => entry.version === version).bytes);
    }
    expect(init.method).toBe("POST");
    const body = JSON.parse(init.body);
    queries.push(body);
    if (status !== 200) return new Response("", { status });
    const [itemName, target] = body.filters[0].criteria.map(criterion => criterion.value);
    const name = Object.keys(STORES).find(key => STORES[key].itemName === itemName && STORES[key].target === target);
    const versions = listed[name];
    const extensions = versions ? [{ publisher: { publisherName: publisher }, extensionName: itemName.split(".")[1], versions: versions.map(entry => ({
      version: entry.version, properties: entry.preRelease ? [{ key: "Microsoft.VisualStudio.Code.PreRelease", value: "true" }] : [],
      files: [{ assetType: STORES[name].asset, source: `https://cdn.example/${name}/${entry.version}` }] })) }] : [];
    return Response.json({ results: [{ extensions }] });
  };
  return { fetchImpl, queries };
};
const withVsix = async (version, body) => {
  const directory = mkdtempSync(join(tmpdir(), "runic-editors-stores-"));
  try {
    writeVsixFixtures(directory, version);
    const bytes = Object.fromEntries(Object.entries(STORES).map(([name, store]) => [name, readFileSync(join(directory, store.vsix.artifact, store.vsix.file))]));
    await body(directory, bytes);
  } finally { rmSync(directory, { recursive: true, force: true }); }
};

test("the stores are the VS Code and Visual Studio Marketplaces; Visual Studio gets final versions only", () => {
  expect(Object.keys(STORES)).toEqual(["vscode", "visualstudio"]);
  expect(STORES.vscode.itemName).toBe(MARKETPLACE.vscode.itemName);
  expect(STORES.visualstudio.itemName).toBe(MARKETPLACE.visualStudio.itemName);
  expect(selectStores("all", "0.22.0")).toEqual({ stores: ["vscode", "visualstudio"], skipped: [] });
  expect(selectStores("all", "1.0.0-rc.1")).toEqual({ stores: ["vscode"], skipped: ["visualstudio"] });
  expect(selectStores("visualstudio", "1.0.0-rc.1")).toEqual({ stores: [], skipped: ["visualstudio"] });
  expect(selectStores("none", "0.22.0")).toEqual({ stores: [], skipped: [] });
  expect(() => selectStores("openvsx", "0.22.0")).toThrow("Unknown store openvsx");
});

test("store-check queries the public gallery by item name and store and reads versions and pre-release flags", async () => {
  const { fetchImpl, queries } = fakeStores({ vscode: [{ version: "1.0.501", preRelease: true }, { version: "0.22.1000" }] });
  expect(await galleryListing("vscode", fetchImpl)).toEqual({ itemName: "runic-artifex.runic-devkit", versions: [
    { version: "1.0.501", preRelease: true, download: "https://cdn.example/vscode/1.0.501" },
    { version: "0.22.1000", preRelease: false, download: "https://cdn.example/vscode/0.22.1000" }] });
  expect(await galleryListing("visualstudio", fetchImpl)).toBeUndefined();
  expect(queries.map(query => query.filters[0].criteria)).toEqual([
    [{ filterType: 7, value: "runic-artifex.runic-devkit" }, { filterType: 8, value: "Microsoft.VisualStudio.Code" }],
    [{ filterType: 7, value: "runic-artifex.runic-devkit-vs" }, { filterType: 8, value: "Microsoft.VisualStudio.Ide" }]]);
  await expect(galleryListing("vscode", fakeStores({ vscode: [] }, { publisher: "someone" }).fetchImpl)).rejects.toThrow("answered for someone.runic-devkit");
  await expect(galleryListing("vscode", fakeStores({}, { status: 503 }).fetchImpl)).rejects.toThrow("failed: 503");
});

test("store-check publishes a new version, skips the same bytes and refuses other bytes, flags or a lower version", async () => {
  await withVsix("0.22.0", async (vsix, bytes) => {
    const check = (listed, name = "vscode") => storeCheck(name, vsix, "0.22.0", { fetchImpl: fakeStores(listed).fetchImpl });
    expect(await check({})).toMatchObject({ action: "publish", message: expect.stringContaining("not listed yet") });
    expect(await check({ visualstudio: [] }, "visualstudio")).toMatchObject({ action: "publish" });
    expect(await check({ vscode: [{ version: "0.21.1000", bytes: "old" }] })).toMatchObject({ action: "publish", message: expect.stringContaining("newest 0.21.1000") });
    expect(await check({ vscode: [{ version: "0.22.1000", bytes: bytes.vscode }] })).toMatchObject({ action: "skip" });
    expect(await check({ visualstudio: [{ version: "0.22.0.1000", bytes: bytes.visualstudio }] }, "visualstudio")).toMatchObject({ action: "skip" });
    await expect(check({ vscode: [{ version: "0.22.1000", bytes: "other" }] })).rejects.toThrow("with other bytes");
    await expect(check({ visualstudio: [{ version: "0.22.0.1000", bytes: "other" }] }, "visualstudio")).rejects.toThrow("with other bytes");
    await expect(check({ vscode: [{ version: "0.22.1000", preRelease: true, bytes: bytes.vscode }] })).rejects.toThrow("as a pre-release");
    await expect(check({ vscode: [{ version: "0.23.1000", bytes: "newer" }] })).rejects.toThrow("already lists runic-artifex.runic-devkit 0.23.1000, above 0.22.1000");
  });
  await withVsix("1.0.0-rc.2", async (vsix, bytes) => {
    const fetchImpl = fakeStores({ vscode: [{ version: "1.0.502", bytes: bytes.vscode }] }).fetchImpl;
    await expect(storeCheck("vscode", vsix, "1.0.0-rc.2", { fetchImpl })).rejects.toThrow("not as a pre-release");
  });
});

test("the Visual Studio store identity comes from publishManifest.json and must match MARKETPLACE", async () => {
  await withVsix("0.22.0", async vsix => {
    expect(storePackage("visualstudio", vsix, "0.22.0")).toMatchObject({ version: "0.22.0.1000", preRelease: false });
    const directory = mkdtempSync(join(tmpdir(), "runic-editors-manifest-"));
    try {
      const manifest = join(directory, "publishManifest.json");
      writeFileSync(manifest, JSON.stringify({ publisher: "runic-artifex", identity: { internalName: "runic-mf2-translations" } }));
      expect(() => storePackage("visualstudio", vsix, "0.22.0", manifest)).toThrow("not runic-artifex.runic-devkit-vs");
    } finally { rmSync(directory, { recursive: true, force: true }); }
    expect(() => storePackage("vscode", vsix, "0.23.0")).toThrow("declares version 0.22.1000, not 0.23.1000");
  });
});

test("store-publish prints the exact trusted-publishing commands and runs them only outside a dry run", async () => {
  await withVsix("1.0.0-rc.2", async vsix => {
    const local = storePackage("vscode", vsix, "1.0.0-rc.2");
    expect(publishCommand("vscode", local)).toEqual(["node", resolve(root, VSCE), "publish", "--packagePath", local.path, "--oidc", "--skip-duplicate", "--pre-release"]);
    const log = [], calls = [];
    const spawn = (...args) => { calls.push(args); return { status: 0 }; };
    expect(await storePublish("all", vsix, "1.0.0-rc.2", { fetchImpl: fakeStores().fetchImpl, spawn, log: line => log.push(line) })).toEqual(["vscode"]);
    expect(calls).toEqual([]);
    expect(log[0]).toContain("Visual Studio Marketplace: skipped, it gets final versions only");
    expect(log.at(-1)).toBe(`Dry run, would run: node ${resolve(root, VSCE)} publish --packagePath ${local.path} --oidc --skip-duplicate --pre-release`);
  });
  await withVsix("0.22.0", async (vsix, bytes) => {
    const vs = storePackage("visualstudio", vsix, "0.22.0");
    expect(publishCommand("visualstudio", vs)).toEqual([resolve(root, VSIXPUBLISHER), "publish", "-payload", vs.path, "-publishManifest", resolve(root, PUBLISH_MANIFEST), "-oidc"]);
    expect(publishCommand("vscode", storePackage("vscode", vsix, "0.22.0"))).not.toContain("--pre-release");
    const calls = [];
    const spawn = (command, args, options) => { calls.push([command, ...args, options.env]); return { status: 0 }; };
    const env = { PATH: "/bin", VSCE_PAT: "secret", ACTIONS_ID_TOKEN_REQUEST_URL: "https://token" };
    await storePublish("vscode", vsix, "0.22.0", { dryRun: false, fetchImpl: fakeStores().fetchImpl, spawn, env, log: () => {} });
    expect(calls).toHaveLength(1);
    expect(calls[0].slice(0, 4)).toEqual(["node", resolve(root, VSCE), "publish", "--packagePath"]);
    expect(calls[0].at(-1)).toEqual({ PATH: "/bin", ACTIONS_ID_TOKEN_REQUEST_URL: "https://token" });
    // Already listed with these bytes: nothing runs (VsixPublisher would overwrite).
    calls.length = 0;
    await storePublish("all", vsix, "0.22.0", { dryRun: false, spawn, env, log: () => {}, fetchImpl: fakeStores({
      vscode: [{ version: "0.22.1000", bytes: bytes.vscode }], visualstudio: [{ version: "0.22.0.1000", bytes: bytes.visualstudio }] }).fetchImpl });
    expect(calls).toEqual([]);
    await expect(storePublish("visualstudio", vsix, "0.22.0", { dryRun: false, spawn, env, log: () => {}, fetchImpl: fakeStores().fetchImpl,
      vsixPublisher: join(vsix, "missing", "VsixPublisher.exe") })).rejects.toThrow("VsixPublisher.exe not found");
    await expect(storePublish("vscode", vsix, "0.22.0", { dryRun: false, env, log: () => {}, fetchImpl: fakeStores().fetchImpl,
      spawn: () => ({ status: 1 }) })).rejects.toThrow("VS Code Marketplace publish failed (exit 1)");
  });
});

test("store-verify waits until each store lists the release's bytes with the right flag", async () => {
  await withVsix("0.22.0", async (vsix, bytes) => {
    let polls = 0;
    const listed = fakeStores({ vscode: [{ version: "0.22.1000", bytes: bytes.vscode }] }).fetchImpl;
    const fetchImpl = async (url, init) => (url === GALLERY && ++polls < 3 ? Response.json({ results: [{ extensions: [] }] }) : listed(url, init));
    const sleeps = [];
    await storeVerify(["vscode"], vsix, "0.22.0", { fetchImpl, waitSeconds: 90, sleep: async seconds => sleeps.push(seconds), log: () => {} });
    expect(sleeps).toEqual([30, 30]);
    await expect(storeVerify(["vscode"], vsix, "0.22.0", { fetchImpl: fakeStores().fetchImpl, waitSeconds: 60, sleep: async () => {}, log: () => {} }))
      .rejects.toThrow("does not list runic-artifex.runic-devkit 0.22.1000 after 60 s");
    await expect(storeVerify(["visualstudio"], vsix, "0.22.0", { log: () => {},
      fetchImpl: fakeStores({ visualstudio: [{ version: "0.22.0.1000", bytes: "other" }] }).fetchImpl })).rejects.toThrow("serves other bytes");
    expect(hash(bytes.vscode)).toBe(storePackage("vscode", vsix, "0.22.0").sha256);
  });
});

test("the listing files agree with the store identities and the store limits", () => {
  const read = path => readFileSync(new URL(`../../${path}`, import.meta.url));
  const publish = JSON.parse(read(PUBLISH_MANIFEST));
  expect(`${publish.publisher}.${publish.identity.internalName}`).toBe(MARKETPLACE.visualStudio.itemName);
  expect(publish).toMatchObject({ publisher: MARKETPLACE.publisher, overview: "overview.md", qna: false, priceCategory: "free", private: false,
    repo: `https://github.com/${REPOSITORY}` });
  expect(publish.categories.length).toBeGreaterThanOrEqual(1);
  expect(publish.categories.length).toBeLessThanOrEqual(3);
  expect(markdownProblems(read("visualstudio/overview.md").toString(), "overview.md")).toEqual([]);
  const vscode = JSON.parse(read("vscode/package.json"));
  expect(vscode).toMatchObject({ icon: "media/icon.png", qna: false, bugs: { url: `https://github.com/${REPOSITORY}/issues` }, preview: true });
  expect(vscode.keywords.length).toBeLessThanOrEqual(MAX_KEYWORDS);
  expect(vscode.devDependencies["@vscode/vsce"]).toBe("4.1.1");
  expect(vscode.devDependencies.ovsx).toBeUndefined();
  const manifest = read("visualstudio/source.extension.vsixmanifest").toString();
  for (const element of ["<Icon>media\\icon.png</Icon>", "<PreviewImage>media\\preview.png</PreviewImage>", "<Preview>true</Preview>"]) expect(manifest).toContain(element);
  expect([pngSize(read("vscode/media/icon.png")), pngSize(read("visualstudio/media/icon.png")), pngSize(read("visualstudio/media/preview.png"))])
    .toEqual([{ width: 256, height: 256 }, { width: 32, height: 32 }, { width: 200, height: 200 }]);
  const tool = read("eng/release/vsixpublisher/VsixPublisher.csproj").toString();
  expect(VSIXPUBLISHER).toContain(`/microsoft.vssdk.buildtools/${/Include="Microsoft.VSSDK.BuildTools" Version="([^"]+)"/.exec(tool)[1]}/`);
  expect(JSON.parse(read("eng/release/vsixpublisher/packages.lock.json")).dependencies[".NETFramework,Version=v4.7.2"]["Microsoft.VSSDK.BuildTools"].resolved).toBe("18.12.1606");
});

test("the listing checks reject SVG images, plain http, missing icons and too many keywords", () => {
  expect(listingBaseUrls("0.22.0")).toEqual({ content: "https://github.com/Runic-Artifex/runic-editors/blob/v0.22.0/vscode",
    images: "https://raw.githubusercontent.com/Runic-Artifex/runic-editors/v0.22.0/vscode" });
  expect(markdownProblems("![a](https://x/a.png) [b](https://x) [c](#c)", "R")).toEqual([]);
  expect(markdownProblems('![a](media/a.svg) <img src="https://x/b.SVG?raw=1"> [c](http://x)', "R")).toEqual([
    "R shows the SVG image media/a.svg", "R shows the SVG image https://x/b.SVG?raw=1", "R links to http://x over plain http"]);
  const icon = readFileSync(new URL("../../vscode/media/icon.png", import.meta.url));
  const manifest = { icon: "media/icon.png", keywords: ["runic"], qna: false, homepage: "https://h", bugs: { url: "https://b" }, repository: { url: "https://r" } };
  const listing = { manifest, readme: "# Runic DevKit", changelog: "", icon };
  expect(checkVscodeListing(listing)).toEqual({ width: 256, height: 256 });
  expect(() => checkVscodeListing({ ...listing, icon: undefined })).toThrow("does not contain the icon");
  expect(() => checkVscodeListing({ ...listing, icon: readFileSync(new URL("../../visualstudio/media/icon.png", import.meta.url)) })).toThrow("at least 128 px");
  expect(() => checkVscodeListing({ ...listing, manifest: { ...manifest, keywords: Array(31).fill("k") } })).toThrow("1 to 30 keywords");
  expect(() => checkVscodeListing({ ...listing, manifest: { ...manifest, qna: "marketplace" } })).toThrow("Q&A");
  expect(() => checkVscodeListing({ ...listing, readme: "![x](a.svg)" })).toThrow("SVG image a.svg");
});

test("the store jobs publish the released files from the marketplace environment, never in a dry run", () => {
  const release = workflow("release.yml");
  const raw = readFileSync(new URL("../../.github/workflows/release.yml", import.meta.url), "utf8");
  expect(release.on.workflow_dispatch.inputs.stores).toMatchObject({ type: "choice", default: "all", options: ["all", "vscode", "visualstudio", "none"] });
  // Trusted publishing only: the workflow reads no stored secret, and Open VSX is deferred.
  expect(raw).not.toMatch(/secrets\.|OVSX|ovsx|VSCE_PAT|--pat|personalAccessToken/i);
  const { candidate, "marketplace-vscode": vscode, "marketplace-visualstudio": visualStudio } = release.jobs;
  const dryRun = candidate.steps.find(step => step.run?.includes("store-publish"));
  expect(dryRun.run).toBe('bun eng/release/release.mjs store-publish "$STORES" "$VERSION" artifacts/vsix --dry-run');
  expect(dryRun.env.STORES).toBe("${{ inputs.stores }}");
  for (const [job, store, runner] of [[vscode, "vscode", "ubuntu-24.04"], [visualStudio, "visualstudio", "windows-2025"]]) {
    expect(job.needs).toEqual(["candidate", "publish"]);
    expect(job.environment).toBe("marketplace");
    expect(job["runs-on"]).toBe(runner);
    expect(job.permissions).toEqual({ contents: "read", "id-token": "write", actions: "read" });
    expect(job.if).toContain("!inputs.dry-run");
    expect(job.if).toContain(`inputs.stores == 'all' || inputs.stores == '${store}'`);
    const steps = job.steps;
    const index = predicate => steps.findIndex(predicate);
    const download = steps.find(step => step.with?.["artifact-ids"]);
    expect(download.with).toMatchObject({ "artifact-ids": "${{ needs.candidate.outputs.vsix-artifact-ids }}", path: "artifacts/vsix", "run-id": "${{ needs.candidate.outputs.ci-run-id }}" });
    const publish = index(step => step.run === `bun eng/release/release.mjs store-publish ${store} "$VERSION" artifacts/vsix`);
    expect(publish).toBeGreaterThan(-1);
    expect(index(step => step.run?.includes("sha256sum --check --strict"))).toBeLessThan(index(step => step.run?.includes("release.mjs verify")));
    expect(index(step => step.run?.includes("release.mjs verify"))).toBeLessThan(publish);
    expect(index(step => step.run?.startsWith(`bun eng/release/release.mjs store-verify "$VERSION" artifacts/vsix ${store}`))).toBeGreaterThan(publish);
    for (const step of steps.filter(step => step.uses)) expect(step.uses).toMatch(/@[0-9a-f]{40}$/);
  }
  expect(visualStudio.if).toContain("!contains(inputs.version, '-')");
  expect(visualStudio.steps.find(step => step.name === "Restore VsixPublisher").run).toBe("dotnet restore eng/release/vsixpublisher --locked-mode");
  expect(vscode.steps.find(step => step.name === "Install vsce").run).toBe("bun install --frozen-lockfile --ignore-scripts");
});
