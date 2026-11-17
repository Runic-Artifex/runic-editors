#!/usr/bin/env bash
# Renders the store icons of both extensions from the Runic Artifex brand mark (D070.14).
#   eng/brand/render-icons.sh        rewrite the committed PNG files
#
# runic-artifex.svg is a copy of runic-site static/brand/marks/runic-artifex.svg (light variant). The mark sits on a
# rounded tile in the site's --rune-stone colour with a --rune-groove border, so it reads on the light and the dark
# store pages. Needs ImageMagick 7 (`magick`) with the librsvg delegate. Re-run after the mark changes and commit the
# PNG files; eng/release/release.test.mjs checks their sizes.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The paths of the mark, unchanged, scaled into the tile.
paths="$(sed -E 's/^<svg[^>]*>//; s/<\/svg>\s*$//' "$here/runic-artifex.svg")"
cat > "$work/tile.svg" <<SVG
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
  <rect x="1" y="1" width="98" height="98" rx="18" ry="18" fill="#ecede8" stroke="#c8cac2" stroke-width="2"/>
  <g transform="translate(14 14) scale(0.72)">$paths</g>
</svg>
SVG

render() {
  local size="$1" output="$2"
  # 96 dpi renders the 100-unit viewBox at 100 px; render larger, then downsample for clean edges.
  magick -background none -density "$((96 * size * 4 / 100))" "$work/tile.svg" \
    -resize "${size}x${size}" -strip -define png:exclude-chunks=date,time "PNG32:$root/$output"
  echo "Wrote $output (${size}x${size})"
}

render 256 vscode/media/icon.png                  # VS Code Marketplace and Open VSX (package.json "icon")
render 32 visualstudio/media/icon.png             # Visual Studio <Icon>
render 200 visualstudio/media/preview.png         # Visual Studio <PreviewImage>
