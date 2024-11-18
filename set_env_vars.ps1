$envFilePath = "config\.env"

function Set-EnvironmentVariable {
    param (
        [string]$name,
        [string]$value
    )

    [System.Environment]::SetEnvironmentVariable($name, $value, [System.EnvironmentVariableTarget]::User)
}

if (Test-Path $envFilePath) {
    Get-Content $envFilePath | ForEach-Object {
        if ($_ -match "^\s*([A-Za-z_][A-Za-z0-9_]+)\s*=\s*(.*)\s*$") {
            $name = $matches[1]
            $value = $matches[2]
            Set-EnvironmentVariable -name $name -value $value
            Write-Output "Set environment variable: $name"
        }
    }
    Write-Output "Environment variables have been set."
} else {
    Write-Error "The file $envFilePath does not exist."
}
