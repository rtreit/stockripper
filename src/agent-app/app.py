from flask import Flask, request, jsonify
from azure.identity import DefaultAzureCredential, ManagedIdentityCredential
from azure.storage.blob import BlobServiceClient, BlobClient, ContainerClient
import os

app = Flask(__name__)

# Configuration for Azure Blob Storage
AZURE_STORAGE_ACCOUNT_URL = os.getenv("AZURE_STORAGE_ACCOUNT_URL")
AZURE_STORAGE_CONTAINER_NAME = os.getenv("AZURE_STORAGE_CONTAINER_NAME", "default-container")
USER_ASSIGNED_CLIENT_ID = os.getenv("USER_ASSIGNED_CLIENT_ID")

if not AZURE_STORAGE_ACCOUNT_URL:
    raise ValueError("AZURE_STORAGE_ACCOUNT_URL environment variable is not set. Please set it to the storage account URL.")

# Use ManagedIdentityCredential with User-Assigned Managed Identity if provided
if USER_ASSIGNED_CLIENT_ID:
    credential = ManagedIdentityCredential(client_id=USER_ASSIGNED_CLIENT_ID)
else:
    credential = DefaultAzureCredential()

blob_service_client = BlobServiceClient(account_url=AZURE_STORAGE_ACCOUNT_URL, credential=credential)
container_client = blob_service_client.get_container_client(AZURE_STORAGE_CONTAINER_NAME)

@app.route('/api/storage/save', methods=['POST'])
def save_to_storage():
    try:
        file = request.files['file']
        blob_name = request.form.get('blob_name', file.filename)
        
        # Upload the file to Azure Blob Storage
        blob_client = container_client.get_blob_client(blob_name)
        blob_client.upload_blob(file, overwrite=True)
        
        return jsonify({"message": "File uploaded successfully", "blob_name": blob_name}), 201
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route('/api/storage/get/<blob_name>', methods=['GET'])
def get_from_storage(blob_name):
    try:
        # Download the file from Azure Blob Storage
        blob_client = container_client.get_blob_client(blob_name)
        blob_data = blob_client.download_blob()
        
        return (blob_data.readall(), 200, {'Content-Type': 'application/octet-stream'})
    except Exception as e:
        return jsonify({"error": str(e)}), 500

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000)
