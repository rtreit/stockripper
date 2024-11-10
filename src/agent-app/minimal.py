from flask import Flask, request, jsonify
import os
import logging
from dotenv import load_dotenv
from langchain_openai import ChatOpenAI
from langchain_core.prompts import ChatPromptTemplate
from langchain_core.tools import tool
import random
from langchain.agents import AgentExecutor, create_tool_calling_agent
from langchain.prompts import (
    ChatPromptTemplate,
    SystemMessagePromptTemplate,
    HumanMessagePromptTemplate,
    MessagesPlaceholder,
    PromptTemplate,
)
from azure.search.documents import SearchClient
from azure.search.documents.indexes import SearchIndexClient
from azure.core.credentials import AzureKeyCredential

from langchain.embeddings.openai import OpenAIEmbeddings
from langchain_community.vectorstores.azuresearch import AzureSearch
from langchain_openai import AzureOpenAIEmbeddings, OpenAIEmbeddings
from langchain_community.retrievers import WikipediaRetriever

retriever = WikipediaRetriever()
docs = retriever.invoke("TOKYO GHOUL")
print(docs[0].page_content[:400])

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

app = Flask(__name__)


vector_store_address: str = COGNITIVE_SEARCH_URL
vector_store_password: str = COGNITIVE_SEARCH_ADMIN_KEY

embeddings_model: str = "text-embedding-ada-002"

openai_api_version: str = "2023-05-15"
# Option 1: Use OpenAIEmbeddings with OpenAI account
embeddings: OpenAIEmbeddings = OpenAIEmbeddings(
    openai_api_key=OPENAI_API_KEY, openai_api_version=openai_api_version, model=embeddings_model
)

index_name: str = "stockripper-documents"
vector_store: AzureSearch = AzureSearch(
    azure_search_endpoint=vector_store_address,
    azure_search_key=vector_store_password,
    index_name=index_name,
    embedding_function=embeddings.embed_query,
)

## test vector search by adding some documents
#from langchain_community.document_loaders import TextLoader
#from langchain_text_splitters import CharacterTextSplitter
#
## load all documents in the documents folder
#for file in os.listdir("documents"):
#    if file.endswith(".txt"):
#        loader = TextLoader(f"documents/{file}", encoding="utf-8")
#        documents = loader.load()
#        text_splitter = CharacterTextSplitter(chunk_size=1000, chunk_overlap=0)
#        docs = text_splitter.split_documents(documents)
#        vector_store.add_documents(documents=docs)


# Perform a similarity search
#docs = vector_store.similarity_search(
#    query="What is an aggressive investment strategy?",
#    k=3,
#    search_type="similarity",
#)
#print(docs[0].page_content)
#

from langchain_community.vectorstores.azuresearch import AzureSearch
from langchain.memory import VectorStoreRetrieverMemory
from langchain.vectorstores.base import VectorStoreRetriever

# Define the vector store retriever for memory
memory_vector_store = AzureSearch(
    azure_search_endpoint=vector_store_address,
    azure_search_key=vector_store_password,
    index_name="agent-memory",
    embedding_function=embeddings.embed_query,
)

# Create a retriever from the vector store
memory_retriever = VectorStoreRetriever(vectorstore=memory_vector_store)

# Define the memory object using the retriever
memory = VectorStoreRetrieverMemory(
    retriever=memory_retriever,
    memory_key="history",  # This key will be used when referencing memory in prompts
    input_key="input",     # The input key to correlate user prompts
    return_docs=False      # To avoid returning entire documents, useful for simplicity
)

memory.save_context({"input": "My favorite food is pizza"}, {"output": "that's good to know"})
memory.save_context({"input": "My favorite sport is soccer"}, {"output": "..."})
memory.save_context({"input": "I don't the Celtics"}, {"output": "ok"}) #

llm = ChatOpenAI(model="gpt-4o-mini", openai_api_key=OPENAI_API_KEY)

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
            return jsonify({"error": "Missing 'session_id' parameter in request body"}), 400

        # Retrieve past relevant interactions for this prompt
        last_user_prompt = memory.retriever.invoke(user_prompt)
        
        # Invoke the agent with memory context
        result = agent_executor.invoke({"input": f"User Prompt: {user_prompt}\nHistory: \n{last_user_prompt}"})

        # Save the interaction in memory
        memory.save_context({"input": user_prompt, "session_id": session_id}, {"output": result})

        return jsonify({"result": result})
    except Exception as e:
        logger.error("Error invoking agent: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500



if __name__ == "__main__":
    # Run the Flask app on port 5000
    app.run(host="0.0.0.0", port=5000, debug=True)
