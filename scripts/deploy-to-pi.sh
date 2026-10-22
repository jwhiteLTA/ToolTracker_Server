#!/bin/bash
# ==========================================
# Deploy to Pi (run from the PC, in Git Bash)
# ==========================================
# Pushes local commits to GitHub, then pulls them onto the Pi and restarts the Node service
# so the change actually takes effect. Static files (public/*.html/*.js/*.css) would already
# be live on next page load without any of this -- this script only matters for server.js (or
# anything else that needs the process restarted to pick up).
#
# Usage: ./scripts/deploy-to-pi.sh
# Assumes: commits are already made locally, your SSH key is already authorized on the Pi (see
# README's Raspberry Pi Migration section), and the Pi is reachable either at tooltracker.local
# (mDNS, shop LAN only) or tooltracker-pi (Tailscale MagicDNS, works from anywhere Tailscale is
# connected, e.g. a laptop off the shop network) -- whichever resolves first is used.

set -e

if timeout 2 bash -c ": < /dev/tcp/tooltracker.local/22" 2>/dev/null; then
    PI_HOST="tooltracker@tooltracker.local"
else
    PI_HOST="tooltracker@tooltracker-pi"
fi
PI_DIR="~/ToolTracker_Server"

echo "==> Pushing to GitHub..."
git push origin main

echo "==> Pulling on the Pi and restarting the service..."
ssh "$PI_HOST" "cd $PI_DIR && git pull && sudo systemctl restart tooltracker"

echo "==> Done. The Pi is now running the latest commit."
