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

$vnetName = "stockripperVNet"
$subnetName = "stockripperSubnet"

# Retrieve subnet ID
$subnetId = az network vnet subnet show --resource-group $resourceGroupName --vnet-name $vnetName --name $subnetName --query "id" -o tsv
if (-not $subnetId) {
    Write-Error "Could not retrieve the subnet ID. Please check that the VNet and Subnet exist."
    exit 1
}

$dnsZoneName = "stockripper.internal"

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

# Define environment variables array (update with actual variables if needed)
$envVarsArray = @()

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
        $subnetId,
        $uamiResourceId
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
        subnetId = @{ "value" = $subnetId }
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

# Create consolidated parameters
$containerGroupName = "stockripper-container-group"
$containerGroupParameters = New-Parameters -containerGroupName $containerGroupName `
    -fsharpImage $fsharpAppImageName `
    -agentImage $agentAppImageName `
    -rustImage $rustAppImageName `
    -chatInterfaceImage $chatAppImageName `
    -envVarsArray $envVarsArray `
    -acrUsername $acrUsername `
    -acrPassword $acrPassword `
    -subnetId $subnetId `
    -uamiResourceId $uamiResourceId

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

function Add-DnsRecordIfNotExists($resourceGroupName, $dnsZoneName, $recordSetName, $ipAddress) {
    try {
        $existingIps = az network private-dns record-set a show `
            --resource-group $resourceGroupName `
            --zone-name $dnsZoneName `
            --name $recordSetName `
            --query "aRecords[*].ipv4Address" -o tsv 2>$null
        $recordExists = $true
    } catch {
        $recordExists = $false
        $existingIps = @()
    }

    if ($recordExists -and ($existingIps -contains $ipAddress)) {
        Write-Output "IP address $ipAddress exists in the DNS record set $recordSetName."
    } else {
        if (-not $recordExists) {
            Write-Output "DNS record set $recordSetName does not exist. Creating it."
            az network private-dns record-set a create `
                --resource-group $resourceGroupName `
                --zone-name $dnsZoneName `
                --name $recordSetName
        }
        Write-Output "Adding DNS record for $recordSetName with IP $ipAddress"
        az network private-dns record-set a add-record `
            --resource-group $resourceGroupName `
            --zone-name $dnsZoneName `
            --record-set-name $recordSetName `
            --ipv4-address $ipAddress        
    }
}

# Since all containers share the same IP in a container group, use container names
$ipAddress = $containerGroup.ipAddress
foreach ($container in $containerGroup.containers) {
    $containerName = $container.name
    Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName $containerName -ipAddress $ipAddress
}
