// Packages artifacts/runic-devkit.vsix with the release version of eng/version.json.
//   node package.mjs [release-version [output]]   package another release mapping (CI checks a final one)
// The committed package.json keeps a placeholder version: the files vsce would package are
// copied to a staging directory, package.json is stamped there and vsce packs the copy, so
// packaging never modifies tracked files. The stamp also sets "preview" false: since 0.23 no release
// carries the Marketplace "Preview" label (D077.4). Only a
// x.y.z-preview.N or x.y.z-rc.N release is packaged with --pre-release. Relative README links point at the
// release tag (--baseContentUrl/--baseImagesUrl), and the store listing (icon, keywords, README and CHANGELOG
// images) is checked after packing (eng/release/listing.mjs).
// See eng/release/ide-versions.mjs for the version mapping.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { ideVersions, releaseVersion, vsixMetadata } from '../eng/release/ide-versions.mjs';
import { checkVscodeListing, listingBaseUrls, vscodeListing } from '../eng/release/listing.mjs';

const here = import.meta.dirname;
const vsce = join(dirname(createRequire(import.meta.url).resolve('@vscode/vsce/package.json')), 'vsce');
const run = (args, cwd) => execFileSync(process.execPath, [vsce, ...args], { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
const versions = ideVersions(process.argv[2] ?? releaseVersion());
const output = resolve(process.argv[3] ?? join(here, 'artifacts', 'runic-devkit.vsix'));
const staging = join(here, 'artifacts', 'package');
const baseUrls = listingBaseUrls(versions.release);

const files = run(['ls', '--no-dependencies'], here).split('\n').map(line => line.trim()).filter(Boolean);
assert.ok(files.includes('package.json') && files.includes('dist/extension.cjs') && files.includes('dist/schemas/project-v1.schema.json') && files.includes('media/icon.png'), `Unexpected vsce file list: ${files.join(', ')}`);
rmSync(staging, { recursive: true, force: true });
mkdirSync(staging, { recursive: true });
mkdirSync(dirname(output), { recursive: true });
try {
  // .vscodeignore only keeps vsce from warning; the staging directory holds exactly the listed files.
  for (const file of [...files, '.vscodeignore']) cpSync(join(here, file), join(staging, file));
  const manifest = JSON.parse(readFileSync(join(here, 'package.json'), 'utf8'));
  writeFileSync(join(staging, 'package.json'), `${JSON.stringify({ ...manifest, version: versions.vscode, preview: versions.vscodePreview }, null, 2)}\n`);
  rmSync(output, { force: true });
  run(['package', '--no-dependencies', ...(versions.vscodePreRelease ? ['--pre-release'] : []),
    '--baseContentUrl', baseUrls.content, '--baseImagesUrl', baseUrls.images, '--out', output], staging);
} finally {
  rmSync(staging, { recursive: true, force: true });
}

const { description: _description, ...packed } = vsixMetadata(output);
assert.deepEqual(packed, { id: 'runic-devkit', version: versions.vscode, publisher: 'runic-artifex',
  preRelease: versions.vscodePreRelease, preview: versions.vscodePreview, packageVersion: versions.vscode,
  packagePreview: versions.vscodePreview },
  `${output} does not carry the ${versions.release} mapping ${JSON.stringify(versions)}`);
const icon = checkVscodeListing(vscodeListing(output));
console.log(`Packaged ${output}: version ${versions.vscode}${versions.vscodePreRelease ? ' (pre-release)' : ''} for release ${versions.release}, icon ${icon.width}x${icon.height}`);
