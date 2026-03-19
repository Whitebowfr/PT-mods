#!/usr/bin/env zsh
set -euo pipefail

# Builds the plugin and copies it into ../ (BepInEx/plugins)
SCRIPT_DIR=${0:a:h}
cd "$SCRIPT_DIR"

dotnet build -c Release

OUT_DLL="$SCRIPT_DIR/bin/Release/QOLImprovements.dll"
cp -v "$OUT_DLL" "$SCRIPT_DIR/.."/
