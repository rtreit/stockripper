$agentAppImageName = "stockripper-agent-app:latest"
$fsharpAppImageName = "stockripper-fsharp-app:latest"

Write-Output "Building $agentAppImageName"
docker build -t $agentAppImageName -f "../src/agent-app/Dockerfile" "../src/agent-app"

Write-Output "Building $fsharpAppImageName"
docker build -t $fsharpAppImageName -f "../src/fsharp-app/Dockerfile" "../src/fsharp-app"
