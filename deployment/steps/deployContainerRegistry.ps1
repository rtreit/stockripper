$resourceGroupName = "stockripper"
$acrName = "stockrippercr"
$location = "westus2"

# Deploy ACR
Write-Output "Deploying ACR..."
az deployment group create --resource-group $resourceGroupName --template-file "..\config\acrTemplate.json" --parameters acrName=$acrName location=$location

