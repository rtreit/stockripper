# Ensure the environment variable is set
if (-not $env:AZURE_SUBSCRIPTION_ID) {
    Write-Output "AZURE_SUBSCRIPTION_ID environment variable is not set."
    exit 1
}

# Set the subscription
az account set --subscription $env:AZURE_SUBSCRIPTION_ID

# Check if the resource group exists
$rgExists = az group exists --name stockripper | ConvertFrom-Json

# Create the resource group if it doesn't exist
if (-not $rgExists) {
    Write-Output "Resource group 'stockripper' does not exist. Creating..."
    az group create --name stockripper --location westus2
} else {
    Write-Output "Resource group 'stockripper' already exists."
}

# Deploy the ARM template
az deployment group create --resource-group stockripper --template-file armtemplate.json
    