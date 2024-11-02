$acrName = "stockrippercr"
$resourceGroupName = "stockripper"
$location = if ($env:AZURE_LOCATION) { $env:AZURE_LOCATION } else { az group list --query "[0].location" --output tsv }
$containerGroupFSharp = "stockripper-fsharp-app"
$containerGroupPython = "stockripper-python-app"
$vnetName = "stockripperVNet"
$subnetName = "stockripperSubnet"
$subnetId = az network vnet subnet show --resource-group $resourceGroupName --vnet-name $vnetName --name $subnetName --query "id" -o tsv
$dnsZoneName = "stockripper.internal"

az acr login --name $acrName

$credentials = az acr credential show --name $acrName | ConvertFrom-Json
$acrUsername = $credentials.username
$acrPassword = $credentials.passwords[0].value

$envFilePath = "..\config\.env"
$resourceGroupName = "stockripper"

$envVars = Get-Content $envFilePath | Where-Object { $_.Trim() -ne "" } | Sort-Object -Unique

$envVarsArray = $envVars | ForEach-Object {
    $key, $value = $_ -split '='
    [PSCustomObject]@{ name = $key.Trim(); value = $value.Trim() }
}

$acrName = "stockrippercr"

$fsharpParameters = @{
    containerName = @{ "value" = $containerGroupFSharp }
    acrName = @{ "value" = $acrName }
    imageName = @{ "value" = "$acrName.azurecr.io/stockripper-fsharp-app:latest" }
    location = @{ "value" = $location }
    environmentVariables = @{ "value" = @($envVarsArray) }
    acrUsername = @{ "value" = $acrUsername }
    acrPassword = @{ "value" = $acrPassword }
    subnetId = @{ "value" = $subnetId }
}

$pythonParameters = @{
    containerName = @{ "value" = $containerGroupPython }
    acrName = @{ "value" = $acrName }
    imageName = @{ "value" = "$acrName.azurecr.io/stockripper-agent-app:latest" }
    location = @{ "value" = $location }
    environmentVariables = @{ "value" = @($envVarsArray) }
    acrUsername = @{ "value" = $acrUsername }
    acrPassword = @{ "value" = $acrPassword }
    subnetId = @{ "value" = $subnetId }
}


$fsharpParametersJson = $fsharpParameters | ConvertTo-Json -Depth 10 -Compress
$pythonParametersJson = $pythonParameters | ConvertTo-Json -Depth 10 -Compress

$fsharpParametersFilePath = ".\fsharpContainerParameters.json"
$pythonParametersFilePath = ".\pythonContainerParameters.json"

$fsharpParametersJson | Out-File -FilePath $fsharpParametersFilePath -Encoding ascii
$pythonParametersJson | Out-File -FilePath $pythonParametersFilePath -Encoding ascii

az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$fsharpParametersFilePath
az deployment group create --resource-group $resourceGroupName --template-file "..\config\containerTemplate.json" --parameters @$pythonParametersFilePath

remove-item $fsharpParametersFilePath
remove-item $pythonParametersFilePath

# Add DNS records so the containers can be accessed by name
$fsharpIp = az container show --name $containerGroupFSharp --resource-group $resourceGroupName --query "ipAddress.ip" -o tsv
$pythonIp = az container show --name $containerGroupPython --resource-group $resourceGroupName --query "ipAddress.ip" -o tsv

# Function to check if a DNS record exists and add it if it doesn't
function Add-DnsRecordIfNotExists($resourceGroupName, $dnsZoneName, $recordSetName, $ipAddress) {
    # Retrieve the record set's A records, focusing on the IP addresses
    $existingIps = az network private-dns record-set a show `
        --resource-group $resourceGroupName `
        --zone-name $dnsZoneName `
        --name $recordSetName `
        --query "aRecords[*].ipv4Address" -o tsv

    if ($existingIps -contains $ipAddress) {
        Write-Output "IP address $ipAddress exists in the DNS record set $recordSetName."
    } else {
        Write-Output "IP address $ipAddress does NOT exist in the DNS record set $recordSetName."
        Write-Output "Adding DNS record for $recordSetName with IP $ipAddress"
        az network private-dns record-set a add-record `
            --resource-group $resourceGroupName `
            --zone-name $dnsZoneName `
            --record-set-name $recordSetName `
            --ipv4-address $ipAddress        
    }
}

# Check and add DNS records if they don't already exist
Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName "stockripper-fsharp-app" -ipAddress $fsharpIp
Add-DnsRecordIfNotExists -resourceGroupName $resourceGroupName -dnsZoneName $dnsZoneName -recordSetName "stockripper-python-app" -ipAddress $pythonIp
