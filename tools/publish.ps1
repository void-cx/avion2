<#
  Builds the portable AVION2 folder: AVION2.exe + AVION2-Updater.exe + licenses.

    tools/publish.ps1                        # artifacts/AVION2 (dev version)
    tools/publish.ps1 -Version 0.2.0 -Zip    # + artifacts/AVION2-v0.2.0-win-x64.zip and artifacts/SHA256SUMS.txt

  Used by .github/workflows/release.yml, so a local run produces exactly what a release ships.
#>
param(
    [string]$Version,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\AVION2'
$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

foreach ($project in 'src/Avion2.Overlay', 'src/Avion2.Updater') {
    dotnet publish (Join-Path $root $project) -p:PublishProfile=Portable "-p:PublishDir=$out\" @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "publish of $project failed" }
}

Write-Host "Published to $out"
Get-ChildItem $out -Recurse -File | ForEach-Object { '{0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($out.Length + 1) }

if ($Zip) {
    if (-not $Version) { throw '-Zip needs -Version' }
    $name = "AVION2-v$Version-win-x64.zip"
    $zipPath = Join-Path $root "artifacts\$name"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    # Same format as sha256sum: "<hex>  <file name>" (AVION2-Updater checks it)
    Set-Content -Path (Join-Path $root 'artifacts\SHA256SUMS.txt') -Value "$hash  $name" -Encoding ascii
    Write-Host "Zip: $zipPath ($hash)"
}
