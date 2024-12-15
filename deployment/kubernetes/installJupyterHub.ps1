helm upgrade --cleanup-on-fail `
  --install stockripper jupyterhub/jupyterhub `
  --namespace stockripper `
  --create-namespace `
  --version=4.0.0 `
  --values config.yaml