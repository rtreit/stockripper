# login to the container registry
$acrName = "stockrippercr"
az acr login --name $acrName

# we'll need helm, the kubernetes package manager
winget install Helm.Helm

# switch to docker-desktop kubernetes context
kubectl config use-context docker-desktop

helm repo add jupyterhub https://hub.jupyter.org/helm-chart/
helm repo update

helm upgrade --cleanup-on-fail `
  --install stockripper jupyterhub/jupyterhub `
  --namespace stockripper `
  --create-namespace `
  --version=4.0.0 `
  --values config.yaml