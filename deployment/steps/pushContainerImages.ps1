$agentAppImageName = "stockripper-agent-app:latest"
$fsharpAppImageName = "stockripper-fsharp-app:latest"
$rustAppImageName = "stockripper-rust-app:latest"
$chatAppImageName = "stockripper-chat-app:latest"
$acrName = "stockrippercr"

az acr login --name $acrName

Write-Output "Tagging and pushing $agentAppImageName..."
docker tag $agentAppImageName "$acrName.azurecr.io/$agentAppImageName"
docker push "$acrName.azurecr.io/$agentAppImageName"

Write-Output "Tagging and pushing $fsharpAppImageName..."
docker tag $fsharpAppImageName "$acrName.azurecr.io/$fsharpAppImageName"
docker push "$acrName.azurecr.io/$fsharpAppImageName"

Write-Output "Tagging and pushing $rustAppImageName..."
docker tag $rustAppImageName "$acrName.azurecr.io/$rustAppImageName"
docker push "$acrName.azurecr.io/$rustAppImageName"

Write-Output "Tagging and pushing $chatAppImageName..."
docker tag $chatAppImageName "$acrName.azurecr.io/$chatAppImageName"
docker push "$acrName.azurecr.io/$chatAppImageName"