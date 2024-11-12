# Motivation
StockRipper is a project to experiment with building AI agents to trade stocks.

This is for learning purposes only. 

I don't expect to make any money. 

# Environment
StockRipper is meant to run in Azure. The /deployment folder has all the scripts for setting up and deploying the needed Azure resources. 

# Containers

There are currently 4 containers:
1. stockripper-agent-app - a python agent built on langchain - listens on port 5000
2. stockripper-fsharp-app - placeholder for F# agent or helper code - listens on port 5001
3. stockripper-rust-app - placeholder for Rust agent or helper code - listens on port 5002
4. stockripper-chat-interface - a small chat interface to make interacting and testing easier - listens on port 5004

## Network
All the containers are on a network called stockripper.internal and communicate with each other's APIs via REST calls to endpoints on this network. 

# Running StockRipper

**Pre-requisites**: You'll need a .env file in the /config folder with the following environment variables:
```text
ALPACA_KEY=
ALPACA_PAPER_API_KEY=
ALPACA_PAPER_API_SECRET=
ALPACA_SECRET=
FINNHUB_API_KEY=
OPENAI_API_KEY=
AZURE_STORAGE_ACCOUNT_URL=
COGNITIVE_SEARCH_URL=
COGNITIVE_SEARCH_ADMIN_KEY=
EMAIL_SENDER=
STOCKRIPPER_CLIENT_ID=
STOCKRIPPER_CLIENT_SECRET=
CLIENT_ID=
CLIENT_SECRET=
TENANT_ID=
REFRESH_TOKEN=
```
The REFRESH_TOKEN is used for sending e-mail and can be obtained by running \stockripper\src\agent-app\get_mail_token.py

To run locally - from the project root:
1. .\run_locally.ps1

