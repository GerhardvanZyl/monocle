#!/usr/bin/env bash
# Publish a self-contained Linux build of Monocle and package it as an AppImage.
# Requires: dotnet 10 SDK, and (for the AppImage step) appimagetool on PATH.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/publish/linux-x64"
APPDIR="$ROOT/publish/Monocle.AppDir"

echo "Publishing app (self-contained, linux-x64)…"
dotnet publish "$ROOT/src/Monocle.App/Monocle.App.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -o "$OUT"

echo "Publishing MCP server (self-contained) into $OUT/mcp …"
dotnet publish "$ROOT/src/Monocle.Mcp/Monocle.Mcp.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -o "$OUT/mcp"

echo "Assembling AppDir…"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
cp -r "$OUT/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/Monocle.App"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Monocle.App" "$@"
EOF
chmod +x "$APPDIR/AppRun"

cat > "$APPDIR/monocle.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Monocle
Exec=Monocle.App
Icon=monocle
Categories=Graphics;Photography;
EOF

# A placeholder icon keeps appimagetool happy; replace with a real one.
touch "$APPDIR/monocle.png"

if command -v appimagetool >/dev/null 2>&1; then
  echo "Building AppImage…"
  appimagetool "$APPDIR" "$ROOT/publish/Monocle-x86_64.AppImage"
  echo "Done -> $ROOT/publish/Monocle-x86_64.AppImage"
else
  echo "appimagetool not found — AppDir is ready at $APPDIR."
  echo "Install appimagetool and run: appimagetool '$APPDIR' '$ROOT/publish/Monocle-x86_64.AppImage'"
fi
