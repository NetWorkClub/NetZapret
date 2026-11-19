#!/usr/bin/env bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

if [ -f "$SCRIPT_DIR/NetZapret" ]; then
    chmod +x "$SCRIPT_DIR/NetZapret" 2>/dev/null
fi
if [ -f "$SCRIPT_DIR/NetZapret.Daemon" ]; then
    chmod +x "$SCRIPT_DIR/NetZapret.Daemon" 2>/dev/null
fi
if [ -f "$SCRIPT_DIR/assets/bin/linux/nfqws" ]; then
    chmod +x "$SCRIPT_DIR/assets/bin/linux/nfqws" 2>/dev/null
fi

exec "$SCRIPT_DIR/NetZapret" "$@"
