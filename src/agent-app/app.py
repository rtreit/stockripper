from flask import Flask, request, jsonify
from azure.identity import DefaultAzureCredential, CredentialUnavailableError
from azure.storage.blob import BlobServiceClient, BlobClient, ContainerClient
import os
import logging
import requests
from dotenv import load_dotenv
from langchain_openai import ChatOpenAI
from langchain_core.messages import HumanMessage, SystemMessage
from langchain_core.prompts import ChatPromptTemplate
from langchain_core.tools import tool
from langchain_core.output_parsers import StrOutputParser
from typing import Optional
from pydantic import BaseModel, Field
import random
from langchain.agents import AgentExecutor, create_tool_calling_agent
from langchain.prompts import (
    ChatPromptTemplate,
    SystemMessagePromptTemplate,
    HumanMessagePromptTemplate,
    MessagesPlaceholder,
    PromptTemplate,
)

# Load environment variables from a .env file.
load_dotenv()

logging.basicConfig(level=logging.WARNING)
logger = logging.getLogger(__name__)

CLIENT_ID = os.getenv("CLIENT_ID")
CLIENT_SECRET = os.getenv("CLIENT_SECRET")
TENANT_ID = os.getenv("TENANT_ID")
REFRESH_TOKEN = os.getenv("REFRESH_TOKEN")
REDIRECT_URI = "http://localhost:5000/getAToken"
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
AZURE_STORAGE_ACCOUNT_URL = os.getenv("AZURE_STORAGE_ACCOUNT_URL")
AZURE_STORAGE_CONTAINER_NAME = os.getenv(
    "AZURE_STORAGE_CONTAINER_NAME", "default-container"
)

if not AZURE_STORAGE_ACCOUNT_URL:
    raise ValueError(
        "AZURE_STORAGE_ACCOUNT_URL environment variable is not set. Please set it to the storage account URL."
    )
logger.debug("Using DefaultAzureCredential for authentication")

credential = DefaultAzureCredential()

app = Flask(__name__)

try:
    blob_service_client = BlobServiceClient(
        account_url=AZURE_STORAGE_ACCOUNT_URL, credential=credential
    )
    logger.debug("BlobServiceClient successfully created.")
except CredentialUnavailableError as e:
    logger.error("Credential unavailable: %s", str(e))
    raise
except Exception as e:
    logger.error("Failed to create BlobServiceClient: %s", str(e))
    raise


def refresh_auth_token():
    """
    Refresh the Microsoft Graph API authentication token using the refresh token.
    Returns the new access token if successful.
    """
    headers = {"Content-Type": "application/x-www-form-urlencoded"}
    data = {
        "client_id": CLIENT_ID,
        "client_secret": CLIENT_SECRET,  # Add the client secret here
        "scope": "offline_access openid profile Mail.Send",
        "refresh_token": REFRESH_TOKEN,
        "redirect_uri": REDIRECT_URI,
        "grant_type": "refresh_token",
    }
    url = "https://login.microsoftonline.com/common/oauth2/v2.0/token"
    response = requests.post(url, data=data, headers=headers)
    if response.status_code == 200:
        tokens = response.json()
        return tokens.get("access_token")
    else:
        raise Exception(f"Failed to refresh token: {response.text}")


@tool("send_email")
def send_email(recipient: str, subject: str, body: str) -> dict:
    """
    Send an email using the Microsoft Graph API.

    Args:
        recipient (str): The email address of the recipient.
        subject (str): The subject of the email.
        body (str): The content of the email body.

    Returns:
        dict: A dictionary containing the result message and the subject of the email.
    """
    try:
        logger.info("Received request to send e-mail.")
        if not recipient:
            raise ValueError("Recipient address is required.")
        if not subject:
            raise ValueError("Subject is required.")
        if not body:
            raise ValueError("Body is required.")

        logger.debug(f"Sending e-mail to: {recipient}")
        access_token = refresh_auth_token()
        email_payload = {
            "message": {
                "subject": subject,
                "body": {
                    "contentType": "Text",
                    "content": body,
                },
                "toRecipients": [
                    {
                        "emailAddress": {
                            "address": recipient,
                        }
                    }
                ],
            }
        }

        headers = {
            "Authorization": f"Bearer {access_token}",
            "Content-Type": "application/json",
        }
        response = requests.post(
            "https://graph.microsoft.com/v1.0/me/sendMail",
            headers=headers,
            json=email_payload,
        )
        if response.status_code == 202:
            logger.info("Email sent successfully to %s", recipient)
            return {"message": "E-mail sent", "subject": subject}
        else:
            logger.error(f"Failed to send email. Status Code: {response.status_code}")
            logger.error(response.text)
            raise RuntimeError(f"Failed to send email: {response.text}")
    except Exception as e:
        logger.error("Error in send_mail_internal: %s", str(e), exc_info=True)
        raise


@tool("save_to_blob")
def save_to_blob(container_name: str, blob_name: str, file_content: bytes) -> dict:
    """
    Save a file to Azure Blob Storage.

    Args:
        container_name (str): The name of the Azure Blob container.
        blob_name (str): The name of the blob (file) to be created.
        file_content (bytes): The content of the file to be uploaded.

    Returns:
        dict: A dictionary containing a success message and the blob name.
    """
    try:
        logger.debug(
            "Saving file to blob storage. Container: %s, Blob: %s",
            container_name,
            blob_name,
        )
        container_client = blob_service_client.get_container_client(container_name)

        if not container_client.exists():
            container_client.create_container()
            logger.debug("Container created: %s", container_name)

        blob_client = container_client.get_blob_client(blob_name)
        blob_client.upload_blob(file_content, overwrite=True)
        logger.debug("File uploaded successfully: %s", blob_name)

        return {"message": "File uploaded successfully", "blob_name": blob_name}
    except Exception as e:
        logger.error("Error in save_to_blob: %s", str(e), exc_info=True)
        return {"error": str(e)}


@tool("list_blobs")
def list_blobs(container_name: str) -> dict:
    """
    List all blobs in a specified Azure Blob container.

    Args:
        container_name (str): The name of the Azure Blob container.

    Returns:
        dict: A dictionary containing the result message and the list of blob names.
    """
    try:
        logger.debug("Listing blobs in container: %s", container_name)
        container_client = blob_service_client.get_container_client(container_name)

        blob_list = container_client.list_blobs()
        blobs = [blob.name for blob in blob_list]
        logger.debug("Blobs listed successfully: %s", blobs)

        return {"message": "Blobs listed successfully", "blobs": blobs}
    except Exception as e:
        logger.error("Error in list_blobs: %s", str(e), exc_info=True)
        return {"error": str(e)}


@tool("list_containers")
def list_containers() -> dict:
    """
    List all containers in Azure Blob Storage.

    Returns:
        dict: A dictionary containing the result message and the list of container names.
    """
    try:
        logger.debug("Listing all containers in blob storage")
        containers = blob_service_client.list_containers()
        container_names = [container.name for container in containers]
        logger.debug("Containers listed successfully: %s", container_names)

        return {
            "message": "Containers listed successfully",
            "containers": container_names,
        }
    except Exception as e:
        logger.error("Error in list_containers: %s", str(e), exc_info=True)
        return {"error": str(e)}


# example from https://python.langchain.com/docs/concepts/tools/
@tool("multiply")
def multiply(a: int, b: int) -> int:
    """Multiply two numbers."""
    return a * b


@tool("divide")
def divide(a: int, b: int) -> float:
    """Divide two numbers."""
    return a / b


@tool("add")
def add(a: int, b: int) -> int:
    """Add two numbers."""
    return a + b


@tool("subtract")
def subtract(a: int, b: int) -> int:
    """Subtract two numbers."""
    return a - b


@tool("generate_random_number")
def generate_random_number(min: int, max: int) -> int:
    """Generate a random number between min and max.
    Args:
        min (int): The minimum value of the random number.
        max (int): The maximum value of the random number.
    """
    return random.randint(min, max)


llm = ChatOpenAI(model="gpt-4o", openai_api_key=OPENAI_API_KEY)
tools = [
    send_email,
    save_to_blob,
    list_blobs,
    list_containers,
    multiply,
    divide,
    add,
    subtract,
    generate_random_number,
]
llm_with_tools = llm.bind_tools(tools)

system_message = SystemMessagePromptTemplate(
    prompt=PromptTemplate(
        input_variables=[],
        input_types={},
        partial_variables={},
        template="You are a helpful assistant that summarizes data and provides it to the user. You can also send emails, save files to Azure Blob Storage, and perform basic arithmetic operations.",
    ),
    additional_kwargs={},
)

human_message = HumanMessagePromptTemplate(
    prompt=PromptTemplate(
        input_variables=["input"],
        input_types={},
        partial_variables={},
        template="{input}",
    ),
    additional_kwargs={},
)

# Create the ChatPromptTemplate with placeholders for chat history and agent scratchpad
prompt = ChatPromptTemplate.from_messages(
    [
        system_message,
        MessagesPlaceholder(variable_name="chat_history", optional=True),
        human_message,
        MessagesPlaceholder(variable_name="agent_scratchpad"),
    ]
)

agent = create_tool_calling_agent(llm, tools, prompt)
agent_executor = AgentExecutor(agent=agent, tools=tools, verbose=True)


# Flask endpoint to expose agent
@app.route("/agents/mailworker", methods=["POST"])
def invoke_agent():
    try:
        # Extract the JSON payload from the request
        data = request.get_json()
        user_prompt = data.get("input")
        if not user_prompt:
            return jsonify({"error": "Missing 'input' parameter in request body"}), 400

        # Invoke the agent with the given user prompt
        result = agent_executor.invoke({"input": user_prompt})

        # Return the agent's response as JSON
        return jsonify({"result": result})
    except Exception as e:
        logger.error("Error invoking agent: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500


if __name__ == "__main__":
    # Run the Flask app on port 5000
    app.run(host="0.0.0.0", port=5000, debug=True)
