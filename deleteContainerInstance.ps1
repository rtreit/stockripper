# Ensure the environment variable is set
if (-not $env:AZURE_SUBSCRIPTION_ID) {
    Write-Output "AZURE_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

# Set the subscription
az account set --subscription $env:AZURE_SUBSCRIPTION_ID

# Define variables
$resourceGroupName = "stockripper"
$containerGroupName = "stockripper-container"

# Confirm deletion
$confirmation = Read-Host "Are you sure you want to delete the container group '$containerGroupName' in resource group '$resourceGroupName'? (yes/no)"
if ($confirmation -ne "yes") {
    Write-Output "Deletion cancelled."
    exit
}

# Delete the container instance
Write-Output "Deleting the container group '$containerGroupName'..."
az container delete --resource-group $resourceGroupName --name $containerGroupName --yes

# Confirm deletion
Write-Output "Container group '$containerGroupName' has been deleted."
