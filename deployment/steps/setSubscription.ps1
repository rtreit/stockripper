if (-not $env:AZURE_SUBSCRIPTION_ID) {
    Write-Output "ATEVET_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

Write-Output "Setting subscription to $env:ATEVET_SUBSCRIPTION_ID"
az account set --subscription $env:ATEVET_SUBSCRIPTION_ID
