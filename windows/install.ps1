# Installs dist\ImagesToVideo for the current user: %LOCALAPPDATA%\Programs\ImagesToVideo,
# plus Start Menu and Desktop shortcuts. Pass -Uninstall to remove it again.
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$target = Join-Path $env:LOCALAPPDATA "Programs\ImagesToVideo"
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ImagesToVideo.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath("Desktop")) "ImagesToVideo.lnk"

Get-Process ImagesToVideo -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

if ($Uninstall) {
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $startMenu, $desktop -Force -ErrorAction SilentlyContinue
    Write-Host "ImagesToVideo uninstalled."
    exit 0
}

$source = Join-Path $PSScriptRoot "dist\ImagesToVideo"
if (-not (Test-Path (Join-Path $source "ImagesToVideo.exe"))) { throw "Build first: dist\ImagesToVideo\ImagesToVideo.exe not found." }

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Copy-Item $source $target -Recurse
$exe = Join-Path $target "ImagesToVideo.exe"

$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in @($startMenu, $desktop)) {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $target
    $s.IconLocation = "$exe,0"
    $s.Description = "Turn photos into a video with pan & zoom and transitions"
    $s.Save()
}
Write-Host "Installed to $target"
Write-Host "Shortcuts: $startMenu"
Write-Host "           $desktop"
