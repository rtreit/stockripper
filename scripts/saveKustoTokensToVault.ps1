while ($true) {
    $KeyVaultName = "mtprarchitectm8544494073"
    $SecretName = "WcdScrubbedAuthToken"
    $KustoCluster = "https://wcdscrubbedfollowereus.eastus2.kusto.windows.net"         

    Write-Output "Getting WcdScrubbed Kusto authentication token..."
    $AccessTokenJson = az account get-access-token --resource $KustoCluster --query "{accessToken: accessToken}" --output json

    if (-not $AccessTokenJson) {
        Write-Error "Failed to get access token. Ensure you're logged in to Azure CLI."
        exit 1
    }

    $AccessToken = ($AccessTokenJson | ConvertFrom-Json).accessToken

    Write-Output "Saving the access token to Key Vault..."
    az keyvault secret set --vault-name $KeyVaultName --name $SecretName --content-type "text/plain" --value $AccessToken

    Write-Output "Access token saved to Key Vault as secret '$SecretName'."
    Write-Output "Sleeping for for a half hour..."
    Start-Sleep -Seconds 1800
}
