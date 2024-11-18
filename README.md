# Stockripper 
A PoC around building a multi-agent sytem. 

# Pre-requisites
Prior to deployment, set your Azure subscription ID:

```powershell
[System.Environment]::SetEnvironmentVariable('AGENTIC_SUBSCRIPTION_ID', '<YOUR SUBSCRIPTION ID HERE>', [System.EnvironmentVariableTarget]::User)
```

Login to the target tenant.
```powershell
az login
```

Add Azure ML extension 
```powershell
az extension add --name ml
```