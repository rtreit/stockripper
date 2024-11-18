$agentAppImageName = "stockripper-agent-app:latest"
$fsharpAppImageName = "stockripper-fsharp-app:latest"
$rustAppImageName = "stockripper-rust-app:latest"
$chatinterface = "stockripper-chat-app:latest"

Write-Output "Building $agentAppImageName"
docker build -t $agentAppImageName -f "../src/agent-app/Dockerfile" "../src/agent-app"

Write-Output "Building $fsharpAppImageName"
docker build -t $fsharpAppImageName -f "../src/fsharp-app/Dockerfile" "../src/fsharp-app"

Write-Output "Building $rustAppImageName"
docker build -t $rustAppImageName -f "../src/rust-app/Dockerfile" "../src/rust-app"

Write-Output "Building $chatinterface"
docker build -t $chatinterface -f "../src/chat-interface/Dockerfile" "../src/chat-interface"