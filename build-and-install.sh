#!/usr/bin/env bash
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET_DIR="$HOME/.local/share/convenient-chests-controller-dotnet"

find_game() {
  local candidates=(
    "$HOME/.local/share/Steam/steamapps/common/Stardew Valley"
    "$HOME/.steam/steam/steamapps/common/Stardew Valley"
  )
  for p in "${candidates[@]}"; do
    if [[ -f "$p/Stardew Valley.dll" && -f "$p/StardewModdingAPI.dll" ]]; then
      printf '%s\n' "$p"
      return 0
    fi
  done

  local p
  while IFS= read -r p; do
    if [[ -f "$p/Stardew Valley.dll" && -f "$p/StardewModdingAPI.dll" ]]; then
      printf '%s\n' "$p"
      return 0
    fi
  done < <(find /run/media -maxdepth 6 -type d -path '*/steamapps/common/Stardew Valley' 2>/dev/null || true)

  return 1
}

GAME_PATH="${GAME_PATH:-$(find_game || true)}"
if [[ -z "$GAME_PATH" ]]; then
  echo "Couldn't find the modded Stardew Valley folder automatically."
  echo "Run again like this:"
  echo "  GAME_PATH='/path/to/Stardew Valley' ./build-and-install.sh"
  exit 1
fi

echo "Using Stardew Valley at: $GAME_PATH"

if command -v dotnet >/dev/null 2>&1; then
  DOTNET="$(command -v dotnet)"
elif [[ -x "$DOTNET_DIR/dotnet" ]]; then
  DOTNET="$DOTNET_DIR/dotnet"
else
  echo "Installing a local .NET 6 SDK for this build..."
  mkdir -p "$DOTNET_DIR"
  INSTALLER="$(mktemp)"
  trap 'rm -f "$INSTALLER"' EXIT
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$INSTALLER"
  bash "$INSTALLER" --channel 6.0 --install-dir "$DOTNET_DIR" --no-path
  DOTNET="$DOTNET_DIR/dotnet"
fi

BUILD_DIR="$HERE/.build"
OUT_DIR="$HERE/ConvenientChestsController"
rm -rf "$BUILD_DIR" "$OUT_DIR"
mkdir -p "$BUILD_DIR" "$OUT_DIR"

"$DOTNET" build "$HERE/ConvenientChestsController.csproj" \
  --configuration Release \
  --output "$BUILD_DIR" \
  -p:GamePath="$GAME_PATH"

cp "$BUILD_DIR/ConvenientChestsController.dll" "$OUT_DIR/"
cp "$HERE/manifest.json" "$OUT_DIR/"
cp "$HERE/README.md" "$OUT_DIR/"

MODS_DIR="$GAME_PATH/Mods"
DEST="$MODS_DIR/ConvenientChestsController"
rm -rf "$DEST"
cp -a "$OUT_DIR" "$DEST"

echo
echo "Installed: $DEST"
echo "Launch Stardew through SMAPI and test a chest."
echo "To uninstall, delete only that ConvenientChestsController folder."
