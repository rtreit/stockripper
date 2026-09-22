[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$secretFile = Join-Path $repositoryRoot 'secrets/stockripper-local.env'
if (-not (Test-Path -LiteralPath $secretFile)) {
    throw 'The local Stockripper secret file does not exist.'
}

Push-Location -LiteralPath $repositoryRoot
try {
    & docker compose `
        --project-name stockripperdev `
        --file eng/compose.dev.yml `
        --env-file $secretFile `
        down --remove-orphans
    if ($LASTEXITCODE -ne 0) {
        throw "docker exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
