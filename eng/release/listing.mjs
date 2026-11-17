// Store listing checks of the packaged VS Code extension (eng/release/README.md, "Stores"). package.mjs runs them on
// every package it builds; the Visual Studio listing is checked by visualstudio/package.py.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";

// The Marketplace keeps at most 30 keywords and needs a PNG icon of at least 128 px (256 px for HiDPI).
export const MAX_KEYWORDS = 30;
export const MIN_ICON_SIZE = 128;
// Relative README links point at the release tag, not main, so the listing shows the text of the published version.
export const listingBaseUrls = version => ({
  content: `https://github.com/Runic-Artifex/runic-editors/blob/v${version}/vscode`,
  images: `https://raw.githubusercontent.com/Runic-Artifex/runic-editors/v${version}/vscode`,
});

// Width and height of a PNG, from its IHDR chunk; undefined for anything else.
export function pngSize(bytes) {
  const signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
  if (bytes.length < 24 || signature.some((byte, index) => bytes[index] !== byte)) return undefined;
  if (Buffer.from(bytes.subarray(12, 16)).toString("latin1") !== "IHDR") return undefined;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return { width: view.getUint32(16), height: view.getUint32(20) };
}

// The store pages render README.md and CHANGELOG.md: no SVG images (except approved badge hosts, which we do not
// use) and no plain-http links or images.
export function markdownProblems(text, name) {
  const problems = [];
  const images = [...text.matchAll(/!\[[^\]]*\]\(\s*<?([^)\s>]+)/g), ...text.matchAll(/<img\b[^>]*\bsrc\s*=\s*["']([^"']+)["']/gi)]
    .map(match => match[1]);
  for (const url of images) if (/\.svgz?(?:[?#].*)?$/i.test(url) || /^data:image\/svg/i.test(url)) problems.push(`${name} shows the SVG image ${url}`);
  for (const match of text.matchAll(/(?:\]\(\s*<?|\b(?:src|href)\s*=\s*["']|<)(http:\/\/[^)\s>"']+)/gi)) problems.push(`${name} links to ${match[1]} over plain http`);
  return problems;
}

// package.json, README, CHANGELOG and the icon of a VS Code VSIX (python3, standard library only).
export function vscodeListing(path) {
  const script = `import base64,json,sys,zipfile
z=zipfile.ZipFile(sys.argv[1])
names={n.lower():n for n in z.namelist()}
read=lambda n:z.read(names[n.lower()]) if n.lower() in names else None
m=json.loads(read('extension/package.json'))
icon=read('extension/'+m['icon']) if m.get('icon') else None
text=lambda n:(read(n) or b'').decode('utf-8')
print(json.dumps(dict(manifest=m,readme=text('extension/readme.md'),changelog=text('extension/changelog.md'),icon=base64.b64encode(icon).decode() if icon else None)))`;
  const listing = JSON.parse(execFileSync(process.platform === "win32" ? "python" : "python3", ["-c", script, path], { encoding: "utf8", maxBuffer: 64 << 20 }));
  return { ...listing, icon: listing.icon ? Buffer.from(listing.icon, "base64") : undefined };
}

export function checkVscodeListing({ manifest, readme, changelog, icon }) {
  assert.ok(manifest.icon, "package.json declares no icon");
  assert.ok(icon, `The VSIX does not contain the icon ${manifest.icon}`);
  const size = pngSize(icon);
  assert.ok(size, `The icon ${manifest.icon} is not a PNG`);
  assert.ok(size.width === size.height && size.width >= MIN_ICON_SIZE, `The icon is ${size.width}x${size.height}; use a square PNG of at least ${MIN_ICON_SIZE} px`);
  assert.ok(Array.isArray(manifest.keywords) && manifest.keywords.length <= MAX_KEYWORDS, `package.json needs 1 to ${MAX_KEYWORDS} keywords`);
  assert.equal(manifest.qna, false, "Marketplace Q&A stays off (D070.14); questions go to GitHub issues");
  for (const field of ["homepage", "bugs.url", "repository.url"]) {
    const value = field.split(".").reduce((object, key) => object?.[key], manifest);
    assert.match(value ?? "", /^https:\/\//, `package.json ${field} must be an https URL`);
  }
  assert.ok(readme.trim(), "The VSIX has no README");
  const problems = [...markdownProblems(readme, "README.md"), ...markdownProblems(changelog, "CHANGELOG.md")];
  assert.deepEqual(problems, [], problems.join("; "));
  return size;
}
