"""Verify and copy a VSIX produced by Microsoft's Windows VSSDK build targets."""
from pathlib import Path
import argparse
import json
import re
import shutil
import struct
import zipfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--configuration', default='Debug', choices=['Debug', 'Release'])
parser.add_argument('--input', type=Path, help='Native-built VSIX, optionally copied from Windows')
parser.add_argument('--check-source', action='store_true', help='Validate the source manifest and support range without a native VSIX')
parser.add_argument('--release-version', help='Release version to expect (default: eng/version.json)')
parser.add_argument('--print-version', action='store_true', help='Print the VSIX version of the release version and exit')
args = parser.parse_args()
root = Path(__file__).resolve().parent
ns = {'v': 'http://schemas.microsoft.com/developer/vsx-schema/2011'}
source_manifest = root / 'source.extension.vsixmanifest'
# The build stamps the Identity version through this VSSDK token (GetRunicVsixVersion in the project).
VERSION_TOKEN = '|%CurrentProject%;GetRunicVsixVersion|'


def vsix_version(release):
    """1.0.0-preview.N is 1.0.0.N, 1.0.0-rc.N is 1.0.0.(500 + N) and the final 1.0.0 is 1.0.0.1000,
    as in eng/release/ide-versions.mjs."""
    match = re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(preview|rc)\.([1-9]\d?|[1-4]\d{2}))?', release)
    assert match, f'Release version {release} cannot be mapped to a VSIX version; use x.y.z, x.y.z-preview.N or x.y.z-rc.N (1 <= N <= 499)'
    # Visual Studio parses each component as a 16-bit System.Version part.
    assert all(int(part) <= 65534 for part in match.group(1, 2, 3)), f'Release version {release} has a component above 65534'
    revision = 1000 if not match.group(4) else int(match.group(5)) + (500 if match.group(4) == 'rc' else 0)
    return '.'.join(match.group(1, 2, 3)) + '.' + str(revision)


release_version = args.release_version or json.loads((root / '../eng/version.json').read_text(encoding='utf-8'))['version']
expected_version = vsix_version(release_version)
if args.print_version:
    print(expected_version)
    raise SystemExit(0)

# The store identity is permanent once published (D070.5); eng/release/release.mjs MARKETPLACE holds it and
# release.test.mjs checks that publishManifest.json agrees.
STORE_PUBLISHER = 'runic-artifex'
STORE_INTERNAL_NAME = 'runic-devkit-vs'
# vsix-publish schema (https://json.schemastore.org/vsix-publish): 1-3 of its categories.
STORE_CATEGORIES = {'build', 'coding', 'data', 'debugging', 'documentation', 'extension sdk', 'framework and libraries',
                    'other', 'performance', 'programming languages', 'security', 'source control', 'testing', 'web'}
# Listing images: <Icon> 32x32 in lists, <PreviewImage> 200x200 (VSIX schema 2.0 reference).
LISTING_IMAGES = {'Icon': 32, 'PreviewImage': 200}


def png_size(data):
    assert data[:8] == b'\x89PNG\r\n\x1a\n' and data[12:16] == b'IHDR', 'Listing image is not a PNG'
    return struct.unpack('>II', data[16:24])


def listing_contract(manifest):
    """The store listing fields of the Metadata element, checked against the Marketplace limits."""
    metadata = manifest.find('v:Metadata', ns)
    text = lambda name: (metadata.findtext(f'v:{name}', namespaces=ns) or '').strip()
    listing = {name: text(name) for name in ('DisplayName', 'MoreInfo', 'ReleaseNotes', 'Icon', 'PreviewImage', 'Tags', 'Preview')}
    assert 0 < len(listing['DisplayName']) <= 50, 'DisplayName must have 1 to 50 characters'
    assert 0 < len(text('Description')) <= 1000, 'Description must have 1 to 1000 characters'
    assert 0 < len(listing['Tags']) <= 100, 'Tags must have 1 to 100 characters'
    assert listing['Preview'] in ('', 'false'), 'Runic DevKit has no Preview label since 0.23 (D077.4): no <Preview>true</Preview>'
    for name in ('MoreInfo', 'ReleaseNotes'):
        assert listing[name].startswith('https://'), f'{name} must be an https URL'
    for name in LISTING_IMAGES:
        listing[name] = listing[name].replace('\\', '/')
        assert listing[name], f'<{name}> is missing'
    return tuple(sorted(listing.items()))


def check_publish_manifest():
    publish = json.loads((root / 'publishManifest.json').read_text(encoding='utf-8'))
    assert publish.get('publisher') == STORE_PUBLISHER, f'publishManifest.json publisher must be {STORE_PUBLISHER}'
    assert publish.get('identity') == {'internalName': STORE_INTERNAL_NAME}, f'publishManifest.json internalName must be {STORE_INTERNAL_NAME}'
    categories = publish.get('categories') or []
    assert 1 <= len(categories) <= 3 and len(set(categories)) == len(categories) and set(categories) <= STORE_CATEGORIES, 'publishManifest.json needs 1-3 Marketplace categories'
    overview = root / publish.get('overview', '')
    assert publish.get('overview') and overview.is_file(), 'publishManifest.json overview must name an existing file'
    text = overview.read_text(encoding='utf-8')
    assert not re.search(r'\]\((?!https://|#)', text), 'overview.md links must be absolute https URLs'
    assert 'http://' not in text and '.svg' not in text.lower(), 'overview.md must not use http or SVG images'
    assert publish.get('qna') is False, 'Marketplace Q&A stays off (D070.14)'
    assert publish.get('priceCategory') == 'free' and publish.get('private') is False, 'The extension is free and public'
    assert publish.get('repo', '').startswith('https://github.com/Runic-Artifex/runic-editors'), 'publishManifest.json repo must be this repository'
    assert not publish.get('assetFiles'), 'overview.md uses no images, so publishManifest.json has no assetFiles'


def manifest_contract(manifest):
    identity = manifest.find('v:Metadata/v:Identity', ns)
    target = manifest.find('v:Installation/v:InstallationTarget', ns)
    prerequisite = manifest.find('v:Prerequisites/v:Prerequisite', ns)
    assert identity is not None, 'VSIX identity is missing'
    assert target is not None, 'VSIX installation target is missing'
    # Visual Studio x64 only (D070.14): one target with one architecture, so no ARM64 target is published untested.
    architectures = [element.text for element in manifest.findall('v:Installation/v:InstallationTarget/v:ProductArchitecture', ns)]
    assert len(manifest.findall('v:Installation/v:InstallationTarget', ns)) == 1 and architectures == ['amd64'], 'The VSIX targets Visual Studio x64 (amd64) only (D070.14)'
    assert prerequisite is not None, 'VSIX core-editor prerequisite is missing'
    assets = tuple(sorted(
        tuple(sorted((name, value.replace('\\', '/') if name == 'Path' else value) for name, value in asset.attrib.items()))
        for asset in manifest.findall('v:Assets/v:Asset', ns)
    ))
    return {
        'identity': tuple(sorted((name, value) for name, value in identity.attrib.items() if name != 'Version')),
        'version': identity.get('Version'),
        'license': manifest.findtext('v:Metadata/v:License', namespaces=ns),
        'target': (tuple(sorted(target.attrib.items())), target.findtext('v:ProductArchitecture', namespaces=ns)),
        'prerequisite': tuple(sorted(prerequisite.attrib.items())),
        'assets': assets,
        'listing': listing_contract(manifest),
    }

source_contract = manifest_contract(ET.parse(source_manifest).getroot())
assert source_contract['version'] == VERSION_TOKEN, f'The source manifest must take its version from the build: Version="{VERSION_TOKEN}"'

if args.check_source:
    identity = dict(source_contract['identity'])
    target = dict(source_contract['target'][0])
    prerequisite = dict(source_contract['prerequisite'])
    assert identity == {'Id': 'RunicArtifex.DevKit.VisualStudio', 'Language': 'en-US', 'Publisher': 'Runic Artifex'}, 'Unexpected source VSIX identity'
    assert target == {'Id': 'Microsoft.VisualStudio.Community', 'Version': '[18.0,19.0)'}, 'Manifest must declare the Visual Studio 2026 (18.x) host line'
    assert prerequisite['Id'] == 'Microsoft.VisualStudio.Component.CoreEditor' and prerequisite['Version'] == '[18.0,19.0)', 'Core editor prerequisite must match the declared installation range'
    assert source_contract['target'][1] == 'amd64', 'VSIX must declare the amd64 product architecture'
    assert source_contract['license'] == 'LICENSE.txt', 'VSIX must carry the packaged license name'
    assert {(dict(asset)['Type'], dict(asset)['Path']) for asset in source_contract['assets']} == {
        ('Microsoft.VisualStudio.MefComponent', 'RunicArtifex.DevKit.VisualStudio.dll'),
        ('Microsoft.VisualStudio.VsPackage', 'Runic.pkgdef'),
        ('Microsoft.VisualStudio.VsPackage', 'RunicArtifex.DevKit.VisualStudio.pkgdef'),
    }, 'Unexpected source VSIX asset contract'
    listing = dict(source_contract['listing'])
    for name, size in LISTING_IMAGES.items():
        image = root / listing[name]
        assert image.is_file(), f'<{name}> {listing[name]} does not exist'
        assert png_size(image.read_bytes()) == (size, size), f'<{name}> must be a {size}x{size} PNG'
    check_publish_manifest()
    print(f'PASS source VSIX manifest, host range, prerequisite, store listing and publishManifest.json contract; {release_version} stamps version {expected_version}')
    raise SystemExit(0)

source = args.input or root / 'bin' / args.configuration / 'net472/RunicArtifex.DevKit.VisualStudio.vsix'
if not source.is_file():
    parser.error('Build with Visual Studio MSBuild on Windows first, or supply --input with that VSIX.')
with zipfile.ZipFile(source) as archive:
    assert archive.testzip() is None, 'Corrupt VSIX'
    names = set(archive.namelist())
    for name in ('manifest.json', 'catalog.json', '[Content_Types].xml', 'LICENSE.txt', 'Runic.pkgdef', 'RunicArtifex.DevKit.VisualStudio.pkgdef'):
        assert name in names, f'Missing native installer metadata: {name}'
    manifest = ET.fromstring(archive.read('extension.vsixmanifest'))
    embedded = manifest_contract(manifest)
    assert embedded['version'] == expected_version, f"VSIX version {embedded['version']} is not {expected_version}, the stamped version of {release_version}"
    assert embedded | {'version': VERSION_TOKEN} == source_contract, 'Embedded VSIX manifest identity/license/host contract differs from source.extension.vsixmanifest'
    for asset in manifest.findall('v:Assets/v:Asset', ns):
        assert asset.attrib['Path'].replace('\\', '/') in names, 'Missing declared VSIX asset'
    dlls = [name for name in names if name.lower().endswith('.dll')]
    assert sorted(dlls) == ['RunicArtifex.DevKit.VisualStudio.Core.dll', 'RunicArtifex.DevKit.VisualStudio.dll'], 'Ship only the extension and its Core library; do not redistribute VS-owned assemblies'
    assert all(archive.read(dll)[:2] == b'MZ' for dll in dlls), 'Missing managed extension'
    for name, size in LISTING_IMAGES.items():
        path = dict(embedded['listing'])[name]
        assert path in names, f'The VSIX does not contain its <{name}> {path}'
        assert png_size(archive.read(path)) == (size, size), f'<{name}> must be a {size}x{size} PNG'
    assert 'rmf2' in json.loads(archive.read('Grammars/rmf2.tmLanguage.json'))['fileTypes']
output = root / 'artifacts/runic-devkit-vs.vsix'
output.parent.mkdir(exist_ok=True)
if source.resolve() != output.resolve():
    shutil.copyfile(source, output)
print(f'PASS native VSIX {expected_version} (release {release_version}) metadata, assets, grammar and archive integrity: {output}')
