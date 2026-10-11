# Downloads the Dropshelf build that matches this PC (x64 or arm64) from the latest
# GitHub release and runs it. Dropshelf then installs itself and starts at sign-in.
#
#   irm https://raw.githubusercontent.com/Nikolay833/Practice1/main/tools/install.ps1 | iex

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# The registry holds the native architecture even when this PowerShell runs emulated
# (x64 PowerShell on an Arm PC reports AMD64 in its own environment).
$native = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE

switch ($native) {
    'ARM64' { $arch = 'arm64' }
    'AMD64' { $arch = 'x64' }
    default { throw "Dropshelf needs a 64-bit PC (x64 or Arm64). This PC reports '$native'." }
}

$url = "https://github.com/Nikolay833/Practice1/releases/latest/download/Dropshelf-$arch.exe"
$dir = Join-Path $env:TEMP 'Dropshelf-Setup'
$exe = Join-Path $dir "Dropshelf-$arch.exe"

New-Item -ItemType Directory -Force -Path $dir | Out-Null
Write-Host "Downloading Dropshelf for $arch..."
Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing
Unblock-File -Path $exe

Write-Host 'Installing...'
Start-Process -FilePath $exe
Write-Host 'Done. Look for the pill at the top of your screen.'
