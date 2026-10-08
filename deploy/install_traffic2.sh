#!/bin/sh
# Installs the ASFALTO AssettoServer build (Traffic 2.0) over an AssettoServer 0.0.55-pre44 install.
# Run from the server folder after extracting the archive there:  sh install_traffic2.sh
# Backs up the current binary to AssettoServer.upstream and enables AiParams.Traffic2 in cfg/extra_cfg.yml.
# Undo: mv AssettoServer.upstream AssettoServer  (Traffic2 settings are ignored by upstream).
set -e
cd "$(dirname "$0")"

if [ ! -f AssettoServer.asfalto ]; then
  echo "AssettoServer.asfalto not found: extract the archive in the server folder first."; exit 1
fi
if [ -f AssettoServer ] && [ ! -f AssettoServer.upstream ]; then
  cp AssettoServer AssettoServer.upstream
  echo "Backed up the current server to AssettoServer.upstream"
fi
cp AssettoServer.asfalto AssettoServer
chmod +x AssettoServer
echo "Installed the ASFALTO AssettoServer build"

cfg=cfg/extra_cfg.yml
if grep -q '^  Traffic2:' "$cfg"; then
  echo "AiParams.Traffic2 already present in $cfg (left as is)"
elif grep -q '^AiParams:' "$cfg"; then
  cp "$cfg" "$cfg.bak-traffic2"
  sed -i 's/^AiParams:.*$/AiParams:\n  Traffic2:\n    Enabled: true\n    SlowLaneSide: Right/' "$cfg"
  echo "Enabled Traffic 2.0 in $cfg (backup: $cfg.bak-traffic2). Set SlowLaneSide: Left for left-hand traffic maps."
else
  echo "No AiParams section in $cfg: add  AiParams: / Traffic2: / Enabled: true  by hand."
fi
echo "Done. Restart the server. All Traffic2 settings and their defaults are listed in ASFALTO.md."
