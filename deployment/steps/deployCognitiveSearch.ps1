$resourceGroupName = "stockripper"
$searchServiceName = "stockrippersearch"
$location = "westus2"
$envFilePath = "..\config\.env"

Write-Output "Deploying Cognitive Search service..."
az deployment group create --resource-group $resourceGroupName --template-file "..\config\searchTemplate.json" --parameters searchServiceName=$searchServiceName location=$location

$SEARCH_ADMIN_KEY = az search admin-key show --service-name $searchServiceName --resource-group $resourceGroupName --query "primaryKey" -o tsv

Write-Output "Reading and cleaning environment variables from $envFilePath..."
$envContent = Get-Content $envFilePath

$cleanedEnvContent = $envContent | Where-Object { $_ -notmatch "^SEARCH_ADMIN_KEY=" }

Set-Content -Path $envFilePath -Value $cleanedEnvContent

Write-Host "Adding the search service admin key to the .env file..."
Add-Content -Path $envFilePath -Value "SEARCH_ADMIN_KEY=$SEARCH_ADMIN_KEY"
