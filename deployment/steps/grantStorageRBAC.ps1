if (-not $env:AZURE_SUBSCRIPTION_ID) {
    Write-Output "AZURE_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

if (-not $env:MY_AZURE_OBJECT_ID) {
    Write-Output "MY_AZURE_OBJECT_ID environment variable is not set."
    exit 1
}

Write-Output "Setting subscription to $env:AZURE_SUBSCRIPTION_ID"
az account set --subscription $env:AZURE_SUBSCRIPTION_ID
$subScriptionId = $env:AZURE_SUBSCRIPTION_ID
$resourceGroupName = "stockripper"
$storageAccountName = "stockripperstg"

az role assignment create `
  --role "Storage Blob Data Contributor" `
  --scope "/subscriptions/$subScriptionId/resourceGroups/$resourceGroupName/providers/Microsoft.Storage/storageAccounts/$storageAccountName" `
  --assignee-object-id $env:MY_AZURE_OBJECT_ID

