# Running HomeEnergy on the NAS, and reaching it from the internet

The image is built by GitHub Actions on every push to `main` and published as
`ghcr.io/jonbe/homeenergy:latest`. The NAS only pulls it. Everything the app
stores (SQLite history, Tibber tokens, PR calibration, the login master key)
lives in one folder on the NAS.

## 1. Files on the NAS

In `/volume1/docker/homeenergy`, next to a `data/` folder:

| File | What |
| --- | --- |
| `docker-compose.yml` | from this repo |
| `.env` | from `.env.example`: inverter IP, the exact solar location (`SOLAR_LATITUDE`, `SOLAR_LONGITUDE`; the image only knows a rounded place), `APP_AUTH_TOKEN`, Tibber and Charge Amps secrets. Never committed. |
| `tunnel.env` | from `tunnel.env.example`: the Cloudflare Tunnel token, alone. Never committed. |
| `update.sh` | pull the newest image and restart (section 3) |

From the PC, `.\deploy.ps1` copies the repo's side of that table (the compose
file, `update.sh`, the two `.env.example` templates and this doc) to
`\\10.30.50.5\docker\homeenergy` (`-Target` to change, `-DryRun` to preview).
It never touches `.env`, `tunnel.env` or `data/`; it reads the first two and
says so if `update.sh` would refuse to run with them, without printing values.
It also warns when `main` has commits that are not pushed yet, because the NAS
runs whatever CI built from the pushed `main`, not what is on the PC.

## 2. Reach it from the internet: Cloudflare Tunnel

The dashboard can switch the inverter's mode and grid-charge the battery, so on
the internet it must sit behind the login screen (`APP_AUTH_TOKEN`). The tunnel
gives it a public https address with no router port opened and your home IP not
exposed; Cloudflare terminates https, so the NAS needs no certificate.

1. **Set a long password first.** `APP_AUTH_TOKEN` in `.env`, at least 12
   characters (`update.sh` refuses to start the tunnel with less). It is entered
   once per browser; a 30-day encrypted session cookie follows. Wrong guesses
   are slowed and locked out per address after 10 in 15 minutes. Stick to
   ASCII (the file may be edited from Windows, and compose's `.env` treats `$`
   and an unquoted `#` specially). To change it later: edit `.env`, run
   `update.sh`; every browser then signs in again, and any script sending the
   old value as `X-Api-Key` must be updated.
2. Cloudflare dashboard → **Zero Trust → Networks → Tunnels → Create a tunnel →
   Cloudflared**, name it, copy the **token** (the long string after `--token`).
3. On the NAS, next to `docker-compose.yml`:
   ```sh
   cp tunnel.env.example tunnel.env     # paste the bare token after TUNNEL_TOKEN=
   sudo sh update.sh
   ```
   Not the whole install command, no quotes. The token lives in its own file so
   the tunnel container never sees `.env`. The script checks the tunnel
   connected and prints cloudflared's own log lines if it did not.
4. In the tunnel's **Public Hostname** tab add the hostname you want (e.g.
   `energy.addvt.io`) with service type **HTTP** and URL `homeenergy:8080`:
   the compose service name and the app's port *inside* the container, not the
   5034 published on the NAS. Cloudflare adds the DNS record itself; a name
   that already has one must have it deleted first. Keep the name one level
   below the domain (`energy.addvt.io`, not `home.energy.addvt.io`) so the free
   certificate covers it.

**A second gate (recommended).** In Zero Trust create an **Access** application
for the hostname with a policy that allows only your email (one-time PIN). Then
Cloudflare asks before a request even reaches the NAS, and the password is the
second lock rather than the only one. Free for up to 50 users. If you add it,
exempt `/tibber/callback` (Tibber's OAuth redirect cannot sign in) by giving
that path a bypass policy, or do the Tibber connect step on the LAN address.

Without a tunnel (LAN only) comment the whole `cloudflared` service out of
`docker-compose.yml`; `update.sh` then skips every tunnel step.

## 3. Update

```sh
cd /volume1/docker/homeenergy
sudo sh update.sh
```

Pulls the newest image, restarts, checks `/api/status` answers (401 with a
password set, 200 without) and that the tunnel reconnected. `.env`,
`tunnel.env` and `data/` are untouched. The dashboard header shows the running
build's git sha, so you can see the new one landed.

## 4. Trying a change without CI

```
.\deploy.ps1 -Image
```

builds the image on the PC (Docker Desktop must be running), saves it as
`homeenergy-image.tar` next to the compose file on the NAS, and `sudo sh
update.sh` there loads and runs it instead of pulling from GHCR. Nothing needs
to be pushed. The dashboard header shows the build as `<sha>-dev` so it is
obvious a local image is running. The image is amd64, which is what the NAS
is; it is about 250 MB over SMB.

To go back to the CI image: push, wait for the action, then a plain
`.\deploy.ps1` (which also deletes a leftover tar on the NAS) and `sudo sh
update.sh`. Container Manager restarts keep whatever image the container was
created with; only `update.sh` switches.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| `update.sh` stops with `tunnel.env is missing` | create it from `tunnel.env.example` (step 3); the app was not stopped |
| `Unauthorized: Invalid tunnel secret` in the tunnel's log | the token is stale (tunnel recreated, or token refreshed) or copied wrong; copy a fresh one into `tunnel.env` and run `update.sh` again |
| `Provided Tunnel token is not valid` | `tunnel.env` holds more than the bare token |
| Tunnel connected but the site shows a Cloudflare error (1033, 502, 530) | the Public Hostname is missing or wrong: it must point at HTTP `homeenergy:8080` |
| The tunnel never connects | the NAS cannot reach Cloudflare; outbound port 7844 (tcp and udp) must be open |
| Signed in on the LAN address but asked again on the public one | expected: the cookie is per hostname. Sign in once on each. |
| Everyone is asked to sign in again | expected after changing `APP_AUTH_TOKEN`: a fingerprint of the password is part of every session, so a new password ends all sessions (that is the point of rotating it). Also after an app update that bumps the ticket version. |
| `Too many attempts` on the login page | 10 wrong passwords from your address within 15 minutes; wait, or restart the container |
