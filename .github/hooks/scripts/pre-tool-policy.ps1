# Pre-tool policy hook
# Blocks obvious secret leaks and keeps StockRipper’s Python-first workflow safe.

param(
    [string]$File = $env:COPILOT_FILE
)

function Test-ForSecrets {
    param([string]$FilePath)

    if (-not (Test-Path $FilePath)) {
        return
    }

    $secretPattern = '(?i)(api[_-]?key|secret|password|token|connection[_-]?string)\s*[:=]\s*["''][^"'']+["'']'
    if (Select-String -Path $FilePath -Pattern $secretPattern -Quiet) {
        Write-Error "POLICY VIOLATION: Potential hardcoded secret detected in $FilePath"
        Write-Error "Use environment variables or a secure secret store instead."
        exit 1
    }
}

function Test-ForUnsafeOrderExecution {
    param([string]$FilePath)

    if (-not (Test-Path $FilePath)) {
        return
    }

    if ($FilePath -match '(?i)src[\\/]stockripper[\\/].*\.py$') {
        $unsafePatterns = @(
            '(?i)\beval\s*\(',
            '(?i)\bexec\s*\(',
            '(?i)subprocess\.',
            '(?i)os\.system\s*\('
        )

        foreach ($pattern in $unsafePatterns) {
            if (Select-String -Path $FilePath -Pattern $pattern -Quiet) {
                Write-Warning "Policy: keep dynamic execution out of core StockRipper workflow code."
                break
            }
        }
    }
}

function Test-ForLiveAlpacaEndpoint {
    param([string]$FilePath)

    if (-not (Test-Path $FilePath)) {
        return
    }

    # The StockRipper MVP universal floor forbids any code in the *main app* that targets
    # a non-paper Alpaca endpoint. The dedicated tools/alpaca_mcp/ package legitimately
    # supports both paper and live modes under its own double-confirmation gate and is
    # excluded here. Tests under tests/ that exercise the rejection path are also excluded.
    if ($FilePath -match '(?i)tools[\\/]alpaca_mcp[\\/]') {
        return
    }
    if ($FilePath -match '(?i)tests[\\/].*test_config\.py$') {
        return
    }

    # Paper endpoint is https://paper-api.alpaca.markets. The live endpoint must never appear.
    $livePattern = '(?i)https?://(api\.alpaca\.markets|data\.alpaca\.markets/v[0-9]+/(?!.*paper))'
    $explicitLive = '(?i)["''](api\.alpaca\.markets)["'']'
    if ((Select-String -Path $FilePath -Pattern $livePattern -Quiet) -or `
        (Select-String -Path $FilePath -Pattern $explicitLive -Quiet)) {
        Write-Error "POLICY VIOLATION: Reference to a non-paper Alpaca endpoint detected in $FilePath"
        Write-Error "StockRipper MVP is paper-only. Use https://paper-api.alpaca.markets."
        Write-Error "If you need live access for the dedicated MCP server, work under tools/alpaca_mcp/."
        exit 1
    }
}

if ($File) {
    Test-ForSecrets -FilePath $File
    Test-ForUnsafeOrderExecution -FilePath $File
    Test-ForLiveAlpacaEndpoint -FilePath $File
}
