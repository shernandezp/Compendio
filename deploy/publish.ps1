<#
.SYNOPSIS
    Publishes Compendio for every supported runtime, with checksums.

.DESCRIPTION
    Self-contained, single-file, untrimmed. Trimming breaks EF Core, and ~100 MB is the accepted
    price of "download one file and run it" — the alternative is telling an SMB admin to install a
    runtime first, which is the thing this product refuses to do.

    Releases ship unsigned, by decision. SmartScreen will show "Windows protected your PC" on first
    run; the README documents that next to the SHA-256 checksums this script writes, because a
    published checksum is the honest substitute for a signature.

.EXAMPLE
    ./publish.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$OutputRoot = "$PSScriptRoot/../artifacts",
    [string[]]$Runtimes = @('win-x64', 'linux-x64', 'linux-arm64')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/Server/Compendio.Server.csproj'

Write-Host "Building the client once, for every runtime to share…" -ForegroundColor Cyan
Push-Location (Join-Path $PSScriptRoot '../src/client')
try {
    npm ci --no-audit --no-fund
    npm run check:i18n
    npm run build
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$checksums = @()

foreach ($runtime in $Runtimes) {
    $output = Join-Path $OutputRoot $runtime
    Write-Host "Publishing $runtime…" -ForegroundColor Cyan

    dotnet publish $project `
        -c Release `
        -r $runtime `
        -o $output `
        -p:SelfContainedPublish=true `
        -p:SkipClientBuild=true `
        -p:Version=$Version `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $runtime." }

    $executable = if ($runtime -like 'win-*') { 'compendio.exe' } else { 'compendio' }
    $binary = Join-Path $output $executable

    if (-not (Test-Path $binary)) { throw "Expected $binary to exist after publishing." }

    # One archive per runtime, named so a download is self-describing.
    $archive = Join-Path $OutputRoot "compendio-$Version-$runtime.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive

    $hash = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $(Split-Path $archive -Leaf)"

    $size = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "  $(Split-Path $archive -Leaf) — $size MB" -ForegroundColor Green
}

# Published beside the artifacts, because the binaries are unsigned and this is what an admin
# verifies instead.
$checksumFile = Join-Path $OutputRoot 'SHA256SUMS.txt'
$checksums | Set-Content -Path $checksumFile -Encoding utf8

Write-Host ""
Write-Host "Wrote $checksumFile" -ForegroundColor Cyan
Get-Content $checksumFile | Write-Host

Write-Host ""
Write-Host "Reminder: these binaries are unsigned. The README documents the SmartScreen dialog." -ForegroundColor Yellow
