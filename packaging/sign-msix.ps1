<#
.SYNOPSIS
  Signs Dropshelf.msixbundle and writes Dropshelf.cer, the public certificate users have to trust once.

.DESCRIPTION
  If the environment has MSIX_PFX_BASE64 (a base64 .pfx) and MSIX_PFX_PASSWORD, that certificate is used: the same certificate on
  every release means users trust it once. Otherwise a throw-away self-signed certificate is created, which works but makes every
  release need a new trust step. The certificate subject must be exactly CN=Dropshelf, the Publisher in Package.appxmanifest.

  To create the stable certificate once, see "MSIX signing certificate" in README.md.
#>
param(
    [Parameter(Mandatory)][string]$Bundle,
    [string]$OutDir = 'msix-out'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'sdk-tools.ps1')

$subject = 'CN=Dropshelf'
$bundlePath = (Resolve-Path -LiteralPath $Bundle).Path
$outFull = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path (Get-Location).Path $OutDir }
$cerPath = Join-Path $outFull 'Dropshelf.cer'

$tempDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$pfxPath = Join-Path $tempDir 'dropshelf-signing.pfx' # never next to the outputs: only the bundle and the .cer are published
$generated = $null

try {
    $pfxBase64 = $env:MSIX_PFX_BASE64
    $password = $env:MSIX_PFX_PASSWORD

    if ($pfxBase64) {
        if (-not $password) { throw 'MSIX_PFX_BASE64 is set but MSIX_PFX_PASSWORD is not.' }
        Write-Host 'Signing with the certificate from the MSIX_PFX_BASE64 secret.'
        [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($pfxBase64.Trim()))
    }
    else {
        Write-Warning 'MSIX_PFX_BASE64 is not set: signing with a new self-signed certificate. Users must trust Dropshelf.cer again for every release.'
        $password = [Guid]::NewGuid().ToString('N')
        Write-Host "::add-mask::$password"

        $generated = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
            -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        $secure = ConvertTo-SecureString -String $password -AsPlainText -Force
        Export-PfxCertificate -Cert $generated -FilePath $pfxPath -Password $secure | Out-Null
    }

    # The public half, for users (Dropshelf.cer) and for the subject check.
    $loaded = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $password)
    Write-Host "Certificate: $($loaded.Subject), thumbprint $($loaded.Thumbprint), valid until $($loaded.NotAfter.ToString('yyyy-MM-dd'))"
    if ($loaded.Subject -ne $subject) {
        throw "The certificate subject is '$($loaded.Subject)' but the package publisher is '$subject'. They must match exactly."
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $cerPath) | Out-Null
    [IO.File]::WriteAllBytes($cerPath, $loaded.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Host "Wrote $cerPath"

    $signtool = Find-SdkTool 'signtool.exe'
    $signArgs = @('sign', '/fd', 'SHA256', '/f', $pfxPath, '/p', $password)

    # The timestamp keeps the signature valid after the certificate expires, but the server is a third party: if it is down,
    # sign without it rather than fail the release.
    Write-Host ">> signtool sign (with timestamp)"
    & $signtool @signArgs '/tr' 'http://timestamp.digicert.com' '/td' 'SHA256' $bundlePath
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Timestamping failed; signing without a timestamp.'
        Invoke-Native $signtool ($signArgs + $bundlePath) -Secret @($password)
    }

    # signtool verify cannot succeed here (the certificate is self-signed, so not trusted on the build machine); check that a signature is in the bundle.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($bundlePath)
    try {
        if (-not ($zip.Entries | Where-Object { $_.FullName -eq 'AppxSignature.p7x' })) {
            throw 'The bundle has no AppxSignature.p7x after signing.'
        }
    }
    finally {
        $zip.Dispose()
    }

    Write-Host "Signed $bundlePath"
}
finally {
    if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force }
    if ($generated) { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($generated.Thumbprint)" -Force -ErrorAction SilentlyContinue }
}
