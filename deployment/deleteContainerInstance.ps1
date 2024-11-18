# Ensure the environment variable is set
if (-not $env:AGENTIC_SUBSCRIPTION_ID) {
    Write-Output "AGENTIC_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

# Set the subscription
az account set --subscription $env:AGENTIC_SUBSCRIPTION_ID

# Define variables
$resourceGroupName = "stockripper"

# List all container groups
$containerGroups = az container list --resource-group $resourceGroupName --query "[].name" -o tsv

if ($null -eq $containerGroups) {
    Write-Output "No container groups found in resource group '$resourceGroupName'."
    exit 0
}

# Iterate through each container group and delete it
foreach ($containerGroupName in $containerGroups) {
    Write-Output "Deleting the container group '$containerGroupName'..."
    az container delete --resource-group $resourceGroupName --name $containerGroupName --yes
    Write-Output "Container group '$containerGroupName' has been deleted."
}

Write-Output "All container groups in resource group '$resourceGroupName' have been deleted."
