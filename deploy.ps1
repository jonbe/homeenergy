<#
.SYNOPSIS
    Copies the NAS-side files to the NAS, and nothing else.

.DESCRIPTION
    HomeEnergy's image is built by GitHub Actions on every push to main and
    published to GHCR; the NAS only pulls it (update.sh). So unlike SoundWall
    and photowall there is no build context to send: this copies the compose
    file, update.sh, the two .env templates and the deploy doc, then reads the
    NAS's .env and tunnel.env and says so if update.sh would refuse to run
    with them (no password or a short one while the tunnel is on, no inverter
    IP, no tunnel token). Secret values are never printed.

    It also checks that main has no unpushed commits: the code the NAS runs is
    whatever CI built last, so deploying the compose file with commits still
    sitting here would run old code with a new compose.

    .env, tunnel.env and data/ on the NAS are never touched. They hold the
    password, the API secrets, the tunnel token and the history database, and
    belong to that machine.

    -Image builds the Docker image on THIS PC and sends it as
    homeenergy-image.tar; update.sh on the NAS then loads and runs that instead
    of pulling from GHCR. For trying a change without waiting for CI (or
    pushing at all). The dashboard header shows it as <sha>-dev. A deploy
    WITHOUT -Image removes a leftover homeenergy-image.tar from the NAS, so a
    stale local build can never be loaded over newer code; the next plain
    update.sh on the NAS returns to the GHCR image.

.EXAMPLE
    .\deploy.ps1 -DryRun
    .\deploy.ps1
    .\deploy.ps1 -Image           # build here, send the image, skip CI
#>
param(
    [string]$Target = '\\10.30.50.5\docker\homeenergy',
    [switch]$DryRun,
    [switch]$Image
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# What the NAS needs next to its data folder.
$files = @(
    'docker-compose.yml'
    'update.sh'
    '.env.example'           # template for .env on the NAS
    'tunnel.env.example'     # template for tunnel.env on the NAS
    'docs/deploy.md'
)

# ---------------------------------------------------------------- line endings
# A shell script with CRLF endings fails on Linux with a shebang error that
# names the interpreter and looks nothing like a line-ending problem.
foreach ($sh in ($files | Where-Object { $_ -like '*.sh' })) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root $sh))
    for ($i = 0; $i -lt $bytes.Length - 1; $i++) {
        if ($bytes[$i] -eq 13 -and $bytes[$i + 1] -eq 10) {
            Write-Host "$sh has CRLF line endings and will not run on the NAS." -ForegroundColor Red
            Write-Host "Fix with:  git add --renormalize $sh" -ForegroundColor Yellow
            exit 1
        }
    }
}

# ---------------------------------------------------------------- local check
$absent = $files | Where-Object { -not (Test-Path (Join-Path $root $_)) }
if ($absent) {
    Write-Host 'Missing locally, cannot deploy:' -ForegroundColor Red
    $absent | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    exit 1
}

# The NAS pulls what CI built from the pushed main; local commits are not in it.
try {
    $dirty = git -C $root status --porcelain 2>$null
    if ($dirty) { Write-Warning "Working tree has uncommitted changes - deploying the committed files anyway." }
    git -C $root fetch --quiet origin main 2>$null
    $ahead = [int](git -C $root rev-list --count origin/main..HEAD 2>$null)
    if ($ahead -gt 0 -and -not $Image) {
        Write-Warning "main is $ahead commit(s) ahead of origin/main. The NAS will keep running the LAST PUSHED build;"
        Write-Warning "push first (git push) and wait for the 'Publish container' action, or use -Image to send a local build."
    }
} catch { }

# ---------------------------------------------------------------- reach the share
if (-not (Test-Path $Target)) {
    $parent = Split-Path $Target -Parent
    if (-not (Test-Path $parent)) {
        Write-Host "Cannot reach $parent" -ForegroundColor Red
        Write-Host 'Check the NAS is on, SMB is enabled, and you are authenticated:' -ForegroundColor Yellow
        Write-Host "    net use $parent /user:<nas-user>" -ForegroundColor Yellow
        exit 1
    }
    if ($DryRun) { Write-Host "would create $Target" -ForegroundColor DarkGray }
    else { New-Item -ItemType Directory -Path $Target | Out-Null }
}

Write-Host ""
Write-Host "  from  $root"
Write-Host "  to    $Target"
if ($DryRun) { Write-Host "  (dry run - nothing is written)" -ForegroundColor DarkGray }
Write-Host ""

# ---------------------------------------------------------------- copy
$sent = 0
foreach ($f in $files) {
    $src = Join-Path $root $f
    $dst = Join-Path $Target (Split-Path $f -Leaf)     # docs/deploy.md lands as deploy.md
    $same = (Test-Path $dst) -and
            ((Get-FileHash $src -Algorithm MD5).Hash -eq (Get-FileHash $dst -Algorithm MD5).Hash)
    if ($same) { Write-Host ("  ={0}" -f $f) -ForegroundColor DarkGray; continue }
    Write-Host ("  >{0}" -f $f) -ForegroundColor Green
    if (-not $DryRun) { Copy-Item $src $dst -Force }
    $sent++
}

# ---------------------------------------------------------------- prebuilt image
$imageOnNas = Join-Path $Target 'homeenergy-image.tar'
if ($Image) {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Host '-Image needs Docker on this PC (Docker Desktop), and "docker" was not found.' -ForegroundColor Red
        exit 1
    }
    docker info 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host '-Image needs the Docker engine running on this PC; start Docker Desktop and run again.' -ForegroundColor Red
        exit 1
    }
    # Identify the build in the dashboard header, as CI does, so it is obvious
    # a local image is running and which commit it came from.
    $sha  = (git -C $root rev-parse --short HEAD 2>$null) + '-dev'
    $time = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    if ($DryRun) {
        Write-Host "  (would build homeenergy:dev here as $sha and send it as homeenergy-image.tar)" -ForegroundColor DarkGray
    } else {
        Write-Host "  building homeenergy:dev ($sha) on this PC ..." -ForegroundColor Green
        docker build --platform linux/amd64 -t homeenergy:dev --build-arg "BUILD_SHA=$sha" --build-arg "BUILD_TIME=$time" $root
        if ($LASTEXITCODE -ne 0) { Write-Host 'docker build failed' -ForegroundColor Red; exit 1 }

        # saved locally first: writing a big file straight onto an SMB share is slower and
        # can leave a half-written file behind if it is interrupted
        $tmp = Join-Path ([IO.Path]::GetTempPath()) 'homeenergy-image.tar'
        docker save homeenergy:dev -o $tmp
        if ($LASTEXITCODE -ne 0) { Write-Host 'docker save failed' -ForegroundColor Red; exit 1 }
        $mb = [math]::Round((Get-Item $tmp).Length / 1MB)
        Write-Host "  sending homeenergy-image.tar ($mb MB) ..." -ForegroundColor Green
        Copy-Item $tmp $imageOnNas -Force
        Remove-Item $tmp -Force
        $sent++
    }
} elseif (Test-Path $imageOnNas) {
    # this deploy runs the CI image; an old local build lying there would be loaded over it
    if ($DryRun) { Write-Host '  (would remove a leftover homeenergy-image.tar from the NAS)' -ForegroundColor DarkGray }
    else {
        Remove-Item $imageOnNas -Force
        Write-Host '  removed a leftover homeenergy-image.tar from the NAS (this deploy uses the GHCR image).' -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------- the NAS-only files
# Read what update.sh will check and say so now, instead of on the NAS. Only
# ever reports problems; never prints a value.
Write-Host ""
$errors = @()
$notes  = @()

$compose = Get-Content (Join-Path $root 'docker-compose.yml') -Raw
$tunnelOn = $compose -match '(?m)^ {2}cloudflared:'

$envPath = Join-Path $Target '.env'
if (Test-Path $envPath) {
    Write-Host "  .env already on the NAS - left untouched." -ForegroundColor DarkGray
    $envText = Get-Content $envPath -Raw
    if ($envText -notmatch '(?m)^INVERTER_IP=\S+') {
        $notes += 'INVERTER_IP is not set in .env: the image default (appsettings.json) is used'
    }
    $pw = if ($envText -match '(?m)^APP_AUTH_TOKEN=(.*)$') { $Matches[1].Trim() } else { '' }
    if ($pw.Length -eq 0) {
        if ($tunnelOn) { $errors += 'APP_AUTH_TOKEN is empty in .env and the tunnel is on: the whole internet could change the inverter. update.sh refuses this.' }
        else           { $notes  += 'APP_AUTH_TOKEN is empty: no login screen (fine on the LAN only)' }
    } elseif ($pw.Length -lt 12) {
        if ($tunnelOn) { $errors += "APP_AUTH_TOKEN is only $($pw.Length) characters; update.sh wants at least 12 before exposing the dashboard" }
        else           { $notes  += "APP_AUTH_TOKEN is only $($pw.Length) characters; make it longer before turning the tunnel on" }
    }
    if ($envText -notmatch '(?m)^TIBBER_TOKEN=\S+') {
        $notes += 'TIBBER_TOKEN is not set: no prices, so the plan is plain General'
    }
} else {
    $errors += 'No .env on the NAS. Create it there:  cp .env.example .env  (then fill it in; see deploy.md)'
}

# the tunnel container reads tunnel.env, and compose refuses to start while it is missing
if ($tunnelOn) {
    $tunnelPath = Join-Path $Target 'tunnel.env'
    if (-not (Test-Path $tunnelPath)) {
        $errors += 'docker-compose.yml enables the cloudflared service, but there is no tunnel.env on the NAS (cp tunnel.env.example tunnel.env, then paste the token). update.sh refuses to run until it exists.'
    } elseif ((Get-Content $tunnelPath -Raw) -notmatch '(?m)^TUNNEL_TOKEN=\S+') {
        $errors += 'tunnel.env on the NAS has no TUNNEL_TOKEN. Put the token after the = and run again.'
    } else {
        Write-Host "  tunnel.env already on the NAS - left untouched." -ForegroundColor DarkGray
    }
}

if (-not (Test-Path (Join-Path $Target 'data'))) {
    $notes += 'No data/ folder on the NAS yet - Docker creates it on first start (history.db, tokens and the login key ring live there)'
}

if ($errors.Count) {
    Write-Host ""
    Write-Host "  Fix on the NAS before updating:" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
}
if ($notes.Count) {
    Write-Host ""
    $notes | ForEach-Object { Write-Host "  note: $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "$sent file(s) $(if ($DryRun) {'would be '} else {''})sent." -ForegroundColor Cyan
Write-Host ""
Write-Host "Then on the NAS:" -ForegroundColor Cyan
Write-Host "    cd /volume1/docker/homeenergy"
Write-Host "    sudo sh update.sh" -ForegroundColor White
Write-Host ""
Write-Host "  ($(if ($Image) {'loads the PC-built image'} else {'pulls the newest GHCR image'}), restarts, checks the app answers and the tunnel connected)" -ForegroundColor DarkGray
Write-Host "  (SMB does not carry the executable bit, hence 'sh update.sh')" -ForegroundColor DarkGray
Write-Host ""

exit 0
