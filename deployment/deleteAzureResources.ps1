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

az group delete --name "stockripper" --yes

if ($LASTEXITCODE -eq 0) {
    Write-Host "Deleted resource group: stockripper"
}
elseif ($LASTEXITCODE -eq 3) {
    Write-Host "Resource group: stockripper not found"
}
else {
    Write-Host "Failed to delete resource group: stockripper"
    exit $LASTEXITCODE
}
