# Check if the Docker network "stockripper-network" already exists
$networkExists = docker network ls --filter "name=^stockripper-network$" -q

if (-not $networkExists) {
    Write-Output "Creating Docker network 'stockripper-network'..."
    docker network create --driver bridge --opt "com.docker.network.bridge.enable_icc=true" stockripper-network
} else {
    Write-Output "Docker network 'stockripper-network' already exists. Skipping creation."
}

# Define container names
$containers = @("stockripper-agent-app", "stockripper-fsharp-app")

# Stop and remove existing containers if they are running
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

# Run containers with network aliases and environment variables from the .env file
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-agent-app -v C:\users\randyt\.azure:/home/appuser/.azure -e AZURE_CONFIG_DIR=/home/appuser/.azure -p 5000:5000 -p 5678:5678 -e FLASK_ENV=development --network stockripper-network --network-alias stockripper-python-app.stockripper.internal --env-file $envFilePath stockripper-agent-app:latest"
Start-Sleep -Seconds 2
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-fsharp-app -v ~/.azure:/root/.azure -p 5001:5001 --network stockripper-network --network-alias stockripper-fsharp-app.stockripper.internal --env-file $envFilePath stockripper-fsharp-app:latest"
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-rust-app -v ~/.azure:/root/.azure -p 5002:5002 --network stockripper-network --network-alias stockripper-rust-app.stockripper.internal --env-file $envFilePath stockripper-rust-app:latest"


