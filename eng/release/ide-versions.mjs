// Maps the release version (eng/version.json) onto the IDE extension version formats.
//   bun eng/release/ide-versions.mjs [version]   print the mapping as JSON (default: eng/version.json)
//
// Moved from runic-translations-sdk (eng/release/ide-versions.mjs), where the extensions were released with
// Runic Translations until 0.11.0; the mapping is unchanged, so versions keep sorting across the move.
//
// Neither format takes a SemVer prerelease label, so a prerelease becomes a number that sorts
// below the final release of the same version and above every earlier release:
//
//   release           Visual Studio (N.N.N.N)   VS Code (x.y.z)        VS Code pre-release flag
//   0.8.0             0.8.0.1000                0.8.1000               no
//   1.0.0-preview.N   1.0.0.N                   1.0.N                  yes
//   1.0.0-rc.N        1.0.0.(500 + N)           1.0.(500 + N)          yes
//   1.0.0             1.0.0.1000                1.0.1000               no
//   1.0.1-rc.N        1.0.1.(500 + N)           1.0.(1500 + N)         yes
//
// Releases before 1.0 are plain x.y.z versions (D026); -preview.N and -rc.N are for previews of a
// coming version from 1.0 on. Visual Studio matches the AnalyzerReleases headers of
// runic-translations-sdk (Runic.Translations.Generator): the fourth component is the preview number, 500 plus
// the release-candidate number, or 1000 for the final release. VS Code folds that fourth component into
// the patch: patch * 1000 + revision. A final x.y.z is therefore a multiple of 1000 and a prerelease
// never is, so a prerelease can never take the number of a later final release. Preview and
// release-candidate numbers run from 1 to 499, so every preview sorts below every release candidate of
// the same version. No other prerelease label is supported.
//
// Visual Studio reads each version component as a 16-bit System.Version part, so major, minor and
// patch are limited to 65534 here, in package.py and in the Visual Studio project. The VS Code patch
// is then at most 65534 * 1000 + 1000 = 65535000, well inside the 32-bit integer the Marketplace
// keeps per component. Since 0.23 no release carries the Marketplace "Preview" label (D070.2, D077.4):
// package.mjs stamps the VS Code "preview" field false, independent of the pre-release flag, and
// release.mjs checks both.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";

const RELEASE = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(preview|rc)\.([1-9]\d?|[1-4]\d{2}))?$/;
const UNSUPPORTED = "is not x.y.z, x.y.z-preview.N or x.y.z-rc.N (1 <= N <= 499); IDE extension versions cannot express it";
export const VSCODE_PRE_RELEASE = "Microsoft.VisualStudio.Code.PreRelease";

export function ideVersions(version) {
  const match = RELEASE.exec(version ?? "");
  assert.ok(match, `${version} ${UNSUPPORTED}`);
  const [major, minor, patch] = match.slice(1, 4).map(Number);
  // Visual Studio parses each component as a 16-bit System.Version part.
  assert.ok([major, minor, patch].every(part => part <= 65534), `${version} has a component above 65534`);
  const revision = !match[4] ? 1000 : Number(match[5]) + (match[4] === "rc" ? 500 : 0);
  const vscodePatch = patch * 1000 + revision;
  assert.ok(vscodePatch <= 2 ** 31 - 1, `${version} maps to VS Code patch ${vscodePatch}, above the 32-bit Marketplace limit`);
  return { release: version, visualStudio: `${major}.${minor}.${patch}.${revision}`,
    vscode: `${major}.${minor}.${vscodePatch}`, vscodePreRelease: Boolean(match[4]), vscodePreview: false };
}

export const releaseVersion = () => JSON.parse(readFileSync(new URL("../version.json", import.meta.url), "utf8")).version;

// Identity, pre-release flag, Marketplace "Preview" gallery flag, the manifest <Description> and the bundled
// package.json version and preview field of a VSIX (python3, standard library only).
export function vsixMetadata(path) {
  const script = `import json,sys,zipfile,xml.etree.ElementTree as E
z=zipfile.ZipFile(sys.argv[1])
assert z.testzip() is None,'corrupt VSIX'
m=E.fromstring(z.read('extension.vsixmanifest'))
local=lambda e:e.tag.rsplit('}',1)[-1]
i=[e for e in m.iter() if local(e)=='Identity']
assert len(i)==1,'one VSIX identity'
p={e.get('Id'):e.get('Value') for e in m.iter() if local(e)=='Property'}
f=' '.join(e.text or '' for e in m.iter() if local(e)=='GalleryFlags').split()
d=[e.text or '' for e in m.iter() if local(e)=='Description']
j=json.loads(z.read('extension/package.json')) if 'extension/package.json' in z.namelist() else {}
print(json.dumps(dict(id=i[0].get('Id'),version=i[0].get('Version'),publisher=i[0].get('Publisher'),preRelease=p.get(sys.argv[2])=='true',preview='Preview' in f,description=d[0] if d else None,packageVersion=j.get('version'),packagePreview=j.get('preview') is True)))`;
  return JSON.parse(execFileSync(process.platform === "win32" ? "python" : "python3", ["-c", script, path, VSCODE_PRE_RELEASE], { encoding: "utf8" }));
}

if (import.meta.main) console.log(JSON.stringify(ideVersions(process.argv[2] ?? releaseVersion()), null, 2));
