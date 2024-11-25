# Variables
$resourceGroupName = "stockripper"
$gatewayName = "stockripper-app-gateway"
$vnetName = "stockripperVNet"
$subnetName = "appGatewaySubnet"
$publicIpName = "stockRipperChatAppPublicIp"

# Get the container group's private IP address
$containerPrivateIp = az container show --resource-group $resourceGroupName --name stockripper-container-group --query "ipAddress.ip" -o tsv


# create a subnet for the Application Gateway if it doesn't exist
az network vnet subnet show `
    --resource-group $resourceGroupName `
    --vnet-name $vnetName `
    --name $subnetName -o none || az network vnet subnet create `
        --resource-group $resourceGroupName `
        --vnet-name $vnetName `
        --name $subnetName `
        --address-prefixes 10.0.2.0/24


# Validate all variables
Write-Output "Resource Group: $resourceGroupName"
Write-Output "VNet Name: $vnetName"
Write-Output "Subnet Name: $subnetName"
Write-Output "Public IP Name: $publicIpName"
Write-Output "Container Private IP: $containerPrivateIp"

# Create the Application Gateway
az network application-gateway create `
    --name $gatewayName `
    --location westus `
    --resource-group $resourceGroupName `
    --capacity 2 `
    --sku Standard_v2 `
    --public-ip-address $publicIpName `
    --vnet-name "stockripperVNet" `
    --subnet $subnetName `
    --servers $containerPrivateIp `
    --priority 100
