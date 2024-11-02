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

az network private-dns record-set a add-record --resource-group $resourceGroupName --zone-name $dnsZoneName --record-set-name "stockripper-fsharp-app" --ipv4-address $fsharpIp
az network private-dns record-set a add-record --resource-group $resourceGroupName --zone-name $dnsZoneName --record-set-name "stockripper-python-app" --ipv4-address $pythonIp

