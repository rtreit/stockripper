$vnetName = "stockripperVNet"
$subnetName = "stockripperSubnet"
$location = if ($env:AZURE_LOCATION) { $env:AZURE_LOCATION } else { az group list --query "[0].location" --output tsv }
$resourceGroupName = "stockripper"
$dnsZoneName = "stockripper.internal"

az network vnet create --name $vnetName --resource-group $resourceGroupName --location $location --address-prefixes "10.0.0.0/16" --subnet-name $subnetName --subnet-prefix "10.0.1.0/24" 
az network vnet subnet update `
  --name $subnetName `
  --vnet-name $vnetName `
  --resource-group $resourceGroupName `
  --delegations "Microsoft.ContainerInstance/containerGroups"

az network private-dns zone create --resource-group $resourceGroupName --name $dnsZoneName
az network private-dns link vnet create --resource-group $resourceGroupName --zone-name $dnsZoneName --name "stockripperDNSLink" --virtual-network $vnetName --registration-enabled false

