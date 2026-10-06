#!/bin/sh
# Update and restart HomeEnergy on the NAS.
#
#   cd /volume1/docker/homeenergy
#   sudo sh update.sh
#
# The image is built by GitHub Actions on every push to main and published to
# GHCR, so this only pulls the newest one and restarts; nothing is built here.
# One command, so sudo asks once. (`sh update.sh` because the SMB share does
# not carry the executable bit.)
set -e

cd "$(dirname "$0")"

# the compose plugin on DSM 7.2, the standalone docker-compose binary on older DSM
if docker compose version >/dev/null 2>&1; then DC="docker compose"; else DC="docker-compose"; fi

# Is the Cloudflare tunnel enabled in the compose file?
TUNNEL=no
if grep -qE '^  cloudflared:' docker-compose.yml 2>/dev/null; then TUNNEL=yes; fi

# Compose refuses to start anything while tunnel.env is missing, and by then the
# running app has already been stopped. So check first, while it is still up.
if [ "$TUNNEL" = "yes" ]; then
  if [ ! -f tunnel.env ]; then
    echo "tunnel.env is missing, and docker-compose.yml enables the Cloudflare tunnel."
    echo "    cp tunnel.env.example tunnel.env     # then paste the tunnel token in (see docs/deploy.md)"
    echo "Or comment the cloudflared service out of docker-compose.yml to run without a tunnel."
    exit 1
  fi
  if ! grep -qE '^TUNNEL_TOKEN=[^[:space:]]' tunnel.env; then
    echo "tunnel.env has no TUNNEL_TOKEN. Put the token after the = (the string after --token,"
    echo "not the whole install command, no quotes)."
    exit 1
  fi
  # With the tunnel on, the dashboard is on the internet: the password is the only gate.
  if ! grep -qE '^APP_AUTH_TOKEN=.{12,}' .env 2>/dev/null; then
    echo "APP_AUTH_TOKEN in .env is missing or shorter than 12 characters, and the tunnel"
    echo "makes the dashboard public. Set a long password before exposing it."
    exit 1
  fi
fi

echo "==> pulling"
$DC pull

echo "==> starting"
$DC up -d

echo "==> checking"
sleep 4

PORT=$(grep -oE '"[0-9]+:8080"' docker-compose.yml | head -1 | tr -d '"' | cut -d: -f1)
PORT=${PORT:-5034}

# With a password set the API answers 401 to a stranger; without one, 200.
code=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:${PORT}/api/status" || echo 000)
printf '    %-14s %s\n' "/api/status" "$code"
case "$code" in
  200) echo "    note: no password set (APP_AUTH_TOKEN empty) - fine on the LAN only" ;;
  401) ;;
  *)   echo "==> the app did not come up; $DC logs --tail=40 homeenergy"; exit 1 ;;
esac
echo "==> up on port ${PORT}"

# The Cloudflare tunnel is connected once cloudflared logs a registered connection. A wrong or
# revoked token, or no route out to Cloudflare, never gets there.
if [ "$TUNNEL" = "yes" ]; then
  echo "==> checking the Cloudflare tunnel"
  connected=no
  i=0
  while [ "$i" -lt 15 ]; do
    if $DC logs cloudflared 2>&1 | grep -q 'Registered tunnel connection'; then connected=yes; break; fi
    sleep 2
    i=$((i + 1))
  done
  if [ "$connected" = "yes" ]; then
    echo "    tunnel: connected"
  else
    echo "    tunnel: not connected after 30 s. Last log lines:"
    tlog=$($DC logs --tail=40 cloudflared 2>&1 || true)
    printf '%s\n' "$tlog" | tail -15
    if printf '%s' "$tlog" | grep -q 'Invalid tunnel secret'; then
      echo "    -> Cloudflare knows this tunnel but rejects the secret in the token: the token is stale"
      echo "       (refreshed, or the tunnel was recreated) or was copied wrong. Copy a fresh one from"
      echo "       Zero Trust > Networks > Tunnels > your tunnel > Docker into tunnel.env."
    elif printf '%s' "$tlog" | grep -q 'token is not valid'; then
      echo "    -> tunnel.env does not hold a bare tunnel token (the whole install command? quotes?)."
    elif printf '%s' "$tlog" | grep -qiE 'no such host|i/o timeout|connection refused|network is unreachable'; then
      echo "    -> the NAS cannot reach Cloudflare: check its internet access and DNS, and that outbound"
      echo "       port 7844 (tcp and udp) is allowed."
    else
      echo "    -> see the log lines above; the usual causes are a wrong or revoked token, or no route"
      echo "       from the NAS to Cloudflare (outbound port 7844, tcp and udp)."
    fi
    echo "==> the app is up, but the tunnel is not connected"
    exit 1
  fi
fi
