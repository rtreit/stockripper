[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$PostgresPort = 54329
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$CommandArguments
    )

    & $Command @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command exited with code $LASTEXITCODE."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$secretDirectory = Join-Path $repositoryRoot 'secrets'
$secretFile = Join-Path $secretDirectory 'stockripper-local.env'
if (-not (Test-Path -LiteralPath $secretFile)) {
    $null = New-Item -ItemType Directory -Path $secretDirectory -Force
    [System.IO.File]::WriteAllLines($secretFile, @(
        "STOCKRIPPER_DEV_POSTGRES_PASSWORD=$([guid]::NewGuid().ToString('N'))",
        "STOCKRIPPER_DEV_STRATEGY_TOKEN=$([guid]::NewGuid().ToString('N'))"))
}

$secretValues = @{}
foreach ($line in Get-Content -LiteralPath $secretFile) {
    $parts = $line.Split('=', 2)
    if ($parts.Count -eq 2) {
        $secretValues[$parts[0]] = $parts[1]
    }
}

$databasePassword = $secretValues['STOCKRIPPER_DEV_POSTGRES_PASSWORD']
$strategyToken = $secretValues['STOCKRIPPER_DEV_STRATEGY_TOKEN']
if (
    $databasePassword -notmatch '^[0-9a-f]{32}$' -or
    $strategyToken -notmatch '^[0-9a-f]{32}$'
) {
    throw 'The local Stockripper secret file is invalid.'
}

$connectionString = "Host=127.0.0.1;Port=$PostgresPort;Database=stockripper;Username=stockripper;Password=$databasePassword;Include Error Detail=false"

Push-Location -LiteralPath $repositoryRoot
try {
    Invoke-CheckedCommand 'docker' @('version', '--format', '{{.Server.Version}}')
    $env:STOCKRIPPER_DEV_POSTGRES_PORT = $PostgresPort.ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)
    Invoke-CheckedCommand 'docker' @(
        'compose',
        '--project-name', 'stockripperdev',
        '--file', 'eng/compose.dev.yml',
        '--env-file', $secretFile,
        'up',
        '--detach',
        '--wait')
    Invoke-CheckedCommand 'dotnet' @(
        'user-secrets', 'set', 'ConnectionStrings:Stockripper',
        $connectionString,
        '--project', 'src/Stockripper.Host')
    Invoke-CheckedCommand 'dotnet' @(
        'user-secrets', 'set', 'Strategy:AccessToken',
        $strategyToken,
        '--project', 'src/Stockripper.Host')
    Invoke-CheckedCommand 'dotnet' @(
        'user-secrets', 'set', 'Strategy:AccessToken',
        $strategyToken,
        '--project', 'src/Stockripper.Strategy.Worker')
    Write-Host 'Local PostgreSQL is ready. Stockripper user secrets are configured.'
}
finally {
    Remove-Item Env:STOCKRIPPER_DEV_POSTGRES_PORT -ErrorAction SilentlyContinue
    Remove-Variable databasePassword -ErrorAction SilentlyContinue
    Remove-Variable strategyToken -ErrorAction SilentlyContinue
    Remove-Variable connectionString -ErrorAction SilentlyContinue
    Pop-Location
}
