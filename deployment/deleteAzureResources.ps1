$resourceGroupName = "stockripper"
$resourceIds = az resource list --resource-group $resourceGroupName --query "[].id" --output tsv

if ($resourceIds) {
    foreach ($resourceId in $resourceIds) {
        Write-Host "Deleting resource with ID: $resourceId"
        az resource delete --ids $resourceId
        Write-Host "Deleted resource with ID: $resourceId"
    }
}
else {
    Write-Host "No resources found in the resource group: $resourceGroupName"
}
