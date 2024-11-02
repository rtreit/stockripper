$resourceGroupName = "stockripper"
$acrName = "stockrippercr"
$location = if ($env:AZURE_LOCATION) { $env:AZURE_LOCATION } else { az group list --query "[0].location" --output tsv }

# Deploy ACR
Write-Output "Deploying ACR..."
az deployment group create --resource-group $resourceGroupName --template-file "..\config\acrTemplate.json" --parameters acrName=$acrName location=$location

