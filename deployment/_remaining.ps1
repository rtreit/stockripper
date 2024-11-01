$resourceGroupName = "stockripper"
$searchServiceName = "stockrippersearch"
$acrName = "stockrippercr"
$containerName = "stockripper-container"
$imageName = "stockripper:latest"
$location = "westus2"
$envFilePath = ".env"

# Fetch the ACR credentials
$credentials = az acr credential show --name $acrName | ConvertFrom-Json
$acrUsername = $credentials.username
$acrPassword = $credentials.passwords[0].value

# Build and push the container image
Write-Output "Building and pushing the container image..."
az acr login --name $acrName
docker build -t $imageName .
docker tag $imageName "$acrName.azurecr.io/$imageName"
docker push "$acrName.azurecr.io/$imageName"

# Deploy Cognitive Search Service
Write-Output "Deploying Cognitive Search service..."
az deployment group create --resource-group $resourceGroupName --template-file ".\searchTemplate.json" --parameters searchServiceName=$searchServiceName location=$location

# Get the admin key
$SEARCH_ADMIN_KEY = az search admin-key show --service-name $searchServiceName --resource-group $resourceGroupName --query "primaryKey" -o tsv

# Read and clean .env file content
Write-Output "Reading and cleaning environment variables from $envFilePath..."
$envContent = Get-Content $envFilePath

# Remove any existing SEARCH_ADMIN_KEY lines
$cleanedEnvContent = $envContent | Where-Object { $_ -notmatch "^SEARCH_ADMIN_KEY=" }

# Write the cleaned and updated content back to the .env file
Set-Content -Path $envFilePath -Value $cleanedEnvContent

# Add the SEARCH_ADMIN_KEY to the .env file
Write-Host "Adding the search service admin key to the .env file..."
Add-Content -Path $envFilePath -Value "SEARCH_ADMIN_KEY=$SEARCH_ADMIN_KEY"

# Clean .env file again to remove any duplicates or blank lines
$envVars = Get-Content $envFilePath | Where-Object { $_.Trim() -ne "" } | Sort-Object -Unique

# Convert cleaned environment variables into an array of objects
$envVarsArray = $envVars | ForEach-Object {
    $key, $value = $_ -split '='
    [PSCustomObject]@{ name = $key.Trim(); value = $value.Trim() }
}

# Convert environment variables to JSON array format
$envVarsJsonArray = $envVarsArray | ConvertTo-Json -Compress -Depth 10

# Prepare parameters for Container Instance deployment
Write-Output "Deploying Container Instance..."
$parameters = @{
    containerName = @{ "value" = $containerName }
    acrName = @{ "value" = $acrName }
    imageName = @{ "value" = "$acrName.azurecr.io/$imageName" }
    location = @{ "value" = $location }
    environmentVariables = @{ "value" = $envVarsArray }
    acrUsername = @{ "value" = $acrUsername }
    acrPassword = @{ "value" = $acrPassword }
}

$parametersJson = $parameters | ConvertTo-Json -Depth 10 -Compress
$parametersFilePath = ".\containerParameters.json"
$parametersJson | Out-File -FilePath $parametersFilePath -Encoding ascii

az deployment group create --resource-group $resourceGroupName --template-file ".\containerTemplate.json" --parameters @$parametersFilePath


# clean up
Remove-Item $parametersFilePath

Write-Output "Deployment completed successfully."
