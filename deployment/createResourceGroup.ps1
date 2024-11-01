$resourceGroupName = "stockripper"

# Create the resource group if it doesn't exist
$rgExists = az group exists --name $resourceGroupName | ConvertFrom-Json
if (-not $rgExists) {
    Write-Output "Resource group '$resourceGroupName' does not exist. Creating..."
    az group create --name $resourceGroupName --location $location
} else {
    Write-Output "Resource group '$resourceGroupName' already exists."
}