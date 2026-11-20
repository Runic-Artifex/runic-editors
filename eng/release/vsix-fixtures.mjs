// Test fixtures: the two CI VSIX artifacts as small but real archives, stamped like CI stamps them.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { VSIX } from "./release.mjs";
import { VSCODE_PRE_RELEASE, ideVersions } from "./ide-versions.mjs";

const script = String.raw`
import json, os, sys, zipfile
from xml.sax.saxutils import escape
path, ident, version, publisher, pre_release, preview, package_preview, package_version, description = sys.argv[1:]
os.makedirs(os.path.dirname(path), exist_ok=True)
properties = f'<Properties><Property Id="${VSCODE_PRE_RELEASE}" Value="true" /></Properties>' if pre_release == 'true' else ''
summary = '<Description xml:space="preserve">' + escape(description) + '</Description>' if description else ''
flags = '<GalleryFlags>Public Preview</GalleryFlags>' if preview == 'true' else '<GalleryFlags>Public</GalleryFlags>'
with zipfile.ZipFile(path, 'w') as z:
    z.writestr('extension.vsixmanifest', f'<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"><Metadata><Identity Id="{ident}" Version="{version}" Language="en-US" Publisher="{publisher}" />{summary}{flags}{properties}</Metadata></PackageManifest>')
    if package_version:
        z.writestr('extension/package.json', json.dumps({'name': ident, 'version': package_version, 'preview': package_preview == 'true', 'license': 'MIT', 'dependencies': {'vscode-languageclient': '10.1.2'}}))
`;

// Writes <directory>/<artifact>/<file> for both extensions; override fields per format to break one.
export function writeVsixFixtures(directory, version, overrides = {}) {
  const versions = ideVersions(version);
  for (const { artifact, file, id, publisher, format } of VSIX) {
    const vscode = format === "vscode";
    const preRelease = vscode && versions.vscodePreRelease, preview = vscode && versions.vscodePreview;
    const fixture = { id, publisher, version: versions[format], preRelease, preview, packagePreview: preview,
      packageVersion: vscode ? versions.vscode : "", description: "Runic DevKit test fixture.", ...overrides[format] };
    execFileSync(process.platform === "win32" ? "python" : "python3", ["-c", script, `${directory}/${artifact}/${file}`, fixture.id, fixture.version,
      fixture.publisher, String(fixture.preRelease), String(fixture.preview), String(fixture.packagePreview), fixture.packageVersion, fixture.description]);
  }
}

// Store-rule fixtures (eng/release/store-rules.mjs): VSIX files built from the real manifests, as CI stamps them, that
// a test breaks one rule at a time. `entries` are text files, `files` copies of repository files.
const zipScript = String.raw`
import json, sys, zipfile
spec = json.load(sys.stdin)
with zipfile.ZipFile(spec['path'], 'w') as z:
    for name, text in spec['entries'].items():
        z.writestr(name, text)
    for name, source in spec['files'].items():
        z.write(source, name)
`;
const writeZip = (path, entries, files = {}) => execFileSync(process.platform === "win32" ? "python" : "python3", ["-c", zipScript],
  { input: JSON.stringify({ path, entries, files }) });
const repository = fileURLToPath(new URL("../..", import.meta.url));
const CONTENT_TYPES = '<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
  + '<Default Extension="vsixmanifest" ContentType="text/xml" /><Default Extension="json" ContentType="application/json" />'
  + '<Default Extension="png" ContentType="application/octet-stream" /></Types>';

// The Visual Studio VSIX as VsixPublisher reads it: visualstudio/source.extension.vsixmanifest with the version CI
// stamps, and its <Icon>; edit(manifest text) breaks it, icon: false leaves the icon out.
export function writeVisualStudioStoreVsix(path, { version = "0.23.1", edit = text => text, icon = true } = {}) {
  const source = readFileSync(join(repository, "visualstudio/source.extension.vsixmanifest"), "utf8")
    .replace("|%CurrentProject%;GetRunicVsixVersion|", ideVersions(version).visualStudio);
  writeZip(path, { "[Content_Types].xml": CONTENT_TYPES, "extension.vsixmanifest": edit(source) },
    icon ? { "media/icon.png": join(repository, "visualstudio/media/icon.png") } : {});
}

// The VS Code VSIX as vsce reads it: vscode/package.json stamped as package.mjs stamps it, in a VSIX manifest that
// carries the pre-release property exactly when packaged as one; edit(package.json object) breaks it.
export function writeVscodeStoreVsix(path, { version = "0.23.1", packagedPreRelease = ideVersions(version).vscodePreRelease, edit = manifest => manifest } = {}) {
  const versions = ideVersions(version);
  const manifest = edit({ ...JSON.parse(readFileSync(join(repository, "vscode/package.json"), "utf8")), version: versions.vscode, preview: versions.vscodePreview });
  const properties = packagedPreRelease ? `<Properties><Property Id="${VSCODE_PRE_RELEASE}" Value="true" /></Properties>` : "";
  writeZip(path, { "[Content_Types].xml": CONTENT_TYPES, "extension/package.json": JSON.stringify(manifest),
    "extension.vsixmanifest": `<?xml version="1.0" encoding="utf-8"?><PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">`
      + `<Metadata><Identity Language="en-US" Id="${manifest.name}" Version="${manifest.version}" Publisher="${manifest.publisher}" />${properties}</Metadata></PackageManifest>` });
}
