$acrName = "stockrippercr"
$resourceGroupName = "stockripper"
$location = if ($env:AZURE_LOCATION) { $env:AZURE_LOCATION } else { az group list --query "[0].location" --output tsv }
$containerGroupFSharp = "stockripper-fsharp-app"
$containerGroupPython = "stockripper-python-app"

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
    environmentVariables = @{ "value" = @($envVarsArray) }  # Wrap in an array
    acrUsername = @{ "value" = $acrUsername }
    acrPassword = @{ "value" = $acrPassword }
}

$pythonParameters = @{
    containerName = @{ "value" = $containerGroupPython }
    acrName = @{ "value" = $acrName }
    imageName = @{ "value" = "$acrName.azurecr.io/stockripper-agent-app:latest" }
    location = @{ "value" = $location }
    environmentVariables = @{ "value" = @($envVarsArray) }  # Wrap in an array
    acrUsername = @{ "value" = $acrUsername }
    acrPassword = @{ "value" = $acrPassword }
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