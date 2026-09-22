[CmdletBinding()]
param()

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

Push-Location -LiteralPath $PSScriptRoot
$listener = $null
$composeProject = "stockrippertest$([guid]::NewGuid().ToString('N'))"
try {
    & docker version --format '{{.Server.Version}}' *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Desktop engine access is required for the PostgreSQL integration tests.'
    }

    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    $listener.Start()
    $testPort = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $listener = $null

    $env:STOCKRIPPER_TEST_POSTGRES_HOST = '127.0.0.1'
    $env:STOCKRIPPER_TEST_POSTGRES_PORT = $testPort.ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)
    $env:STOCKRIPPER_TEST_POSTGRES_DATABASE = 'postgres'
    $env:STOCKRIPPER_TEST_POSTGRES_USER = 'stockripper_test'
    $env:STOCKRIPPER_TEST_POSTGRES_PASSWORD = [guid]::NewGuid().ToString('N')

    Invoke-CheckedCommand 'docker' @(
        'compose',
        '--project-name', $composeProject,
        '--file', 'eng/compose.test.yml',
        'up',
        '--detach',
        '--wait',
        '--quiet-pull')

    $containerId = (& docker compose `
        --project-name $composeProject `
        --file eng/compose.test.yml `
        ps --quiet postgres).Trim()
    if ($LASTEXITCODE -ne 0 -or $containerId -notmatch '^[0-9a-f]{12,64}$') {
        throw 'The PostgreSQL test container identity is invalid.'
    }

    $env:STOCKRIPPER_TEST_POSTGRES_CONTAINER = $containerId
    Invoke-CheckedCommand 'dotnet' @(
        'test',
        'Stockripper.slnx',
        '--configuration',
        'Release',
        '--disable-build-servers',
        '-m:1',
        '-p:NuGetAudit=false')
}
finally {
    if ($listener) {
        $listener.Stop()
    }

    if ($composeProject -match '^stockrippertest[0-9a-f]{32}$') {
        & docker compose `
            --project-name $composeProject `
            --file eng/compose.test.yml `
            down --volumes --remove-orphans *> $null
    }

    Remove-Item Env:STOCKRIPPER_TEST_POSTGRES_HOST -ErrorAction SilentlyContinue
    Remove-Item Env:STOCKRIPPER_TEST_POSTGRES_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:STOCKRIPPER_TEST_POSTGRES_DATABASE -ErrorAction SilentlyContinue
    Remove-Item Env:STOCKRIPPER_TEST_POSTGRES_USER -ErrorAction SilentlyContinue
    Remove-Item Env:STOCKRIPPER_TEST_POSTGRES_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:STOCKRIPPER_TEST_POSTGRES_CONTAINER -ErrorAction SilentlyContinue
    Pop-Location
}
