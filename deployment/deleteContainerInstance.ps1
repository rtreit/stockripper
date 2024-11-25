# Ensure the environment variable is set
if (-not $env:AZURE_SUBSCRIPTION_ID) {
    Write-Output "AZURE_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

# Set the subscription
az account set --subscription $env:AZURE_SUBSCRIPTION_ID

# Define variables
$resourceGroupName = "stockripper"

# List all container groups
$containerGroups = az container list --resource-group $resourceGroupName --query "[].name" -o tsv

if ($null -eq $containerGroups) {
    Write-Output "No container groups found in resource group '$resourceGroupName'."
} else {
    # Iterate through each container group and delete it
    foreach ($containerGroupName in $containerGroups) {
        Write-Output "Deleting the container group '$containerGroupName'..."
        az container delete --resource-group $resourceGroupName --name $containerGroupName --yes
        Write-Output "Container group '$containerGroupName' has been deleted."
    }

    Write-Output "All container groups in resource group '$resourceGroupName' have been deleted."
}

# List all application gateways
$appGateways = az network application-gateway list --resource-group $resourceGroupName --query "[].name" -o tsv

if ($null -eq $appGateways) {
    Write-Output "No application gateways found in resource group '$resourceGroupName'."
} else {
    # Iterate through each application gateway and delete it
    foreach ($appGatewayName in $appGateways) {
        Write-Output "Deleting the application gateway '$appGatewayName'..."
        az network application-gateway delete --resource-group $resourceGroupName --name $appGatewayName
        Write-Output "Application gateway '$appGatewayName' has been deleted."
    }

    Write-Output "All application gateways in resource group '$resourceGroupName' have been deleted."
}

Write-Output "Cleanup of container groups and application gateways in resource group '$resourceGroupName' is complete."
