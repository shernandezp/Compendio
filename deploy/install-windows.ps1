<#
.SYNOPSIS
    Installs Compendio on Windows, start to finish.

.DESCRIPTION
    One command, and it asks rather than assumes. It creates the data folder, registers the
    Windows Service, grants the service account the one permission it needs, optionally opens the
    firewall, starts everything, waits until the wiki actually answers, and prints the address and
    the administrator credentials to sign in with.

    Everything it asks has a default, so pressing Enter through it is a valid install. Pass any
    parameter to skip that question, or -Unattended to accept every default silently.

    Safe to run twice: an existing service is reused, an existing data folder is left alone, and an
    instance that already has an administrator is never given a second one.

.PARAMETER DataDir
    Where pages, database, logs and keys live. Never put this inside Program Files — the service
    account cannot write there and Compendio will refuse to start rather than fail quietly later.

.PARAMETER Port
    The port to serve on. Default 8080.

.PARAMETER OpenFirewall
    Allow other machines on the network to reach the wiki.

.PARAMETER Unattended
    Ask nothing, take every default.

.EXAMPLE
    .\install-windows.ps1

.EXAMPLE
    .\install-windows.ps1 -DataDir 'D:\CompendioData' -Port 8080 -OpenFirewall -Unattended
#>
[CmdletBinding()]
param(
    [string]$InstallPath = $PSScriptRoot,
    [string]$DataDir,
    [int]$Port = 8080,
    [switch]$OpenFirewall,
    [switch]$Unattended
)

$ErrorActionPreference = 'Stop'

# PowerShell 7.4 turns a non-zero exit from a native command into a terminating error by default,
# 5.1 never does. Both are in the field on Windows Server, so the difference is switched off and
# every native call below checks $LASTEXITCODE itself — one behaviour on both.
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ServiceName = 'Compendio'
$ServiceAccount = 'NT SERVICE\Compendio'

function Write-Step { param([string]$Text) Write-Host "`n$Text" -ForegroundColor Cyan }
function Write-Ok { param([string]$Text) Write-Host "  $Text" -ForegroundColor Green }
function Write-Note { param([string]$Text) Write-Host "  $Text" -ForegroundColor DarkGray }

function Read-WithDefault {
    param([string]$Question, [string]$Default)

    if ($Unattended) { return $Default }

    $answer = Read-Host "$Question [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim()
}

function Read-YesNo {
    param([string]$Question, [bool]$Default)

    if ($Unattended) { return $Default }

    $hint = if ($Default) { 'Y/n' } else { 'y/N' }
    $answer = Read-Host "$Question [$hint]"

    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim().ToLowerInvariant().StartsWith('y')
}

<#
    A password a person has to read off a console and type into a browser once. Ambiguous
    characters are left out on purpose — nobody should lose ten minutes to an l that was a 1.
#>
function New-Password {
    $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $bytes = [byte[]]::new(24)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)

    return -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

# ---------------------------------------------------------------------------------------------

Write-Host ''
Write-Host '  Compendio — Windows installer' -ForegroundColor White
Write-Host '  ─────────────────────────────' -ForegroundColor DarkGray

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host ''
    Write-Host '  This needs an administrator PowerShell window.' -ForegroundColor Red
    Write-Host '  Right-click the Start button, choose "Terminal (Admin)", then run it again.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

$executable = Join-Path $InstallPath 'compendio.exe'
if (-not (Test-Path $executable)) {
    Write-Host ''
    Write-Host "  compendio.exe is not in '$InstallPath'." -ForegroundColor Red
    Write-Host '  Put this script in the folder you unzipped, or pass -InstallPath.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

# A zip downloaded through a browser marks every file inside it, which is what makes the first run
# show "Windows protected your PC". Clearing it here saves explaining that later.
Write-Step 'Preparing the files'
Get-ChildItem -Path $InstallPath -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
Write-Ok 'Files unblocked.'

# --- Where the data goes ----------------------------------------------------------------------

Write-Step 'Where should your pages and database live?'
Write-Note 'This is the folder you back up. Keep it off the system drive if you have another one.'

if (-not $DataDir) {
    $suggested = if (Test-Path 'D:\') { 'D:\CompendioData' } else { 'C:\CompendioData' }
    $DataDir = Read-WithDefault -Question '  Data folder' -Default $suggested
}

if ($DataDir -like "$env:ProgramFiles*" -or $DataDir -like "$(${env:ProgramFiles(x86)})*") {
    Write-Host ''
    Write-Host '  That is inside Program Files, where the service account cannot write.' -ForegroundColor Red
    Write-Host '  Compendio would refuse to start. Choose somewhere else, such as C:\CompendioData.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

# Whether this instance is brand new decides if credentials get created and printed below.
$databaseFile = Join-Path $DataDir 'db\compendio.db'
$isFreshInstall = -not (Test-Path $databaseFile)

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Write-Ok "Using $DataDir"

# --- Port -------------------------------------------------------------------------------------

Write-Step 'Which port should it serve on?'

while (-not $PSBoundParameters.ContainsKey('Port')) {
    $answer = Read-WithDefault -Question '  Port' -Default '8080'

    if ([int]::TryParse($answer, [ref]$Port) -and $Port -gt 0 -and $Port -le 65535) {
        break
    }

    Write-Host '  That is not a port number. Try something like 8080.' -ForegroundColor Yellow
}

$inUse = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($inUse) {
    $owner = (Get-Process -Id $inUse[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName
    Write-Host ''
    Write-Host "  Port $Port is already in use by '$owner'." -ForegroundColor Red
    Write-Host '  Choose another port, or stop that program, and run this again.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Ok "Port $Port is free."

# --- Firewall ---------------------------------------------------------------------------------

Write-Step 'Should other computers be able to reach this wiki?'
Write-Note 'Say no if only this machine will use it. You can change it later.'

if (-not $PSBoundParameters.ContainsKey('OpenFirewall')) {
    $OpenFirewall = Read-YesNo -Question '  Open the firewall' -Default $true
}

# --- Configuration ----------------------------------------------------------------------------

Write-Step 'Registering the service'

[Environment]::SetEnvironmentVariable('DataDir', $DataDir, 'Machine')

if ($Port -ne 8080) {
    [Environment]::SetEnvironmentVariable('Urls', "http://0.0.0.0:$Port", 'Machine')
}

<#
    The administrator account, for a fresh install only.

    Compendio creates this from configuration at startup and only when no account exists at all,
    so re-running this script can never mint a second administrator or reset an existing one. The
    values are set as machine variables because that is the only channel the service reads at
    start, and they are removed again as soon as the account exists — a password left in the
    registry is a password somebody finds a year later.
#>
$password = $null

if ($isFreshInstall) {
    $password = New-Password
    [Environment]::SetEnvironmentVariable('Bootstrap__AdminUser', 'admin', 'Machine')
    [Environment]::SetEnvironmentVariable('Bootstrap__AdminPassword', $password, 'Machine')
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($service) {
    Write-Note 'The service already exists — reusing it.'
    if ($service.Status -eq 'Running') { Stop-Service -Name $ServiceName -Force }
}
else {
    # This both creates and tries to start the service. The start can fail here, because the
    # service account does not exist until the service does, so its permissions are granted below.
    & $executable install | Out-Null

    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Write-Host ''
        Write-Host '  The service could not be created.' -ForegroundColor Red
        Write-Host ''
        exit 1
    }

    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Write-Ok 'Service registered.'
}

# --- Permissions ------------------------------------------------------------------------------
#
# After the service exists, never before: NT SERVICE\Compendio is a virtual account that cannot be
# resolved until the service it belongs to has been created.

Write-Step 'Granting the service access to its data folder'

$icacls = & icacls $DataDir /grant "${ServiceAccount}:(OI)(CI)M" /T 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host "  Could not grant '$ServiceAccount' access to '$DataDir'." -ForegroundColor Red
    Write-Host "  $icacls" -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Ok "$ServiceAccount can write to $DataDir"

# --- Firewall rule ----------------------------------------------------------------------------

if ($OpenFirewall) {
    Write-Step 'Opening the firewall'

    $ruleName = "Compendio (port $Port)"
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP `
        -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null

    Write-Ok "Inbound TCP $Port allowed on domain and private networks."
    Write-Note 'Public networks are deliberately left closed.'
}

# --- Start ------------------------------------------------------------------------------------

Write-Step 'Starting Compendio'

# Not fatal on its own: a service that fails to come up has something specific to say, and the
# diagnostics below say it far better than a thrown ServiceCommandException would.
try {
    Start-Service -Name $ServiceName
}
catch {
    Write-Note "The service did not start cleanly: $($_.Exception.Message)"
}

$address = "http://localhost:$Port"
$ready = $false

# Up to a minute: the very first start runs database migrations and builds the search schema.
foreach ($attempt in 1..120) {
    try {
        $response = Invoke-WebRequest -Uri "$address/health" -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) { $ready = $true; break }
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}

# The password has done its job the moment the account exists. Clear it either way: on failure it
# is equally undesirable to leave it behind, and the account will be created on the next start.
[Environment]::SetEnvironmentVariable('Bootstrap__AdminUser', $null, 'Machine')
[Environment]::SetEnvironmentVariable('Bootstrap__AdminPassword', $null, 'Machine')

if (-not $ready) {
    Write-Host ''
    Write-Host '  Compendio was installed but is not answering yet.' -ForegroundColor Yellow
    Write-Host '  Check what it says about itself:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "    & '$executable' doctor" -ForegroundColor White
    Write-Host ''
    Write-Host '  Logs: Event Viewer -> Windows Logs -> Application, source "Compendio"' -ForegroundColor DarkGray
    Write-Host "        $DataDir\logs" -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}

# --- Done -------------------------------------------------------------------------------------

$hostAddress = "http://$($env:COMPUTERNAME.ToLower()):$Port"

Write-Host ''
Write-Host '  ─────────────────────────────────────────────' -ForegroundColor DarkGray
Write-Host '   Compendio is running.' -ForegroundColor Green
Write-Host '  ─────────────────────────────────────────────' -ForegroundColor DarkGray
Write-Host ''
Write-Host "   On this machine:  $address" -ForegroundColor White

if ($OpenFirewall) {
    Write-Host "   From the network: $hostAddress" -ForegroundColor White
}

if ($isFreshInstall) {
    Write-Host ''
    Write-Host '   Sign in with' -ForegroundColor White
    Write-Host '     User      admin' -ForegroundColor Yellow
    Write-Host "     Password  $password" -ForegroundColor Yellow
    Write-Host ''
    Write-Host '   Write this down now — it is not stored anywhere and will not be' -ForegroundColor DarkGray
    Write-Host '   shown again. Change it under Profile once you are in.' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '   Forgotten it later?' -ForegroundColor DarkGray
    Write-Host "     & '$executable' reset-admin-password --password '<new one>'" -ForegroundColor DarkGray
}
else {
    Write-Host ''
    Write-Host '   This instance already had an administrator, so no new account was created.' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host "   Data folder  $DataDir" -ForegroundColor DarkGray
Write-Host '   Back that folder up and you have backed up your wiki.' -ForegroundColor DarkGray
Write-Host ''
Write-Host '   The service starts automatically with Windows.' -ForegroundColor DarkGray
Write-Host "   To remove it:  & '$executable' uninstall   (your data is left alone)" -ForegroundColor DarkGray
Write-Host ''

if (-not $Unattended) {
    if (Read-YesNo -Question '  Open it in a browser now' -Default $true) {
        Start-Process $address
    }
}
