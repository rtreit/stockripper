import os
from azure.identity import AzureCliCredential
from azure.storage.blob import BlobServiceClient

# Set up logging
import logging
logging.basicConfig(level=logging.DEBUG)
logger = logging.getLogger(__name__)

# Load environment variables
AZURE_STORAGE_ACCOUNT_URL = "https://stockripperstg.blob.core.windows.net"

# Initialize the Azure CLI credential
credential = AzureCliCredential()

try:
    # Create a BlobServiceClient using the Azure CLI credential
    blob_service_client = BlobServiceClient(account_url=AZURE_STORAGE_ACCOUNT_URL, credential=credential)
    logger.debug("BlobServiceClient successfully created with AzureCliCredential.")

    # Test access by listing containers
    containers = blob_service_client.list_containers()
    for container in containers:
        logger.info(f"Container name: {container.name}")

except Exception as e:
    logger.error("Failed to create BlobServiceClient: %s", str(e))
    raise
