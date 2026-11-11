#!/usr/bin/env bash
# Replaces the bundled Facepunch.Steamworks with the given upstream release.
# Usage: update-facepunch.sh <release tag>
set -euo pipefail

target="$1"
current="$(cat .facepunch-version)"
plugins="Assets/PurrNetFacepunch/Plugins"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

curl -fsSL -o "$work/release.zip" \
  "https://github.com/Facepunch/Facepunch.Steamworks/releases/download/$target/Facepunch.Steamworks.$target.zip"
unzip -q "$work/release.zip" -d "$work/release"

# The zip layout differs between releases: prefer a Release build of the Unity folder, else any.
candidates="$(find "$work/release" -type f -path '*/Unity/Facepunch.Steamworks.Win64.dll' -not -path '*/__MACOSX/*' -not -path '*/Debug/*' | sort)"
match="$(printf '%s
' "$candidates" | grep '/Release/' | head -n 1 || true)"
if [ -z "$match" ]; then
  match="$(printf '%s
' "$candidates" | head -n 1)"
fi
if [ -z "$match" ]; then
  echo "Release $target has no Unity folder with Facepunch.Steamworks.Win64.dll." >&2
  exit 1
fi

source="$(dirname "$match")"
for required in Facepunch.Steamworks.Win64.dll.meta Facepunch.Steamworks.Posix.dll redistributable_bin; do
  if [ ! -e "$source/$required" ]; then
    echo "Release $target is missing Unity/$required." >&2
    exit 1
  fi
done
echo "Using ${source#"$work/release/"}"

rsync -a --delete \
  --exclude='*.pdb' --exclude='*.pdb.meta' --exclude='.DS_Store' --exclude='LICENSE.Facepunch.md*' \
  "$source/" "$plugins/"

sed -i "s/Facepunch\.Steamworks ${current//./\.}/Facepunch.Steamworks $target/" README.md Assets/PurrNetFacepunch/package.json
printf '%s\n' "$target" > .facepunch-version
