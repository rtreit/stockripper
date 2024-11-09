from azure.core.credentials import AzureKeyCredential
from azure.search.documents import SearchClient, IndexDocumentsBatch
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    SimpleField,
    SearchIndex,
    SearchableField,
)
from azure.search.documents.models import IndexAction
from flask import Flask, request, jsonify
from azure.identity import DefaultAzureCredential, CredentialUnavailableError
from azure.storage.blob import BlobServiceClient
import os
import logging
import requests
import random
import tiktoken
from dotenv import load_dotenv
from langchain.chat_models import ChatOpenAI
from langchain.tools import tool
from langchain.agents import AgentExecutor, create_tool_calling_agent
from langchain.prompts import (
    ChatPromptTemplate,
    SystemMessagePromptTemplate,
    HumanMessagePromptTemplate,
    MessagesPlaceholder,
    PromptTemplate,
)
from langchain.memory import ConversationSummaryBufferMemory
from langchain.schema import AIMessage, HumanMessage, SystemMessage

# Load environment variables from a .env file.
load_dotenv()

logging.basicConfig(level=logging.WARNING)
logger = logging.getLogger(__name__)

# Adjust token limits based on the model's context window
MAX_TOKENS_TOTAL = 8192  # For GPT-4 standard context window
SUMMARY_MAX_TOKENS = 500  # Token limit for the summary
RECENT_CONTEXT_MAX_TOKENS = 1000  # Token limit for recent context
RESPONSE_TOKENS_MAX = 1000  # Tokens allocated for the model's response

CLIENT_ID = os.getenv("CLIENT_ID")
CLIENT_SECRET = os.getenv("CLIENT_SECRET")
TENANT_ID = os.getenv("TENANT_ID")
REFRESH_TOKEN = os.getenv("REFRESH_TOKEN")
REDIRECT_URI = "http://localhost:5000/getAToken"
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
AZURE_STORAGE_ACCOUNT_URL = os.getenv("AZURE_STORAGE_ACCOUNT_URL")
COGNITIVE_SEARCH_URL = os.getenv("COGNITIVE_SEARCH_URL")
COGNITIVE_SEARCH_ADMIN_KEY = os.getenv("COGNITIVE_SEARCH_ADMIN_KEY")
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

index_client = SearchIndexClient(
    endpoint=COGNITIVE_SEARCH_URL,
    credential=AzureKeyCredential(COGNITIVE_SEARCH_ADMIN_KEY),
)


def get_search_client_for_agent(agent_name: str) -> SearchClient:
    index_name = f"agent-memory-{agent_name}"
    return SearchClient(
        endpoint=COGNITIVE_SEARCH_URL,
        index_name=index_name,
        credential=AzureKeyCredential(COGNITIVE_SEARCH_ADMIN_KEY),
    )


def count_tokens(text, model_name="gpt-4"):
    encoding = tiktoken.encoding_for_model(model_name)
    return len(encoding.encode(text))


def create_index_if_not_exists(agent_name: str):
    try:
        index_name = f"agent-memory-{agent_name}"
        try:
            index_client.get_index(index_name)
            logger.debug(f"Index '{index_name}' already exists.")
        except:
            logger.info(f"Index '{index_name}' does not exist. Creating it now.")
            index = SearchIndex(
                name=index_name,
                fields=[
                    SimpleField(name="id", type="Edm.String", key=True),
                    SearchableField(name="conversation", type="Edm.String"),
                ],
            )
            index_client.create_index(index)
            logger.info(f"Index '{index_name}' created successfully.")

    except Exception as e:
        logger.error("Error creating index: %s", str(e))
        raise


def save_conversation_to_search(agent_name: str, session_id: str, conversation: str):
    try:
        # Sanitize the conversation before saving
        sanitized_conversation = sanitize_conversation(conversation)
        search_client = get_search_client_for_agent(agent_name)

        # Create an IndexDocumentsBatch and add actions
        batch = IndexDocumentsBatch()
        batch.add_upload_actions(
            [{"id": session_id, "conversation": sanitized_conversation}]
        )

        # Use the batch in the index_documents method
        result = search_client.index_documents(batch=batch)
        logger.debug("Conversation saved to Azure Cognitive Search: %s", result)
    except Exception as e:
        logger.error("Error saving conversation to Cognitive Search: %s", str(e))
        raise


def load_conversation_from_search(agent_name: str, session_id: str) -> str:
    try:
        search_client = get_search_client_for_agent(agent_name)
        result = search_client.get_document(key=session_id)
        conversation = result.get("conversation", "")
        return sanitize_conversation(conversation)
    except Exception as e:
        logger.warning(
            "Conversation not found for session_id %s: %s", session_id, str(e)
        )
        return ""


def sanitize_conversation(conversation):
    # Implement sanitization logic
    # For example, remove sensitive information or escape problematic characters
    sanitized_conversation = conversation.replace("\\", "\\\\").replace(
        "\n", "\\n"
    ).replace("\r", "\\r")
    return sanitized_conversation


def refresh_auth_token():
    """
    Refresh the Microsoft Graph API authentication token using the refresh token.
    Returns the new access token if successful.
    """
    headers = {"Content-Type": "application/x-www-form-urlencoded"}
    data = {
        "client_id": CLIENT_ID,
        "client_secret": CLIENT_SECRET,
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
def send_email(
    to_recipients: list, subject: str, body: str, cc_recipients: list = None
) -> dict:
    """
    Send an email using the Microsoft Graph API.

    Args:
        to_recipients (list): A list of email addresses for the primary recipients.
        subject (str): The subject of the email.
        body (str): The content of the email body.
        cc_recipients (list, optional): A list of email addresses for CC recipients.

    Returns:
        dict: A dictionary containing the result message and the subject of the email.
    """
    try:
        logger.info("Received request to send e-mail.")

        if (
            not to_recipients
            or not isinstance(to_recipients, list)
            or len(to_recipients) == 0
        ):
            raise ValueError(
                "At least one recipient address is required in 'to_recipients'."
            )
        if not subject:
            raise ValueError("Subject is required.")
        if not body:
            raise ValueError("Body is required.")

        logger.debug(
            f"Sending e-mail to: {', '.join(to_recipients)} with CC: {', '.join(cc_recipients or [])}"
        )
        access_token = refresh_auth_token()

        # Format the recipients for the "to" field
        to_addresses = [
            {"emailAddress": {"address": email}} for email in to_recipients
        ]

        # Format the recipients for the "cc" field, if provided
        cc_addresses = (
            [{"emailAddress": {"address": email}} for email in cc_recipients]
            if cc_recipients
            else []
        )

        email_payload = {
            "message": {
                "subject": subject,
                "body": {
                    "contentType": "Text",
                    "content": body,
                },
                "toRecipients": to_addresses,
                "ccRecipients": cc_addresses,
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
            logger.info("Email sent successfully to %s", ", ".join(to_recipients))
            return {"message": "E-mail sent", "subject": subject}
        else:
            logger.error(
                f"Failed to send email. Status Code: {response.status_code}"
            )
            logger.error(response.text)
            raise RuntimeError(f"Failed to send email: {response.text}")
    except Exception as e:
        logger.error("Error in send_email: %s", str(e), exc_info=True)
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


# Example tools
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
    """Generate a random number between min and max."""
    return random.randint(min, max)


# Initialize the language model
llm = ChatOpenAI(model_name="gpt-4", openai_api_key=OPENAI_API_KEY)

# Define the tools
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

llm_with_tools  = llm.bind_tools(tools)
# Initialize memory
memory = ConversationSummaryBufferMemory(
    llm=llm,
    max_token_limit=MAX_TOKENS_TOTAL - RESPONSE_TOKENS_MAX,
    memory_key="chat_history",
    input_key="input",
)


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

agent = create_tool_calling_agent(llm_with_tools, prompt)
agent_executor = AgentExecutor(agent=agent, tools=tools, verbose=True)

# Flask endpoint to expose agent
@app.route("/agents/<agent_name>", methods=["POST"])
def invoke_agent(agent_name):
    try:
        data = request.get_json()
        user_prompt = data.get("input")
        session_id = data.get("session_id")

        if not user_prompt:
            return (
                jsonify({"error": "Missing 'input' parameter in request body"}),
                400,
            )
        if not session_id:
            return (
                jsonify({"error": "Missing 'session_id' parameter in request body"}),
                400,
            )

        create_index_if_not_exists(agent_name)

        # Load conversation history from search and set it in memory
        conversation_history = load_conversation_from_search(agent_name, session_id)
        if conversation_history:
            # Convert the conversation string back into messages
            messages = []
            for line in conversation_history.strip().split("\n"):
                if line.startswith("User:"):
                    messages.append(HumanMessage(content=line[5:].strip()))
                elif line.startswith("Assistant:"):
                    messages.append(AIMessage(content=line[10:].strip()))
            memory.chat_memory.messages = messages

        # Run the agent
        result = agent_executor.run(user_prompt)

        # Append the new exchange to the conversation
        new_conversation = conversation_history + f"\nUser: {user_prompt}\nAssistant: {result}"
        save_conversation_to_search(agent_name, session_id, new_conversation)

        return jsonify({"result": result})

    except Exception as e:
        logger.error("Error invoking agent: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500


if __name__ == "__main__":
    # Run the Flask app on port 5000
    app.run(host="0.0.0.0", port=5000, debug=True)
