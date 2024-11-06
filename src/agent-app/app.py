from flask import Flask, request, jsonify
from azure.identity import DefaultAzureCredential, CredentialUnavailableError
from azure.storage.blob import BlobServiceClient, BlobClient, ContainerClient
import os
import logging
import debugpy

app = Flask(__name__)

# Configure logging
logging.basicConfig(level=logging.DEBUG)
logger = logging.getLogger(__name__)

# Configuration for Azure Blob Storage
AZURE_STORAGE_ACCOUNT_URL = os.getenv("AZURE_STORAGE_ACCOUNT_URL")
AZURE_STORAGE_CONTAINER_NAME = os.getenv("AZURE_STORAGE_CONTAINER_NAME", "default-container")

if not AZURE_STORAGE_ACCOUNT_URL:
    raise ValueError("AZURE_STORAGE_ACCOUNT_URL environment variable is not set. Please set it to the storage account URL.")

# Use DefaultAzureCredential for authentication
logger.debug("Using DefaultAzureCredential for authentication")
credential = DefaultAzureCredential()

try:
    # Create the BlobServiceClient
    blob_service_client = BlobServiceClient(account_url=AZURE_STORAGE_ACCOUNT_URL, credential=credential)
    logger.debug("BlobServiceClient successfully created.")
except CredentialUnavailableError as e:
    logger.error("Credential unavailable: %s", str(e))
    raise
except Exception as e:
    logger.error("Failed to create BlobServiceClient: %s", str(e))
    raise

@app.route('/api/storage/containers/<container_name>/blobs', methods=['POST'])
@app.route('/api/storage/containers/<container_name>/blobs', methods=['POST'])
def save_to_storage(container_name: str):
    try:
        logger.debug("Received request to save files to storage. Container name: %s", container_name)

        # Validate if any files are provided
        if 'file' not in request.files:
            return jsonify({"error": "No files provided"}), 400

        files = request.files.getlist('file')
        blob_names = []

        # Use form data instead of JSON for blob_name
        blob_name_template = request.form.get("blob_name")  # Optional blob name template

        # Initialize the container client
        container_client = blob_service_client.get_container_client(container_name)
        # Create the container if it does not exist
        if not container_client.exists():
            container_client.create_container()
            logger.debug("Container created: %s", container_name)

        for file in files:
            blob_name = file.filename or blob_name_template
            blob_client = container_client.get_blob_client(blob_name)
            blob_client.upload_blob(file, overwrite=True)
            blob_names.append(blob_name)
            logger.debug("File uploaded successfully: %s", blob_name)

        return jsonify({"message": "Files uploaded successfully", "blob_names": blob_names}), 201

    except Exception as e:
        logger.error("Error in save_to_storage: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500



@app.route('/api/storage/containers/<container_name>/blobs/<blob_name>', methods=['GET'])
def get_from_storage(container_name, blob_name):
    try:
        logger.debug("Received request to get file from storage. Container name: %s, Blob name: %s", container_name, blob_name)
        # Get the container client
        container_client = blob_service_client.get_container_client(container_name)
        # Download the file from Azure Blob Storage
        blob_client = container_client.get_blob_client(blob_name)
        blob_data = blob_client.download_blob()
        logger.debug("File downloaded successfully: %s", blob_name)
        
        return (blob_data.readall(), 200, {'Content-Type': 'application/octet-stream'})
    except Exception as e:
        logger.error("Error in get_from_storage: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500

@app.route('/api/storage/containers', methods=['GET'])
def list_containers():
    try:
        logger.debug("Received request to list all containers.")
        containers = blob_service_client.list_containers()
        container_names = [container.name for container in containers]
        logger.debug("Containers listed successfully.")
        
        return jsonify({"containers": container_names}), 200
    except Exception as e:
        logger.error("Error in list_containers: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500

@app.route('/api/storage/containers/<container_name>/blobs', methods=['GET'])
def list_blobs(container_name):
    try:
        logger.debug("Received request to list all blobs in container: %s", container_name)
        container_client = blob_service_client.get_container_client(container_name)
        blobs = container_client.list_blobs()
        blob_names = [blob.name for blob in blobs]
        logger.debug("Blobs listed successfully in container: %s", container_name)
        
        return jsonify({"blobs": blob_names}), 200
    except Exception as e:
        logger.error("Error in list_blobs: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500

@app.route('/api/storage/containers', methods=['POST'])
def create_container():
    try:
        data = request.get_json()
        container_name = data.get("container_name")
        if not container_name:
            return jsonify({"error": "Container name is required."}), 400

        logger.debug("Received request to create container: %s", container_name)
        container_client = blob_service_client.get_container_client(container_name)
        if container_client.exists():
            return jsonify({"message": "Container already exists."}), 200

        container_client.create_container()
        logger.debug("Container created successfully: %s", container_name)
        return jsonify({"message": "Container created successfully", "container_name": container_name}), 201
    except Exception as e:
        logger.error("Error in create_container: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500

@app.route('/api/storage/containers/<container_name>', methods=['DELETE'])
def delete_container(container_name):
    try:
        logger.debug("Received request to delete container: %s", container_name)
        container_client = blob_service_client.get_container_client(container_name)
        if not container_client.exists():
            return jsonify({"error": "Container does not exist."}), 404

        container_client.delete_container()
        logger.debug("Container deleted successfully: %s", container_name)
        return jsonify({"message": "Container deleted successfully", "container_name": container_name}), 200
    except Exception as e:
        logger.error("Error in delete_container: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500

if __name__ == '__main__':
    if os.getenv("FLASK_ENV") == "development":
        logger.info("Waiting for debugger attach on port 5678...")
        debugpy.listen(('0.0.0.0', 5678))
        debugpy.wait_for_client()  # Wait for the debugger to attach    
    app.run(host='0.0.0.0', port=5000)
