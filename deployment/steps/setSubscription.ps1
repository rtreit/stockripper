if (-not $env:AZURE_SUBSCRIPTION_ID) {
    Write-Output "AZURE_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

Write-Output "Setting subscription to $env:AZURE_SUBSCRIPTION_ID"
az account set --subscription $env:AZURE_SUBSCRIPTION_ID
