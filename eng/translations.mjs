// The Runic Translations release the extensions are built and tested against is the dotnet-runic-translations
// version in .config/dotnet-tools.json. The VS Code extension bundles that release's runic.json schema from
// eng/translations/project-v1.schema.json; this script takes it from the release's Runic.Translations.Tooling
// package on nuget.org.
//   bun eng/translations.mjs           print the pinned version and refresh the schema copy
//   bun eng/translations.mjs --check   fail when the schema copy differs from the pinned release's
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const read = path => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
export const translationsVersion = () => JSON.parse(read(".config/dotnet-tools.json")).tools["dotnet-runic-translations"].version;
const SCHEMA = "eng/translations/project-v1.schema.json";

async function releasedSchema(version) {
  const id = "runic.translations.tooling";
  const url = `https://api.nuget.org/v3-flatcontainer/${id}/${version}/${id}.${version}.nupkg`;
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url}: ${response.status}`);
  const directory = mkdtempSync(join(tmpdir(), "runic-editors-translations-"));
  try {
    const archive = join(directory, "package.nupkg");
    writeFileSync(archive, Buffer.from(await response.arrayBuffer()));
    // Python's zipfile (standard library only) reads the one member; nothing from the package is executed.
    return execFileSync(process.platform === "win32" ? "python" : "python3",
      ["-I", "-c", "import sys,zipfile;sys.stdout.buffer.write(zipfile.ZipFile(sys.argv[1]).read('schemas/project-v1.schema.json'))", archive]);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

if (import.meta.main) {
  const version = translationsVersion();
  const schema = await releasedSchema(version);
  const check = process.argv.includes("--check");
  if (!check) {
    writeFileSync(new URL(`../${SCHEMA}`, import.meta.url), schema);
    console.log(`Runic Translations ${version}: wrote ${SCHEMA}`);
  } else if (read(SCHEMA) !== schema.toString("utf8")) {
    console.log(`::error::${SCHEMA} differs from the runic.json schema of Runic Translations ${version}; run bun eng/translations.mjs and commit the result.`);
    process.exitCode = 1;
  } else console.log(`${SCHEMA} matches Runic Translations ${version}`);
}
