$acrName = "stockrippercr"
az acr login --name $acrName

docker-compose -f "..\docker\docker-compose.yml" --env-file "..\config\.env" up -d


