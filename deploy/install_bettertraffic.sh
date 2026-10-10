#!/bin/sh
# Installs the ASFALTO AssettoServer build (BetterTraffic) over an AssettoServer 0.0.55-pre44 install.
# Run from the server folder after extracting the archive there:  sh install_bettertraffic.sh
# Backs up the current binary to AssettoServer.upstream and enables AiParams.BetterTraffic in cfg/extra_cfg.yml.
# Undo: mv AssettoServer.upstream AssettoServer  (BetterTraffic settings are ignored by upstream).
set -e
cd "$(dirname "$0")"

if [ ! -f AssettoServer.asfalto ]; then
  echo "AssettoServer.asfalto not found: extract the archive in the server folder first."; exit 1
fi

# stop the server before replacing its binary
if [ -f ./lsp.sh ]; then
  if [ -x ./lsp.sh ]; then ./lsp.sh stop || echo "./lsp.sh stop failed (already stopped?), continuing"
  else sh ./lsp.sh stop || echo "./lsp.sh stop failed (already stopped?), continuing"; fi
else
  echo "No ./lsp.sh here: make sure the server is stopped."
fi
if [ -f AssettoServer ] && [ ! -f AssettoServer.upstream ]; then
  cp AssettoServer AssettoServer.upstream
  echo "Backed up the current server to AssettoServer.upstream"
fi
cp AssettoServer.asfalto AssettoServer
chmod +x AssettoServer
echo "Installed the ASFALTO AssettoServer build"

# AutoModerationPlugin: same plugin, its warning icons no longer show as grey squares
if [ -f AutoModerationPlugin.dll.asfalto ] && [ -d plugins/AutoModerationPlugin ]; then
  if [ -f plugins/AutoModerationPlugin/AutoModerationPlugin.dll ] && [ ! -f plugins/AutoModerationPlugin/AutoModerationPlugin.dll.upstream ]; then
    cp plugins/AutoModerationPlugin/AutoModerationPlugin.dll plugins/AutoModerationPlugin/AutoModerationPlugin.dll.upstream
  fi
  cp AutoModerationPlugin.dll.asfalto plugins/AutoModerationPlugin/AutoModerationPlugin.dll
  echo "Installed the ASFALTO AutoModerationPlugin (backup: plugins/AutoModerationPlugin/AutoModerationPlugin.dll.upstream)"
fi

cfg=cfg/extra_cfg.yml
if grep -q '^  Traffic2:' "$cfg"; then
  # build 1 called it Traffic2; AssettoServer refuses unknown settings, so rename it
  cp "$cfg" "$cfg.bak-traffic2"
  sed -i 's/^  Traffic2:/  BetterTraffic:/' "$cfg"
  echo "Renamed AiParams.Traffic2 to AiParams.BetterTraffic in $cfg (backup: $cfg.bak-traffic2)"
fi
if grep -q '^  BetterTraffic:' "$cfg"; then
  echo "AiParams.BetterTraffic already present in $cfg (left as is)"
elif grep -q '^AiParams:' "$cfg"; then
  cp "$cfg" "$cfg.bak-bettertraffic"
  sed -i 's/^AiParams:.*$/AiParams:\n  BetterTraffic:\n    Enabled: true\n    SlowLaneSide: Right/' "$cfg"
  echo "Enabled BetterTraffic in $cfg (backup: $cfg.bak-bettertraffic). Set SlowLaneSide: Left for left-hand traffic maps."
else
  echo "No AiParams section in $cfg: add  AiParams: / BetterTraffic: / Enabled: true  by hand."
fi
echo "Done. Restart the server. All BetterTraffic settings and their defaults are listed in ASFALTO.md."
