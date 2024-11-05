from flask import Flask, request, jsonify
from azure.identity import AzureCliCredential, ManagedIdentityCredential, CredentialUnavailableError
from azure.storage.blob import BlobServiceClient, BlobClient, ContainerClient
import os
import logging

app = Flask(__name__)

# Configure logging
logging.basicConfig(level=logging.DEBUG)
logger = logging.getLogger(__name__)

# Configuration for Azure Blob Storage
AZURE_STORAGE_ACCOUNT_URL = os.getenv("AZURE_STORAGE_ACCOUNT_URL")
AZURE_STORAGE_CONTAINER_NAME = os.getenv("AZURE_STORAGE_CONTAINER_NAME", "default-container")
USER_ASSIGNED_CLIENT_ID = os.getenv("USER_ASSIGNED_CLIENT_ID")

if not AZURE_STORAGE_ACCOUNT_URL:
    raise ValueError("AZURE_STORAGE_ACCOUNT_URL environment variable is not set. Please set it to the storage account URL.")

# Use ManagedIdentityCredential with User-Assigned Managed Identity if provided
if USER_ASSIGNED_CLIENT_ID:
    logger.debug("Using ManagedIdentityCredential with client ID: %s", USER_ASSIGNED_CLIENT_ID)
    credential = ManagedIdentityCredential(client_id=USER_ASSIGNED_CLIENT_ID)
else:
    logger.debug("Using AzureCliCredential explicitly for local development")
    credential = AzureCliCredential()

# Try to create the BlobServiceClient and catch any credential errors
try:
    blob_service_client = BlobServiceClient(account_url=AZURE_STORAGE_ACCOUNT_URL, credential=credential)
    container_client = blob_service_client.get_container_client(AZURE_STORAGE_CONTAINER_NAME)
    logger.debug("BlobServiceClient successfully created.")
except CredentialUnavailableError as e:
    logger.error("Credential unavailable: %s", str(e))
    raise
except Exception as e:
    logger.error("Failed to create BlobServiceClient: %s", str(e))
    raise

@app.route('/api/storage/save', methods=['POST'])
def save_to_storage():
    try:
        logger.debug("Received request to save file to storage.")
        file = request.files['file']
        blob_name = request.form.get('blob_name', file.filename)
        logger.debug("Blob name: %s", blob_name)
        
        # Upload the file to Azure Blob Storage
        blob_client = container_client.get_blob_client(blob_name)
        blob_client.upload_blob(file, overwrite=True)
        logger.debug("File uploaded successfully: %s", blob_name)
        
        return jsonify({"message": "File uploaded successfully", "blob_name": blob_name}), 201
    except Exception as e:
        logger.error("Error in save_to_storage: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500

@app.route('/api/storage/get/<blob_name>', methods=['GET'])
def get_from_storage(blob_name):
    try:
        logger.debug("Received request to get file from storage. Blob name: %s", blob_name)
        # Download the file from Azure Blob Storage
        blob_client = container_client.get_blob_client(blob_name)
        blob_data = blob_client.download_blob()
        logger.debug("File downloaded successfully: %s", blob_name)
        
        return (blob_data.readall(), 200, {'Content-Type': 'application/octet-stream'})
    except Exception as e:
        logger.error("Error in get_from_storage: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000)
