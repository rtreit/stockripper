$resourceGroupName = "stockripper"
$publicIpName = "stockRipperChatAppPublicIp"

az network public-ip create `
  --resource-group $resourceGroupName `
  --name $publicIpName `
  --allocation-method Static `
  --sku Standard