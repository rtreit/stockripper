$resourceGroupName = "stockripper"
$storageAccountName = "stockripperstg"
$location = if ($env:AZURE_LOCATION) { $env:AZURE_LOCATION } else { az group list --query "[0].location" --output tsv }
$sku = "Standard_LRS"

az storage account create --name $storageAccountName --resource-group $resourceGroupName --location $location --sku $sku --kind StorageV2 --access-tier Hot

