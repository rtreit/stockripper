if ($args.Length -gt 0)
{
    $env:AZURE_LOCATION=$args[0]
}
else
{
    $env:AZURE_LOCATION = az group list --query "[0].location" --output tsv
}

.\steps\buildContainers.ps1
.\steps\setSubscription.ps1
.\steps\createResourceGroup.ps1
.\steps\deployContainerRegistry.ps1
.\steps\pushContainerImages.ps1
.\steps\deployCognitiveSearch.ps1
.\steps\createVnet.ps1
.\steps\deployContainerInstances.ps1

