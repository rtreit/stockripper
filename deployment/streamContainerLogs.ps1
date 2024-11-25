$containerName = "agent-container"
az container logs `
            --resource-group stockripper `
            --name stockripper-container-group `
            --container-name $containerName `
            --follow