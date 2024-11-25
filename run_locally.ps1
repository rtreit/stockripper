pushd .\deployment
.\steps\buildContainers.ps1
.\runDockerLocally.ps1
popd
# sleep 2 seconds to let the containers start up
Start-Sleep -Seconds 2
Start-Process "msedge.exe" -ArgumentList "http://127.0.0.1"
