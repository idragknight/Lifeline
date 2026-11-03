#!/usr/bin/env bash
# Builds Lifeline.dll (plugin) and LifelineHelper.exe (helper) for VibeSkua 2.0+.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
SKUA_COMMIT='967f07e4b2ed7d0180e761644e1e1319d6e5db9a'   # auqw/skua, Skua 1.4.5
DEPS="$ROOT/.deps/skua"

if [ ! -d "$DEPS/.git" ]; then
    echo "== Downloading Skua (for its interface assemblies)"
    git clone -q https://github.com/auqw/skua.git "$DEPS"
fi
git -C "$DEPS" fetch -q origin
git -C "$DEPS" checkout -q -f "$SKUA_COMMIT"

echo "== Building Skua.Core.Interfaces / Models / Utils"
dotnet build "$DEPS/Skua.Core.Interfaces/Skua.Core.Interfaces.csproj" -c Release -p:EnableWindowsTargeting=true -v quiet
dotnet build "$DEPS/Skua.Core.Models/Skua.Core.Models.csproj"         -c Release -p:EnableWindowsTargeting=true -v quiet
dotnet build "$DEPS/Skua.Core.Utils/Skua.Core.Utils.csproj"           -c Release -p:EnableWindowsTargeting=true -v quiet

LIB="$ROOT/.deps/lib"; mkdir -p "$LIB"
for src in \
    "$DEPS/Skua.Core.Interfaces/bin/Release" \
    "$DEPS/Skua.Core.Models/bin/Release" \
    "$DEPS/Skua.Core.Utils/bin/Release"; do
    [ -d "$src" ] && find "$src" -name 'Skua.Core.*.dll' -exec cp -f {} "$LIB/" \;
done

echo "== Building Lifeline plugin"
dotnet build "$ROOT/src/Lifeline.csproj" -c Release -p:SkuaLib="$LIB" -v quiet
echo "== Done: $ROOT/src/bin/Release/net10.0-windows/Lifeline.dll"

echo "== Building LifelineHelper"
dotnet build "$ROOT/helper/LifelineHelper.csproj" -c Release -v quiet

# Print clear instructions for the 4 files
echo ""
echo "=========================================================="
echo "Build Complete! (VibeSkua 2.0+ only)"
echo ""
echo "1. Copy Lifeline.dll to your VibeSkua plugins folder:"
echo "   %APPDATA%\\VibeSkua\\plugins\\Lifeline.dll"
echo "   (Built at: $ROOT/src/bin/Release/net10.0-windows/Lifeline.dll)"
echo ""
echo "2. Copy the ENTIRE contents of the helper folder into a 'Lifeline' subfolder:"
echo "   %APPDATA%\\VibeSkua\\plugins\\Lifeline\\"
echo "   (Built at: $ROOT/helper/bin/Release/net10.0-windows/)"
echo ""
echo "   (That folder contains LifelineHelper.exe, .dll, .deps.json, and"
echo "    .runtimeconfig.json. You need ALL of them.)"
echo "=========================================================="
