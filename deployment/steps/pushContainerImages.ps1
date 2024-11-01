$agentAppImageName = "stockripper-agent-app:latest"
$fsharpAppImageName = "stockripper-fsharp-app:latest"
$acrName = "stockrippercr"

az acr login --name $acrName

Write-Output "Tagging and pushing $agentAppImageName..."
docker tag $agentAppImageName "$acrName.azurecr.io/$agentAppImageName"
docker push "$acrName.azurecr.io/$agentAppImageName"

Write-Output "Tagging and pushing $fsharpAppImageName..."
docker tag $fsharpAppImageName "$acrName.azurecr.io/$fsharpAppImageName"
docker push "$acrName.azurecr.io/$fsharpAppImageName"

