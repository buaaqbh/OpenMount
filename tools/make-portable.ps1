<#
.SYNOPSIS
  Builds the self-contained portable release: dist\OpenMount-<version>-portable-win-x64\ and the matching .zip.

.DESCRIPTION
  Publishes src\OpenMount.App (win-x64, self-contained, ReadyToRun), adds portable.txt, 使用说明.txt, LICENSE and
  THIRD-PARTY-NOTICES.md, then zips the folder. An existing data\ folder inside the output directory (a user's trial
  configuration) is left untouched and is not included in the zip.
#>
[CmdletBinding()]
param(
  [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Version not found in Directory.Build.props" }
$name = "OpenMount-$version-portable-$Runtime"
$out = Join-Path $root "dist\$name"
$stage = Join-Path $root "dist\.stage-$name"

if (-not (Test-Path (Join-Path $root "deps\rclone\rclone.exe"))) { throw "deps\rclone\rclone.exe missing: run tools\fetch-deps.ps1 first" }
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

Write-Host "publishing $name ..."
dotnet publish (Join-Path $root "src\OpenMount.App\OpenMount.App.csproj") -c Release -r $Runtime --self-contained true `
  -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $stage | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# portable.txt and the Chinese read-me; copied by wildcard so this script needs no non-ASCII literals (Windows PowerShell reads it as ANSI)
Get-ChildItem (Join-Path $PSScriptRoot "portable") -File | Copy-Item -Destination $stage
Copy-Item (Join-Path $root "LICENSE") $stage
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $stage
foreach ($required in @("OpenMount.exe", "bin\rclone.exe", "Assets\drive-webdav.ico", "Assets\drive-sftp.ico", "Assets\drive-cloud.ico", "Assets\drive-nas.ico", "Assets\drive-offline.ico")) {
  if (-not (Test-Path (Join-Path $stage $required))) { throw "publish output is missing $required" }
}

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
# ZipFile with UTF-8 entry names (Compress-Archive writes the OEM code page, which garbles the Chinese read-me elsewhere)
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false, [System.Text.Encoding]::UTF8)

# refresh the unpacked folder but keep a user's data\ from an earlier trial
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Force | Where-Object { $_.Name -ne "data" } | Remove-Item -Recurse -Force
Get-ChildItem $stage -Force | Copy-Item -Destination $out -Recurse -Force
Remove-Item -Recurse -Force $stage

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "done: $zip ($size MB); unpacked copy in $out"
