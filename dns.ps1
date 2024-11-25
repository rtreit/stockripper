# Define variables
$dnsZoneName = "stockripper.internal"
$resourceGroupName = "stockripper"
$containerGroupName = "stockripper-container-group"

# Get the IP of the container group
$containerIp = az container show `
    --resource-group $resourceGroupName `
    --name $containerGroupName `
    --query "ipAddress.ip" -o tsv

if (-not $containerIp) {
    Write-Error "Could not retrieve the container group IP."
    exit 1
}

# List existing DNS records
$dnsRecords = az network private-dns record-set a list `
    --resource-group $resourceGroupName `
    --zone-name $dnsZoneName `
    --query "[].name" -o tsv

# Delete existing records
foreach ($record in $dnsRecords) {
    Write-Output "Deleting DNS record: $record"
    az network private-dns record-set a delete `
        --resource-group $resourceGroupName `
        --zone-name $dnsZoneName `
        --name $record --yes
}

# Define container names
$containerNames = @("stockripper-agent-app", "stockripper-fsharp-app", "stockripper-rust-app", "stockripper-chat-app")

# Add new records
foreach ($containerName in $containerNames) {
    Write-Output "Adding DNS record for: $containerName"
    az network private-dns record-set a add-record `
        --resource-group $resourceGroupName `
        --zone-name $dnsZoneName `
        --record-set-name $containerName `
        --ipv4-address $containerIp
}

# Verify
az network private-dns record-set a list `
    --resource-group $resourceGroupName `
    --zone-name $dnsZoneName `
    --query "[].{Name:name,IP:aRecords[].ipv4Address}" -o table
