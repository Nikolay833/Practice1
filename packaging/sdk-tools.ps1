# Helpers shared by build-msix.ps1 and sign-msix.ps1 (dot-source this file).

# Finds a tool of the Windows 10/11 SDK (makeappx.exe, signtool.exe) in the newest SDK folder that has it:
# C:\Program Files (x86)\Windows Kits\10\bin\<version>\x64\<name>. The windows-latest runner ships the SDK.
function Find-SdkTool {
    param([Parameter(Mandatory)][string]$Name)

    $bin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $bin)) {
        throw "Windows SDK not found: $bin does not exist."
    }

    $found = Get-ChildItem -LiteralPath $bin -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1

    if (-not $found) {
        throw "$Name not found in any SDK folder under $bin."
    }

    Write-Host "Using $Name at $found"
    return $found
}

# Runs a native program and throws when it fails (try/catch and $ErrorActionPreference do not see exit codes).
# Values in -Secret are replaced by *** in the echoed command line.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [string[]]$Secret = @()
    )

    $shown = $ArgumentList -join ' '
    foreach ($value in $Secret) {
        if ($value) { $shown = $shown.Replace($value, '***') }
    }

    Write-Host ">> $FilePath $shown"
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}
