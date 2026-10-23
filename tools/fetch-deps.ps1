<#
.SYNOPSIS
  Downloads the pinned rclone build and the WinFsp installer, verifies their SHA256 and places them under deps\.

.DESCRIPTION
  rclone  -> deps\rclone\rclone.exe   (copied to the app output as bin\rclone.exe by OpenMount.App.csproj)
  WinFsp  -> deps\winfsp\winfsp-<ver>.msi (bundled by the installer; also used by the first-run guide)

  Versions are pinned (SRS §4.4). Changing them requires running the integration test matrix.
#>
[CmdletBinding()]
param(
  [string]$RcloneVersion = "1.75.2",
  [string]$WinFspVersion = "2.1",
  [string]$WinFspFile = "winfsp-2.1.25156.msi",
  [ValidateSet("amd64", "arm64")] [string]$Arch = "amd64",
  [switch]$SkipWinFsp
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$depsRclone = Join-Path $root "deps\rclone"
$depsWinFsp = Join-Path $root "deps\winfsp"
New-Item -ItemType Directory -Force $depsRclone | Out-Null
New-Item -ItemType Directory -Force $depsWinFsp | Out-Null

function Get-FileSha256([string]$Path) { (Get-FileHash -Algorithm SHA256 $Path).Hash.ToLowerInvariant() }

# ---- rclone ----
$zipName = "rclone-v$RcloneVersion-windows-$Arch.zip"
$base = "https://github.com/rclone/rclone/releases/download/v$RcloneVersion"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("openmount-deps-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
Write-Host "Downloading $zipName ..."
Invoke-WebRequest -Uri "$base/$zipName" -OutFile (Join-Path $tmp $zipName)
Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $tmp "SHA256SUMS")
$expected = (Get-Content (Join-Path $tmp "SHA256SUMS") | Where-Object { $_ -match "\s$([regex]::Escape($zipName))$" } | ForEach-Object { ($_ -split "\s+")[0] }).ToLowerInvariant()
if (-not $expected) { throw "SHA256SUMS has no entry for $zipName" }
$actual = Get-FileSha256 (Join-Path $tmp $zipName)
if ($actual -ne $expected) { throw "SHA256 mismatch for $zipName`n expected $expected`n actual   $actual" }
Expand-Archive -Path (Join-Path $tmp $zipName) -DestinationPath $tmp -Force
$exe = Get-ChildItem -Path $tmp -Recurse -Filter rclone.exe | Select-Object -First 1
Copy-Item $exe.FullName (Join-Path $depsRclone "rclone.exe") -Force
Copy-Item (Join-Path $exe.DirectoryName "README.txt") (Join-Path $depsRclone "README-rclone.txt") -Force -ErrorAction SilentlyContinue
Get-FileSha256 (Join-Path $depsRclone "rclone.exe") | Set-Content (Join-Path $depsRclone "rclone.sha256") -Encoding ascii
Write-Host "rclone v$RcloneVersion ($Arch) -> $depsRclone"

# ---- WinFsp ----
if (-not $SkipWinFsp) {
  $msiUrl = "https://github.com/winfsp/winfsp/releases/download/v$WinFspVersion/$WinFspFile"
  $msiPath = Join-Path $depsWinFsp $WinFspFile
  Write-Host "Downloading $WinFspFile ..."
  Invoke-WebRequest -Uri $msiUrl -OutFile $msiPath
  $sig = Get-AuthenticodeSignature $msiPath
  if ($sig.Status -ne "Valid") { throw "WinFsp MSI Authenticode signature is not valid: $($sig.Status)" }
  Get-FileSha256 $msiPath | Set-Content (Join-Path $depsWinFsp "winfsp.sha256") -Encoding ascii
  Write-Host "WinFsp $WinFspVersion -> $msiPath (signer: $($sig.SignerCertificate.Subject))"
}

Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
Write-Host "Done."
