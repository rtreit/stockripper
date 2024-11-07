$acrName = "stockrippercr"
$resourceGroupName = "stockripper"
$location = if ($env:AZURE_LOCATION) { $env:AZURE_LOCATION } else { az group list --query "[0].location" --output tsv }
$containerGroupFSharp = "stockripper-fsharp-app"
$containerGroupPython = "stockripper-python-app"
$containerGroupRust = "stockripper-rust-app"
$vnetName = "stockripperVNet"
$subnetName = "stockripperSubnet"
$subnetId = az network vnet subnet show --resource-group $resourceGroupName --vnet-name $vnetName --name $subnetName --query "id" -o tsv
$dnsZoneName = "stockripper.internal"

# Load UAMI details
$uamiDetailsFile = ".\uami_details.json"
if (Test-Path -Path $uamiDetailsFile) {
    $uamiDetails = Get-Content -Path $uamiDetailsFile | ConvertFrom-Json
    $uamiResourceId = $uamiDetails.id  # Ensure this line is included to fetch the full resource ID
} else {
    Write-Output "UAMI details file $uamiDetailsFile does not exist."
    exit 1
}

az acr login --name $acrName

$credentials = az acr credential show --name $acrName | ConvertFrom-Json
$acrUsername = $credentials.username
# $acrPassword = ConvertTo-SecureString $credentials.passwords[0].value -AsPlainText -Force 
$acrPassword = $credentials.passwords[0].value 

$envFilePath = "..\config\.env"
$envVars = Get-Content $envFilePath | Where-Object { $_.Trim() -ne "" } | Sort-Object -Unique

$envVarsArray = $envVars | ForEach-Object {
    $key, $value = $_ -split '='
    [PSCustomObject]@{ name = $key.Trim(); value = $value.Trim() }
}

# Function to create the parameters
function New-Parameters {
    param (
        $containerName,
        $imageName,
        $envVarsArray,
        $acrUsername,
        $acrPassword,
        $subnetId,
        $uamiResourceId
    )

    return @{
        containerName = @{ "value" = $containerName }
        acrName = @{ "value" = $acrName }
        imageName = @{ "value" = $imageName }
        location = @{ "value" = $location }
        environmentVariables = @{ "value" = @($envVarsArray) }
        acrUsername = @{ "value" = $acrUsername }
        acrPassword = @{ "value" = $acrPassword }
        subnetId = @{ "value" = $subnetId }
        identity = @{ 
            "value" = @{
                type = "UserAssigned"
                userAssignedIdentities = @{
                    $uamiResourceId = @{}  # Ensure this is a valid key
                }
            }
        }
    }
}

# Create parameters for each container
$fsharpParameters = New-Parameters -containerName $containerGroupFSharp -imageName "$acrName.azurecr.io/stockripper-fsharp-app:latest" -envVarsArray $envVarsArray -acrUsername $acrUsername -acrPassword $acrPassword -subnetId $subnetId -uamiResourceId $uamiResourceId
$pythonParameters = New-Parameters -containerName $containerGroupPython -imageName "$acrName.azurecr.io/stockripper-agent-app:latest" -envVarsArray $envVarsArray -acrUsername $acrUsername -acrPassword $acrPassword -subnetId $subnetId -uamiResourceId $uamiResourceId
$rustParameters = New-Parameters -containerName $containerGroupRust -imageName "$acrName.azurecr.io/stockripper-rust-app:latest" -envVarsArray $envVarsArray -acrUsername $acrUsername -acrPassword $acrPassword -subnetId $subnetId -uamiResourceId $uamiResourceId

# Convert parameters to JSON
$fsharpParametersJson = $fsharpParameters | ConvertTo-Json -Depth 10 -Compress
$pythonParametersJson = $pythonParameters | ConvertTo-Json -Depth 10 -Compress
$rustParametersJson = $rustParameters | ConvertTo-Json -Depth 10 -Compress

# File paths for parameters
$fsharpParametersFilePath = ".\fsharpContainerParameters.json"
$pythonParametersFilePath = ".\pythonContainerParameters.json"
$rustParametersFilePath = ".\rustContainerParameters.json"

# Write parameters to files
$fsharpParametersJson | Out-File -FilePath $fsharpParametersFilePath -Encoding ascii
$pythonParametersJson | Out-File -FilePath $pythonParametersFilePath -Encoding ascii
$rustParametersJson | Out-File -FilePath $rustParametersFilePath -Encoding ascii

# Deploy container groups
az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$fsharpParametersFilePath
az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$pythonParametersFilePath
az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$rustParametersFilePath

# Clean up parameter files
remove-item $fsharpParametersFilePath
remove-item $pythonParametersFilePath
remove-item $rustParametersFilePath

# Retrieve and assign DNS records for each container
$fsharpIp = az container show --name $containerGroupFSharp --resource-group $resourceGroupName --query "ipAddress.ip" -o tsv
$pythonIp = az container show --name $containerGroupPython --resource-group $resourceGroupName --query "ipAddress.ip" -o tsv
$rustIp = az container show --name $containerGroupRust --resource-group $resourceGroupName --query "ipAddress.ip" -o tsv

function Add-DnsRecordIfNotExists($resourceGroupName, $dnsZoneName, $recordSetName, $ipAddress) {
    $existingIps = az network private-dns record-set a show `
        --resource-group $resourceGroupName `
        --zone-name $dnsZoneName `
        --name $recordSetName `
        --query "aRecords[*].ipv4Address" -o tsv

    if ($existingIps -contains $ipAddress) {
        Write-Output "IP address $ipAddress exists in the DNS record set $recordSetName."
    } else {
        Write-Output "Adding DNS record for $recordSetName with IP $ipAddress"
        az network private-dns record-set a add-record `
            --resource-group $resourceGroupName `
            --zone-name $dnsZoneName `
            --record-set-name $recordSetName `
            --ipv4-address $ipAddress        
    }
}

Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName "stockripper-fsharp-app" -ipAddress $fsharpIp
Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName "stockripper-python-app" -ipAddress $pythonIp
Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName "stockripper-rust-app" -ipAddress $rustIp
# Load UAMI details
$uamiDetailsFile = ".\uami_details.json"
if (Test-Path -Path $uamiDetailsFile) {
    $uamiDetails = Get-Content -Path $uamiDetailsFile | ConvertFrom-Json
    $uamiResourceId = $uamiDetails.id  # Ensure this line is included to fetch the full resource ID
} else {
    Write-Output "UAMI details file $uamiDetailsFile does not exist."
    exit 1
}

az acr login --name $acrName

$credentials = az acr credential show --name $acrName | ConvertFrom-Json
$acrUsername = $credentials.username
# $acrPassword = ConvertTo-SecureString $credentials.passwords[0].value -AsPlainText -Force 
$acrPassword = $credentials.passwords[0].value 

$envFilePath = "..\config\.env"
$envVars = Get-Content $envFilePath | Where-Object { $_.Trim() -ne "" } | Sort-Object -Unique

$envVarsArray = $envVars | ForEach-Object {
    $key, $value = $_ -split '='
    [PSCustomObject]@{ name = $key.Trim(); value = $value.Trim() }
}

# Function to create the parameters
function New-Parameters {
    param (
        $containerName,
        $imageName,
        $envVarsArray,
        $acrUsername,
        $acrPassword,
        $subnetId,
        $uamiResourceId
    )

    return @{
        containerName = @{ "value" = $containerName }
        acrName = @{ "value" = $acrName }
        imageName = @{ "value" = $imageName }
        location = @{ "value" = $location }
        environmentVariables = @{ "value" = @($envVarsArray) }
        acrUsername = @{ "value" = $acrUsername }
        acrPassword = @{ "value" = $acrPassword }
        subnetId = @{ "value" = $subnetId }
        identity = @{ 
            "value" = @{
                type = "UserAssigned"
                userAssignedIdentities = @{
                    $uamiResourceId = @{}  # Ensure this is a valid key
                }
            }
        }
    }
}

# Create parameters for each container
$fsharpParameters = New-Parameters -containerName $containerGroupFSharp -imageName "$acrName.azurecr.io/stockripper-fsharp-app:latest" -envVarsArray $envVarsArray -acrUsername $acrUsername -acrPassword $acrPassword -subnetId $subnetId -uamiResourceId $uamiResourceId
$pythonParameters = New-Parameters -containerName $containerGroupPython -imageName "$acrName.azurecr.io/stockripper-agent-app:latest" -envVarsArray $envVarsArray -acrUsername $acrUsername -acrPassword $acrPassword -subnetId $subnetId -uamiResourceId $uamiResourceId
$rustParameters = New-Parameters -containerName $containerGroupRust -imageName "$acrName.azurecr.io/stockripper-rust-app:latest" -envVarsArray $envVarsArray -acrUsername $acrUsername -acrPassword $acrPassword -subnetId $subnetId -uamiResourceId $uamiResourceId

# Convert parameters to JSON
$fsharpParametersJson = $fsharpParameters | ConvertTo-Json -Depth 10 -Compress
$pythonParametersJson = $pythonParameters | ConvertTo-Json -Depth 10 -Compress
$rustParametersJson = $rustParameters | ConvertTo-Json -Depth 10 -Compress

# File paths for parameters
$fsharpParametersFilePath = ".\fsharpContainerParameters.json"
$pythonParametersFilePath = ".\pythonContainerParameters.json"
$rustParametersFilePath = ".\rustContainerParameters.json"

# Write parameters to files
$fsharpParametersJson | Out-File -FilePath $fsharpParametersFilePath -Encoding ascii
$pythonParametersJson | Out-File -FilePath $pythonParametersFilePath -Encoding ascii
$rustParametersJson | Out-File -FilePath $rustParametersFilePath -Encoding ascii

# Deploy container groups
az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$fsharpParametersFilePath
az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$pythonParametersFilePath
az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$rustParametersFilePath

# Clean up parameter files
remove-item $fsharpParametersFilePath
remove-item $pythonParametersFilePath
remove-item $rustParametersFilePath

# Retrieve and assign DNS records for each container
$fsharpIp = az container show --name $containerGroupFSharp --resource-group $resourceGroupName --query "ipAddress.ip" -o tsv
$pythonIp = az container show --name $containerGroupPython --resource-group $resourceGroupName --query "ipAddress.ip" -o tsv
$rustIp = az container show --name $containerGroupRust --resource-group $resourceGroupName --query "ipAddress.ip" -o tsv

function Add-DnsRecordIfNotExists($resourceGroupName, $dnsZoneName, $recordSetName, $ipAddress) {
    $existingIps = az network private-dns record-set a show `
        --resource-group $resourceGroupName `
        --zone-name $dnsZoneName `
        --name $recordSetName `
        --query "aRecords[*].ipv4Address" -o tsv

    if ($existingIps -contains $ipAddress) {
        Write-Output "IP address $ipAddress exists in the DNS record set $recordSetName."
    } else {
        Write-Output "Adding DNS record for $recordSetName with IP $ipAddress"
        az network private-dns record-set a add-record `
            --resource-group $resourceGroupName `
            --zone-name $dnsZoneName `
            --record-set-name $recordSetName `
            --ipv4-address $ipAddress        
    }
}

Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName "stockripper-fsharp-app" -ipAddress $fsharpIp
Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName "stockripper-python-app" -ipAddress $pythonIp
Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName "stockripper-rust-app" -ipAddress $rustIp
