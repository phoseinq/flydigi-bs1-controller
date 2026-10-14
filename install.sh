#!/bin/sh
# BS1 Cooler: quick install on Linux and macOS (the latest release, for this user only).
#   curl -fsSL https://raw.githubusercontent.com/phoseinq/flydigi-bs1-controller/main/install.sh | sh
#   ... | sh -s -- --uninstall
set -eu
REPO=phoseinq/flydigi-bs1-controller

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)               rid=linux-x64; dest="$HOME/.local/share/bs1cooler" ;;
  Darwin-arm64)               rid=osx-arm64; dest="$HOME/Applications/BS1Cooler" ;;
  Darwin-x86_64)              rid=osx-x64;   dest="$HOME/Applications/BS1Cooler" ;;
  *) echo "BS1 Cooler has no build for $(uname -s) $(uname -m) yet." >&2; exit 1 ;;
esac
link="$HOME/.local/bin/bs1cooler"
desktop="$HOME/.local/share/applications/bs1cooler.desktop"
rules=/etc/udev/rules.d/99-bs1cooler.rules

if [ "${1:-}" = "--uninstall" ]; then
  pkill -x Bs1Cooler 2>/dev/null || true
  rm -rf "$dest" "$link" "$desktop" "$HOME/.config/autostart/bs1cooler.desktop" "$HOME/Library/LaunchAgents/com.phoseinq.bs1cooler.plist"
  [ -f "$rules" ] && echo "The USB rule stays; remove it with: sudo rm $rules"
  echo "BS1 Cooler removed (settings are kept in ~/.config/Bs1Cooler)."
  exit 0
fi

tag=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name": *"v\([^"]*\)".*/\1/p' | head -n 1)
[ -n "$tag" ] || { echo "Could not find the latest release on GitHub." >&2; exit 1; }
echo "Installing BS1 Cooler $tag ($rid) into $dest"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fL --progress-bar "https://github.com/$REPO/releases/download/v$tag/BS1Cooler-$tag-$rid.tar.gz" | tar -xz -C "$tmp"
pkill -x Bs1Cooler 2>/dev/null || true
rm -rf "$dest"
mkdir -p "$(dirname "$dest")" "$(dirname "$link")"
mv "$tmp/BS1Cooler" "$dest"
ln -sf "$dest/Bs1Cooler" "$link"

if [ "$rid" = linux-x64 ]; then
  mkdir -p "$(dirname "$desktop")"
  cat > "$desktop" <<EOF
[Desktop Entry]
Type=Application
Name=BS1 Cooler
Comment=Fan and lights for the Flydigi BS1 laptop cooler
Exec="$dest/Bs1Cooler"
Icon=$dest/cooler.ico
Categories=Utility;System;
EOF
  # USB access without root: the one step that needs sudo
  if [ ! -f "$rules" ] && command -v sudo >/dev/null 2>&1; then
    echo "Allowing your user to reach the cooler over USB (needs sudo once):"
    if sudo install -m 644 "$dest/99-bs1cooler.rules" "$rules"; then
      sudo udevadm control --reload && sudo udevadm trigger || true
    else
      echo "Skipped. Later: sudo install -m 644 $dest/99-bs1cooler.rules $rules"
    fi
  fi
else
  # unsigned app: let macOS run it
  xattr -dr com.apple.quarantine "$dest" 2>/dev/null || true
fi

[ -n "${BS1COOLER_NOSTART:-}" ] || nohup "$dest/Bs1Cooler" >/dev/null 2>&1 &
echo "Done. BS1 Cooler is running (tray icon). Run it again any time with: bs1cooler"
case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) echo "(add ~/.local/bin to your PATH to use the bs1cooler command)";; esac
