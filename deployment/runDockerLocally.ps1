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

# Run containers with network aliases to simulate Azure setup
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-agent-app -p 5000:5000 --network stockripper-network --network-alias stockripper-python-app.stockripper.internal stockripper-agent-app:latest"
Start-Process -FilePath "docker" -ArgumentList "run --name stockripper-fsharp-app -p 5001:5001 --network stockripper-network --network-alias stockripper-fsharp-app.stockripper.internal stockripper-fsharp-app:latest"
