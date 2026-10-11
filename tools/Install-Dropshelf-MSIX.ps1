# Installs the MSIX version of Dropshelf from the latest GitHub release (one package for x64 and Arm64 PCs).
# The MSIX version adds notifications on the pill and the Windows calendar.
#
#   irm https://github.com/Nikolay833/Practice1/releases/latest/download/Install-Dropshelf-MSIX.ps1 | iex
#
# The package is signed with a self-signed certificate, so Windows has to trust that certificate once
# (Local Machine > Trusted People). That step needs administrator rights and asks for them with a Windows prompt;
# the package itself is then installed for your own account, not the administrator's.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([Environment]::OSVersion.Version.Build -lt 19041) {
    throw 'The Dropshelf MSIX version needs Windows 10 version 2004 (build 19041) or later.'
}

$base = 'https://github.com/Nikolay833/Practice1/releases/latest/download'
$dir = Join-Path $env:TEMP 'Dropshelf-Setup'
$cer = Join-Path $dir 'Dropshelf.cer'
$bundle = Join-Path $dir 'Dropshelf.msixbundle'

New-Item -ItemType Directory -Force -Path $dir | Out-Null
Write-Host 'Downloading Dropshelf (MSIX)...'
Invoke-WebRequest -Uri "$base/Dropshelf.cer" -OutFile $cer -UseBasicParsing
Invoke-WebRequest -Uri "$base/Dropshelf.msixbundle" -OutFile $bundle -UseBasicParsing
Unblock-File -Path $cer, $bundle

# 1. Trust the signing certificate (administrator, once).
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)

function Test-Trusted {
    $null -ne (Get-ChildItem -Path Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint })
}

if (-not (Test-Trusted)) {
    $import = "`$ErrorActionPreference = 'Stop'; Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($isAdmin) {
        Invoke-Expression $import
    }
    else {
        Write-Host 'Windows will ask for permission to trust the Dropshelf certificate (once)...'
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($import))
        try {
            $elevated = Start-Process -FilePath powershell.exe -Verb RunAs -Wait -PassThru `
                -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
        }
        catch {
            throw "The certificate was not trusted: $($_.Exception.Message)"
        }

        if ($elevated.ExitCode -ne 0) {
            throw "Trusting the certificate failed (exit code $($elevated.ExitCode))."
        }
    }

    if (-not (Test-Trusted)) {
        throw 'The Dropshelf certificate is still not trusted, so Windows would refuse the package.'
    }
}

# 2. Install the package for the current user (the Appx module is Windows PowerShell only).
if ($PSVersionTable.PSEdition -eq 'Core') {
    Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue
}

Write-Host 'Installing...'
Add-AppxPackage -Path $bundle -ForceApplicationShutdown
Write-Host 'Done. Look for the pill at the top of your screen. In Settings, turn on notifications if you want them on the pill.'
