# Define variables
$acrName = "stockrippercr"
$resourceGroupName = "stockripper"

# Get location from environment variable or resource group
$location = if ($env:AZURE_LOCATION) { $env:AZURE_LOCATION } else {
    $locationFromRg = az group show --name $resourceGroupName --query "location" --output tsv
    if (-not $locationFromRg) {
        Write-Error "Could not determine the location. Please set AZURE_LOCATION environment variable or ensure the resource group exists."
        exit 1
    }
    $locationFromRg
}

# Define image names
$agentAppImageName = "$acrName.azurecr.io/stockripper-agent-app:latest"
$fsharpAppImageName = "$acrName.azurecr.io/stockripper-fsharp-app:latest"
$rustAppImageName = "$acrName.azurecr.io/stockripper-rust-app:latest"
$chatAppImageName = "$acrName.azurecr.io/stockripper-chat-app:latest"

# Load UAMI details
$uamiDetailsFile = ".\uami_details.json"
if (Test-Path -Path $uamiDetailsFile) {
    $uamiDetails = Get-Content -Path $uamiDetailsFile | ConvertFrom-Json
    $uamiResourceId = $uamiDetails.id
    if (-not $uamiResourceId) {
        Write-Error "Could not retrieve the UAMI resource ID from $uamiDetailsFile"
        exit 1
    }
} else {
    Write-Error "UAMI details file $uamiDetailsFile does not exist."
    exit 1
}

# Retrieve ACR credentials
$acrUsername = az acr credential show --name $acrName --query "username" -o tsv
$acrPassword = az acr credential show --name $acrName --query "passwords[0].value" -o tsv
if (-not $acrUsername -or -not $acrPassword) {
    Write-Error "Could not retrieve ACR credentials."
    exit 1
}

# Parse .env file into environment variable array
$envVarsArray = @()
if (Test-Path -Path "..\config\.env") {
    Get-Content "..\config\.env" | ForEach-Object {
        if ($_ -match "^(?<key>[^=]+)=(?<value>.*)$") {
            $secureKeys = @("ALPACA_KEY","ALPACA_PAPER_API_KEY","ALPACA_PAPER_API_SECRET","ALPACA_SECRET","FINNHUB_API_KEY","OPENAI_API_KEY","AZURE_STORAGE_ACCOUNT_URL","COGNITIVE_SEARCH_ADMIN_KEY","STOCKRIPPER_CLIENT_SECRET","CLIENT_SECRET","REFRESH_TOKEN","BING_SUBSCRIPTION_KEY") # List sensitive variables here
            if ($matches['key'] -in $secureKeys) {
                $envVarsArray += @{
                    name = $matches['key']
                    secureValue = $matches['value'] # Use secureValue for sensitive variables
                }
            } else {
                $envVarsArray += @{
                    name = $matches['key']
                    value = $matches['value'] # Use value for non-sensitive variables
                }
            }
        }
    }
} else {
    Write-Error "The .env file does not exist."
    exit 1
}


function New-Parameters {
    param (
        $containerGroupName,
        $fsharpImage,
        $agentImage,
        $chatInterfaceImage,
        $rustImage,
        $envVarsArray,
        $acrUsername,
        $acrPassword,
        $uamiResourceId,
        $dnsNameLabel
    )

    return @{
        containerGroupName = @{ "value" = $containerGroupName }
        acrName = @{ "value" = $acrName }
        fsharpImage = @{ "value" = $fsharpImage }
        agentImage = @{ "value" = $agentImage }
        rustImage = @{ "value" = $rustImage }
        chatInterfaceImage = @{ "value" = $chatInterfaceImage }
        location = @{ "value" = $location }
        environmentVariables = @{ "value" = @($envVarsArray) }
        acrUsername = @{ "value" = $acrUsername }
        acrPassword = @{ "value" = $acrPassword }
        dnsNameLabel = @{ "value" = $dnsNameLabel }
        identity = @{
            "value" = @{
                type = "UserAssigned"
                userAssignedIdentities = @{
                    $uamiResourceId = @{}
                }
            }
        }
    }
}

$containerGroupName = "stockripper-container-group"
$dnsNameLabel = "stockripper"

# Create consolidated parameters
$containerGroupParameters = New-Parameters -containerGroupName $containerGroupName `
    -fsharpImage $fsharpAppImageName `
    -agentImage $agentAppImageName `
    -rustImage $rustAppImageName `
    -chatInterfaceImage $chatAppImageName `
    -envVarsArray $envVarsArray `
    -acrUsername $acrUsername `
    -acrPassword $acrPassword `
    -uamiResourceId $uamiResourceId `
    -dnsNameLabel $dnsNameLabel

# Convert parameters to JSON and write to file
$containerGroupParametersJson = $containerGroupParameters | ConvertTo-Json -Depth 10 -Compress
$containerGroupParametersFilePath = ".\containerGroupParameters.json"
$containerGroupParametersJson | Out-File -FilePath $containerGroupParametersFilePath -Encoding ascii

# Deploy container group
az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$containerGroupParametersFilePath

# Clean up parameter file
Remove-Item $containerGroupParametersFilePath

# Retrieve and assign DNS records for each container
$containerGroup = az container show --resource-group $resourceGroupName --name $containerGroupName --query "{ipAddress:ipAddress.ip, containers:containers}" -o json | ConvertFrom-Json
if (-not $containerGroup) {
    Write-Error "Could not retrieve the container group details."
    exit 1
}
