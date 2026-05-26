# Post-tool hook scaffold for StockRipper
# Warns when Python tasks are run without uv and keeps the repo aligned with the spec.

param(
    [string]$Command = $env:COPILOT_TOOL_INPUT
)

if (-not $Command) {
    exit 0
}

if ($Command -match '(?i)\bpython\b' -and $Command -notmatch '(?i)\buv run\b') {
    Write-Warning "Prefer uv run for StockRipper Python tasks so the environment stays reproducible."
}

if ($Command -match '(?i)\b(ruff|pytest|mypy|pyright)\b') {
    Write-Host "StockRipper quality command observed: $Command"
}

exit 0
