
if (-not $env:AGENTIC_SUBSCRIPTION_ID) {
    Write-Output "AGENTIC_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

if (-not $env:MY_AZURE_OBJECT_ID) {
    Write-Output "MY_AZURE_OBJECT_ID environment variable is not set."
    exit 1
}

Write-Output "Setting subscription to $env:AGENTIC_SUBSCRIPTION_ID"
az account set --subscription $env:AGENTIC_SUBSCRIPTION_ID
$subscriptionId = $env:AGENTIC_SUBSCRIPTION_ID
$resourceGroupName = "stockripper"
$storageAccountName = "stockripperstg"
$uamiDetailsFile = ".\uami_details.json"

Write-Output "Assigning Storage Blob Data Contributor role to the user..."
az role assignment create `
  --role "Storage Blob Data Contributor" `
  --scope "/subscriptions/$subscriptionId/resourceGroups/$resourceGroupName/providers/Microsoft.Storage/storageAccounts/$storageAccountName" `
  --assignee-object-id $env:MY_AZURE_OBJECT_ID

if (Test-Path -Path $uamiDetailsFile) {
    Write-Output "Reading UAMI details from $uamiDetailsFile..."
    $uamiDetails = Get-Content -Path $uamiDetailsFile | ConvertFrom-Json
    $uamiPrincipalId = $uamiDetails.principalId

    if ($uamiPrincipalId) {
        Write-Output "Assigning Storage Blob Data Contributor role to the UAMI..."
        az role assignment create `
          --role "Storage Blob Data Contributor" `
          --scope "/subscriptions/$subscriptionId/resourceGroups/$resourceGroupName/providers/Microsoft.Storage/storageAccounts/$storageAccountName" `
          --assignee-object-id $uamiPrincipalId
    } else {
        Write-Output "Error: Could not find 'principalId' in $uamiDetailsFile."
        exit 1
    }
} else {
    Write-Output "Error: UAMI details file $uamiDetailsFile does not exist."
    exit 1
}

Write-Output "Role assignments completed."
