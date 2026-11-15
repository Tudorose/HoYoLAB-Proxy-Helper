#Requires -Version 5.1
<#
.SYNOPSIS
  One-click release: build plugin+helper, update manifest version, commit, tag, create GitHub Release, upload .hutao.
.PARAMETER Version
  Release version, e.g. 1.0.1
#>
param([Parameter(Mandatory=$true)][string]$Version)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be like 1.0.1" }

Write-Host "==> Updating manifest version to $Version"
$manifest = "HutaoProxyPlugin/manifest.json"
$json = Get-Content $manifest -Raw | ConvertFrom-Json
$json.version.major = [int]($Version.Split('.')[0])
$json.version.minor = [int]($Version.Split('.')[1])
$json.version.patch = [int]($Version.Split('.')[2])
($json | ConvertTo-Json -Depth 5) | Set-Content $manifest -Encoding UTF8

Write-Host "==> Building ProxyHelper"
dotnet build ProxyHelper/ProxyHelper.csproj -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ProxyHelper build failed" }
Copy-Item ProxyHelper/bin/Debug/ProxyHelper.* HutaoProxyPlugin/lib/ProxyHelper/ -Force -Exclude *.pdb

Write-Host "==> Building plugin package"
dotnet build HutaoProxyPlugin/HutaoProxyPlugin.csproj -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }
$pkg = "HutaoProxyPlugin/bin/Debug/HutaoProxyPlugin.hutao"
if (-not (Test-Path $pkg)) { throw "package not produced" }

# privacy scan: fail on known personal artifacts
$bytes = [IO.File]::ReadAllBytes($pkg)
$text = [Text.Encoding]::Unicode.GetString($bytes) + [Text.Encoding]::ASCII.GetString($bytes)
foreach ($secret in '94d4320b','redacted-uid','redacted-uid','example.com') {
  if ($text.Contains($secret)) { throw "PRIVACY SCAN FAILED: found '$secret' in package" }
}
Write-Host "==> Privacy scan passed"

Write-Host "==> Committing & tagging v$Version"
git add -A
git commit -m "Release v$Version" 2>$null
git tag -a "v$Version" -m "HoYoLAB Proxy Helper v$Version" 2>$null
git push origin main
git push origin "v$Version"

Write-Host "==> Creating GitHub Release v$Version"
$tokenOut = "protocol=https`nhost=github.com`n`n" | & git credential fill
$token = ($tokenOut | Where-Object { $_ -like 'password=*' }) -replace '^password='
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json' }
$notes = @"
## HoYoLAB Proxy Helper v$Version

See [README](https://github.com/Tudorose/HoYoLAB-Proxy-Helper#readme) for install & config.

### Install
Download the .hutao below into %LOCALAPPDATA%\SnapHutaoRemastered\Plugins\ (or run: npx github:Tudorose/HoYoLAB-Proxy-Helper)
"@
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/Tudorose/HoYoLAB-Proxy-Helper/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ tag_name="v$Version"; name="HoYoLAB Proxy Helper v$Version"; body=$notes; draft=$false; prerelease=$false } | ConvertTo-Json) -TimeoutSec 60
Invoke-RestMethod -Uri "https://uploads.github.com/repos/Tudorose/HoYoLAB-Proxy-Helper/releases/$($rel.id)/assets?name=HoYoLAB-Proxy-Helper-v$Version.hutao" -Method Post -Headers @{ Authorization="Bearer $token"; 'Content-Type'='application/octet-stream' } -InFile $pkg -TimeoutSec 300 | Out-Null
Write-Host "==> DONE: https://github.com/Tudorose/HoYoLAB-Proxy-Helper/releases/tag/v$Version"
