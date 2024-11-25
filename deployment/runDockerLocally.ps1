$containers = @("stockripper-agent-app", "stockripper-fsharp-app", "stockripper-rust-app", "stockripper-chat-app")

foreach ($container in $containers) {
    $existingContainer = docker ps -a --filter "name=$container" -q
    if ($existingContainer) {
        Write-Output "Stopping container '$container' if running..."
        docker stop $existingContainer | Out-Null
        Write-Output "Removing container '$container'..."
        docker rm $existingContainer
    }
}

$envFilePath = "..\config\.env" # Adjust the path to your .env file
$storageToken = az account get-access-token --resource https://storage.azure.com/ --query accessToken -o tsv
# Run containers with network aliases and environment variables from the .env file
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-agent-app -v $env:userprofile\.azure:/home/appuser/.azure -e AZURE_CONFIG_DIR=/home/appuser/.azure -p 5000:5000 -p 5678:5678 --network stockripper-network -e AZURE_STORAGE_TOKEN=$storageToken -e FLASK_ENV=development --env-file $envFilePath stockripper-agent-app:latest"
Start-Sleep -Seconds 2
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-fsharp-app -v $env:userprofile\.azure:/root/.azure -p 5001:5001 --network stockripper-network --env-file $envFilePath stockripper-fsharp-app:latest"
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-rust-app -v $env:userprofile\.azure:/root/.azure -p 5002:5002 --network stockripper-network --env-file $envFilePath stockripper-rust-app:latest"
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-chat-app -v $env:userprofile\.azure:/root/.azure -p 80:80 -p 443:443 --network stockripper-network --env-file $envFilePath -e AGENT_SERVICE_URL=http://stockripper-agent-app:5000/agents stockripper-chat-app:latest"

