$resourceGroup = "stockripper"
$identityName = "stockripper-uami"
$outputFile = "uami_details.json"

try {
    $identity = az identity show --resource-group $resourceGroup --name $identityName --query "{id:id, clientId:clientId, principalId:principalId}" --output json 2>$null | ConvertFrom-Json
} catch {
    $identity = $null
}

if (-not $identity) {
    Write-Output "User-assigned managed identity does not exist. Creating..."

    az identity create --resource-group $resourceGroup --name $identityName

    $identity = az identity show --resource-group $resourceGroup --name $identityName --query "{id:id, clientId:clientId, principalId:principalId}" --output json | ConvertFrom-Json
} else {
    Write-Output "User-assigned managed identity already exists."
}

$identity | ConvertTo-Json | Out-File -FilePath $outputFile -Encoding utf8
Write-Output "Managed identity details saved to $outputFile"
