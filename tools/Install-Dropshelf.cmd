@echo off
rem Double-click to install the Dropshelf build that matches this PC (x64 or Arm64).
rem Same logic as install.ps1, inlined so this file works on its own.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue';" ^
  "$n=(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE;" ^
  "if($n -eq 'ARM64'){$a='arm64'}elseif($n -eq 'AMD64'){$a='x64'}else{throw \"Dropshelf needs a 64-bit PC (x64 or Arm64). This PC reports '$n'.\"};" ^
  "$d=Join-Path $env:TEMP 'Dropshelf-Setup'; New-Item -ItemType Directory -Force -Path $d | Out-Null; $e=Join-Path $d \"Dropshelf-$a.exe\";" ^
  "Write-Host \"Downloading Dropshelf for $a...\";" ^
  "Invoke-WebRequest -Uri \"https://github.com/Nikolay833/Practice1/releases/latest/download/Dropshelf-$a.exe\" -OutFile $e -UseBasicParsing;" ^
  "Unblock-File -Path $e; Start-Process -FilePath $e; Write-Host 'Done. Look for the island at the top of your screen.'"
pause
