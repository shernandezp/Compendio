<#
.SYNOPSIS
    Installs Compendio as a Windows Service.

.DESCRIPTION
    A wrapper around `compendio.exe install`, plus the one thing the executable cannot do for
    itself: grant its service account write access to a data directory that lives somewhere other
    than beside the binary.

    The service runs as the virtual account NT SERVICE\Compendio and never as LocalSystem. It needs
    exactly one thing on the machine — its data directory — and nothing else.

    `compendio.exe uninstall` removes the service and leaves the data untouched. An uninstaller that
    deleted somebody's wiki because they wanted to move it to another machine is not a supported
    outcome.

.EXAMPLE
    ./install-windows-service.ps1 -InstallPath 'C:\Program Files\Compendio' -DataDir 'D:\CompendioData'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallPath,
    [string]$DataDir,
    [int]$Port = 8080
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell prompt.'
}

$executable = Join-Path $InstallPath 'compendio.exe'
if (-not (Test-Path $executable)) {
    throw "compendio.exe is not in '$InstallPath'. Copy it there first."
}

if (-not $DataDir) {
    $DataDir = Join-Path $InstallPath 'data'
}

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

# The virtual service account. Granting it write access here is why the "data directory not
# writable" startup guard normally never fires.
$account = 'NT SERVICE\Compendio'
Write-Host "Granting '$account' write access to '$DataDir'…" -ForegroundColor Cyan
& icacls $DataDir /grant "${account}:(OI)(CI)M" /T | Out-Null

if ($Port -ne 8080) {
    [Environment]::SetEnvironmentVariable('Urls', "http://0.0.0.0:$Port", 'Machine')
}

[Environment]::SetEnvironmentVariable('DataDir', $DataDir, 'Machine')

Write-Host 'Installing the service…' -ForegroundColor Cyan
& $executable install

Write-Host ''
Write-Host "Compendio is installed and running on http://localhost:$Port" -ForegroundColor Green
Write-Host "  Data directory: $DataDir"
Write-Host '  Logs:           Event Viewer → Windows Logs → Application, source "Compendio"'
Write-Host ''
Write-Host 'If the firewall should let other machines in:' -ForegroundColor Yellow
Write-Host "  New-NetFirewallRule -DisplayName 'Compendio' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow"
