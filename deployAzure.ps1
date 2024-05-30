# Ensure the environment variable is set
if (-not $env:AZURE_SUBSCRIPTION_ID) {
    Write-Output "AZURE_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

# Set the subscription
az account set --subscription $env:AZURE_SUBSCRIPTION_ID

# Define variables
$resourceGroupName = "stockripper"
$searchServiceName = "stockrippersearch"
$acrName = "stockrippercr"
$containerName = "stockripper-container"
$imageName = "stockripper:latest"
$location = "westus2"
$envFilePath = ".env"

# Create the resource group if it doesn't exist
$rgExists = az group exists --name $resourceGroupName | ConvertFrom-Json
if (-not $rgExists) {
    Write-Output "Resource group '$resourceGroupName' does not exist. Creating..."
    az group create --name $resourceGroupName --location $location
} else {
    Write-Output "Resource group '$resourceGroupName' already exists."
}

# Deploy ACR
Write-Output "Deploying ACR..."
az deployment group create --resource-group $resourceGroupName --template-file ".\acrTemplate.json" --parameters acrName=$acrName location=$location

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

$envContent = Get-Content $envFilePath

# Remove any existing SEARCH_ADMIN_KEY lines
$cleanedEnvContent = $envContent | Where-Object { $_ -notmatch "^SEARCH_ADMIN_KEY=" }

# Write the cleaned and updated content back to the .env file
Set-Content -Path $envFilePath -Value $cleanedEnvContent

# Add the endpoint and key to the .env file
Write-Host "Adding the search service admin key to the .env file..."
Add-Content -Path $envFilePath -Value "SEARCH_ADMIN_KEY=$SEARCH_ADMIN_KEY"

# Clean .env file again to remove any potential duplicates or blank lines
$envVars = Get-Content $envFilePath | Where-Object { $_.Trim() -ne "" } | Sort-Object -Unique
Set-Content -Path $envFilePath -Value $envVars

Write-Host "Successfully updated .env file with SEARCH_ADMIN_KEY and cleaned up."


# Read and clean .env file
Write-Output "Reading and cleaning environment variables from $envFilePath..."
$envVars = Get-Content $envFilePath | Where-Object { $_.Trim() -ne "" } | Sort-Object -Unique | ForEach-Object {
    $key, $value = $_ -split '='
    [PSCustomObject]@{ name = $key.Trim(); value = $value.Trim() }
}

# Print cleaned environment variables for debugging
Write-Output "Cleaned environment variables:"
$envVars | Format-Table -AutoSize

# Deploy Container Instance
Write-Output "Deploying Container Instance..."
$parameters = @{
    containerName = @{ "value" = $containerName }
    acrName = @{ "value" = $acrName }
    imageName = @{ "value" = "$acrName.azurecr.io/$imageName" }
    location = @{ "value" = $location }
    environmentVariables = @{ "value" = $envVars }
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
