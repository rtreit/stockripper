$vnetName = "stockripperVNet"
$subnetName = "stockripperSubnet"
$location = if ($env:AZURE_LOCATION) { $env:AZURE_LOCATION } else { az group list --query "[0].location" --output tsv }
$resourceGroupName = "stockripper"
$dnsZoneName = "stockripper.internal"

# Function to create VNet and Subnet if they don’t exist
function Create-VNetAndSubnet {
    try {
        if (-not (az network vnet show --resource-group $resourceGroupName --name $vnetName -o none)) {
            Write-Output "Creating VNet and Subnet..."
            az network vnet create --name $vnetName --resource-group $resourceGroupName --location $location --address-prefixes "10.0.0.0/16" --subnet-name $subnetName --subnet-prefix "10.0.1.0/24"
        } else {
            Write-Output "VNet already exists."
        }
    } catch {
        Write-Output "Error creating VNet or Subnet: $_"
        exit 1
    }
}

# Function to update subnet with required delegation
function Update-SubnetDelegation {
    try {
        Write-Output "Updating subnet with delegation..."
        az network vnet subnet update `
            --name $subnetName `
            --vnet-name $vnetName `
            --resource-group $resourceGroupName `
            --delegations "Microsoft.ContainerInstance/containerGroups"
    } catch {
        Write-Output "Error updating subnet delegation: $_"
        exit 1
    }
}

# Function to create Private DNS Zone if it does not exist
function Create-PrivateDnsZone {
    try {
        if (-not (az network private-dns zone show --resource-group $resourceGroupName --name $dnsZoneName -o none)) {
            Write-Output "Creating Private DNS Zone..."
            az network private-dns zone create --resource-group $resourceGroupName --name $dnsZoneName
        } else {
            Write-Output "Private DNS Zone already exists."
        }
    } catch {
        if ($_.Exception.Message -match "PreconditionFailed") {
            Write-Output "Private DNS Zone already exists, skipping creation."
        } else {
            Write-Output "Error creating Private DNS Zone: $_"
            exit 1
        }
    }
}

# Function to link VNet to DNS Zone if the link does not exist
function Link-VNetToDnsZone {
    $dnsLinkName = "stockripperDNSLink"
    try {
        if (-not (az network private-dns link vnet show --resource-group $resourceGroupName --zone-name $dnsZoneName --name $dnsLinkName -o none)) {
            Write-Output "Creating Private DNS Link..."
            az network private-dns link vnet create --resource-group $resourceGroupName --zone-name $dnsZoneName --name $dnsLinkName --virtual-network $vnetName --registration-enabled false
        } else {
            Write-Output "Private DNS Link already exists."
        }
    } catch {
        if ($_.Exception.Message -match "PreconditionFailed") {
            Write-Output "Private DNS Link already exists, skipping creation."
        } else {
            Write-Output "Error creating Private DNS Link: $_"
            exit 1
        }
    }
}

# Run the functions
Create-VNetAndSubnet
Update-SubnetDelegation
Create-PrivateDnsZone
Link-VNetToDnsZone
