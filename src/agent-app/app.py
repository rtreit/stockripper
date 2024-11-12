from flask import Flask, request, jsonify
import os
import logging
import random
import json
from dotenv import load_dotenv
from datetime import datetime, timezone
import requests

from langchain_openai import ChatOpenAI, OpenAIEmbeddings
from langchain_core.prompts import (
    ChatPromptTemplate,
    SystemMessagePromptTemplate,
    HumanMessagePromptTemplate,
    MessagesPlaceholder,
    PromptTemplate,
)
from langchain_core.tools import tool
from langchain.agents import AgentExecutor, create_tool_calling_agent
from langchain.prompts import ChatPromptTemplate
from langchain.embeddings.openai import OpenAIEmbeddings
from langchain_community.vectorstores.azuresearch import AzureSearch
from langchain_community.retrievers import WikipediaRetriever, AzureAISearchRetriever
from langchain.memory import (
    VectorStoreRetrieverMemory,
    ConversationBufferMemory,
    ConversationBufferWindowMemory,
    ConversationSummaryMemory,
)
from langchain.vectorstores.base import VectorStoreRetriever
from langchain_core.documents import Document

from azure.storage.blob import BlobServiceClient
from azure.search.documents import SearchClient
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    SimpleField,
    SearchIndex,
    SearchableField,
    SearchFieldDataType,
)
from azure.core.credentials import AzureKeyCredential
from azure.identity import DefaultAzureCredential, CredentialUnavailableError
from azure.search.documents.models import QueryType


# Load environment variables from a .env file.
load_dotenv()

logging.basicConfig(level=logging.WARNING)
logger = logging.getLogger(__name__)

OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
COGNITIVE_SEARCH_URL = os.getenv("COGNITIVE_SEARCH_URL")
COGNITIVE_SEARCH_ADMIN_KEY = os.getenv("COGNITIVE_SEARCH_ADMIN_KEY")
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

rag_index_name = "stockripper-documents"
memory_index_name = "agent-memory"
credential = DefaultAzureCredential()

# add clients
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

memory_search_client = SearchClient(
        endpoint=COGNITIVE_SEARCH_URL,
        index_name=memory_index_name,
        credential=AzureKeyCredential(COGNITIVE_SEARCH_ADMIN_KEY),
    )

rag_search_client = SearchClient(
    endpoint=COGNITIVE_SEARCH_URL,
    index_name=rag_index_name,
    credential=AzureKeyCredential(COGNITIVE_SEARCH_ADMIN_KEY),
)

app = Flask(__name__)

embeddings_model: str = "text-embedding-ada-002"
openai_api_version: str = "2023-05-15"
embeddings: OpenAIEmbeddings = OpenAIEmbeddings(
    openai_api_key=OPENAI_API_KEY, openai_api_version=openai_api_version, model=embeddings_model
)

# RAG 
rag_vector_store: AzureSearch = AzureSearch(
    azure_search_endpoint=COGNITIVE_SEARCH_URL,
    azure_search_key=COGNITIVE_SEARCH_ADMIN_KEY,
    index_name=rag_index_name,
    embedding_function=embeddings.embed_query,
)

# Memory 
memory_vector_store: AzureSearch = AzureSearch(
    azure_search_endpoint=COGNITIVE_SEARCH_URL,
    azure_search_key=COGNITIVE_SEARCH_ADMIN_KEY,
    index_name=memory_index_name,
    embedding_function=embeddings.embed_query,
)

def add_to_memory(vector_store: AzureSearch, conversation: Document, session_id: str):
    id = vector_store.add_documents(documents=[conversation], index_name=memory_index_name)[0]
    update_document = {
        "id": id,
        "session_id": session_id
    }
    memory_search_client.merge_documents([update_document])

# we'll need memory we can pass to the model
memory_retriever = VectorStoreRetriever(
    vectorstore=memory_vector_store
)

memory = VectorStoreRetrieverMemory(
    retriever=memory_retriever,
    memory_key="history",  
    input_key="input",     
    return_docs=False
)

# add tools
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


# main agent functions
def summarize_conversation(agent_executor, session_history, user_prompt, result):
    # Define the enhanced summarization prompt
    summarization_prompt = f"""
    You are maintaining a summary of the ongoing conversation to serve as a working memory. 
    This summary should include all key facts, action results, entities, and values generated during the conversation, in a way that will allow you to recall specific information in the future if asked. 
    Examples:
    
    - Any websites, links, or URLs visited or discussed
    - Unique values or numbers you have provided, like random numbers or IDs
    - Key decisions, instructions, or choices made by the user or agent
    - Names, dates, and any other specific entities referenced
    - Similar kinds of information
    
    Format the summary to be as concise as possible while retaining these essential details. 
    Do not include filler or repetitive information. 
    Your goal is to create a memory of important facts, allowing you to answer questions like "What website did you previously browse?" or "What was the last number you provided?" without needing to review the entire conversation history.

    Session History:
    {session_history}

    Latest Interaction:
    User: {user_prompt}
    Agent: {result['output']}
    
    Provide a concise yet detailed summary of the conversation so far, focusing on capturing key information in a way that you can reference it easily.
    """

    # Invoke the agent to generate the summary
    summary_result = agent_executor.invoke({"input": summarization_prompt})
    summary = summary_result.get("output", "Summary could not be generated.")
    return summary



def store_summary(vector_store, session_id, summary):
    current_time_utc = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    summary_doc = Document(
        page_content=summary,
        metadata={
            "start_timestamp": current_time_utc,
            "session_id": session_id,
            "summary_version": "latest"
        }
    )
    add_to_memory(vector_store, summary_doc, session_id)
    print("Summary stored successfully.")

def prune_old_conversations(vector_store, session_id, keep_latest=1):
    filter_expression = f"session_id eq '{session_id}'"
    results = list(memory_search_client.search(search_text="", filter=filter_expression))
    for result in results:
        result["metadata"] = json.loads(result["metadata"])  
    results.sort(
        key=lambda x: datetime.strptime(x["metadata"]["start_timestamp"], "%Y-%m-%dT%H:%M:%SZ"),
        reverse=True
    )

    old_docs_to_delete = [{"id": doc["id"]} for doc in results[keep_latest:]]
    if old_docs_to_delete:
        memory_search_client.delete_documents(documents=old_docs_to_delete)
        print(f"Deleted {len(old_docs_to_delete)} old documents for session {session_id}.")
    else:
        print("No old documents to delete.")


def call_agent(agent_executor, user_prompt, session_id):
    filter_expression = f"session_id eq '{session_id}'"
    session_history = list(memory_search_client.search(search_text="", filter=filter_expression))
    print(f"Found {len(session_history)} documents for session {session_id}.")
    if len(session_history) == 0:
        session_history_content = ""
    else:
        session_history_content = "" 
        for doc in session_history:
            session_history_content += doc["content"] + "\n"
    result = agent_executor.invoke({"input": f"User Prompt: {user_prompt}\nHistory: \n{session_history_content}"})
    summary = summarize_conversation(agent_executor, session_history_content, user_prompt, result)
    store_summary(memory_vector_store, session_id, summary)
    prune_old_conversations(memory_vector_store, session_id)
    return result

llm = ChatOpenAI(model="gpt-4o-mini", openai_api_key=OPENAI_API_KEY)
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
        template="""
        You are StockRipper, an expert stock trading and investing agent. 
        Your job is to help maximize the user's investment returns by providing stock market insights, analysis, and recommendations.
        You can also execute trades, manage portfolios, and provide real-time updates on stock prices and market trends.
        You have access to a set of tools that allow you to perform various tasks.",
        """
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

prompt = ChatPromptTemplate.from_messages(
    [
        system_message,
        MessagesPlaceholder(variable_name="chat_history", optional=True),
        human_message,
        MessagesPlaceholder(variable_name="agent_scratchpad"),
    ]
)

agent = create_tool_calling_agent(llm, tools, prompt)
agent_executor = AgentExecutor(agent=agent, tools=tools, verbose=True, memory=memory)


@app.route("/agents/mailworker", methods=["POST"])
def invoke_agent():
    try:
        data = request.get_json()
        user_prompt = data.get("input")
        session_id = data.get("session_id")

        if not user_prompt:
            return jsonify({"error": "Missing 'input' parameter in request body"}), 400
        if not session_id:
            return (
                jsonify({"error": "Missing 'session_id' parameter in request body"}),
                400,
            )

        # Invoke the agent with memory context
        result = call_agent(agent_executor, user_prompt, session_id)
        return jsonify({"result": result})
    except Exception as e:
        logger.error("Error invoking agent: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500

if __name__ == "__main__":
    # Run the Flask app on port 5000
    app.run(host="0.0.0.0", port=5000, debug=True)
