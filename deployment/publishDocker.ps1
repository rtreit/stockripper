$acr = "stockrippercr"
az acr login --name stockrippercr
docker build -t stockripper:latest .
docker tag stockripper:latest $("$acr.azurecr.io/stockripper:latest")
docker push $("$acr.azurecr.io/stockripper:latest")
