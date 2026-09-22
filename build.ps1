[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$DotNetArguments)

    & dotnet @DotNetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

Push-Location -LiteralPath $PSScriptRoot
try {
    Invoke-DotNet @(
        'restore',
        'Stockripper.slnx',
        '--disable-build-servers',
        '-m:1',
        '-p:NuGetAudit=false')
    Invoke-DotNet @(
        'format',
        'whitespace',
        '.',
        '--folder',
        '--verify-no-changes',
        '--include',
        'src',
        'tests')
    Invoke-DotNet @(
        'build',
        'Stockripper.slnx',
        '--configuration',
        'Release',
        '--no-restore',
        '--disable-build-servers',
        '-m:1')
}
finally {
    Pop-Location
}
