from flask import Flask, request, jsonify, g
import os
import asyncio
import logging
import random
import json
from dotenv import load_dotenv
from datetime import datetime, timezone
import requests

from langchain_openai import ChatOpenAI, OpenAIEmbeddings, AzureChatOpenAI
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
from azure.core.credentials import AzureKeyCredential, TokenCredential
from azure.identity import DefaultAzureCredential, CredentialUnavailableError
import os
from datetime import datetime, timedelta
from azure.core.credentials import TokenCredential, AccessToken
from azure.storage.blob import BlobServiceClient
from azure.identity import DefaultAzureCredential, CredentialUnavailableError, ManagedIdentityCredential
import logging
from langchain_community.tools.bing_search import BingSearchResults
from langchain_community.utilities import BingSearchAPIWrapper
from langchain_community.tools import DuckDuckGoSearchResults
from langchain_community.utilities import DuckDuckGoSearchAPIWrapper
from langchain_community.agent_toolkits import PlayWrightBrowserToolkit
from langchain_community.tools.playwright.utils import (
    create_async_playwright_browser,
    create_sync_playwright_browser,
)
from playwright.async_api import async_playwright
import atexit
import nest_asyncio
nest_asyncio.apply()


# Load environment variables from a .env file.
load_dotenv()

logging.basicConfig(level=logging.WARNING)
logger = logging.getLogger(__name__)

OPENAI_ENDPOINT = os.getenv("OPENAI_ENDPOINT")
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
COGNITIVE_SEARCH_URL = os.getenv("COGNITIVE_SEARCH_URL")
COGNITIVE_SEARCH_ADMIN_KEY = os.getenv("COGNITIVE_SEARCH_ADMIN_KEY")
CLIENT_ID = os.getenv("CLIENT_ID")
CLIENT_SECRET = os.getenv("CLIENT_SECRET")
TENANT_ID = os.getenv("TENANT_ID")
REFRESH_TOKEN = os.getenv("REFRESH_TOKEN")
REDIRECT_URI = "http://localhost:5000/getAToken"
AZURE_STORAGE_TOKEN = os.getenv("AZURE_STORAGE_TOKEN")
AZURE_STORAGE_ACCOUNT_URL = os.getenv("AZURE_STORAGE_ACCOUNT_URL")
AZURE_STORAGE_CONTAINER_NAME = os.getenv(
    "AZURE_STORAGE_CONTAINER_NAME", "default-container"
)
if not AZURE_STORAGE_ACCOUNT_URL:
    raise ValueError(
        "AZURE_STORAGE_ACCOUNT_URL environment variable is not set. Please set it to the storage account URL."
    )
UAMI_CLIENT_ID = os.getenv("UAMI_CLIENT_ID")

rag_index_name = "stockripper-documents"
memory_index_name = "agent-memory"

# add clients
logger = logging.getLogger(__name__)


# bit of a hack for running containers locally but giving permissions to storage without using secrets
class EnvironmentTokenCredential(TokenCredential):
    def __init__(self, token):
        self.token = token
        self.expires_on = (datetime.now(timezone.utc) + timedelta(hours=24)).timestamp()

    def get_token(self, *scopes, **kwargs):
        return AccessToken(self.token, self.expires_on)


try:
    if AZURE_STORAGE_TOKEN:
        token_credential = EnvironmentTokenCredential(token=AZURE_STORAGE_TOKEN)
        blob_service_client = BlobServiceClient(
            account_url=AZURE_STORAGE_ACCOUNT_URL, credential=token_credential
        )
        logger.debug("BlobServiceClient successfully created with environment token.")
    else:
        logger.warning(
            "AZURE_STORAGE_TOKEN not set. Falling back to DefaultAzureCredential."
        )
        try:
            credential = DefaultAzureCredential()
            blob_service_client = BlobServiceClient(
                account_url=AZURE_STORAGE_ACCOUNT_URL, credential=credential
            )
            logger.debug(
                "BlobServiceClient successfully created with DefaultAzureCredential."
            )
        except CredentialUnavailableError as e:
            logger.error("DefaultAzureCredential unavailable: %s", str(e))
            logger.warning("Falling back to UAMI credential.")
            if UAMI_CLIENT_ID:
                uami_credential = ManagedIdentityCredential(client_id=UAMI_CLIENT_ID)
                blob_service_client = BlobServiceClient(
                    account_url=AZURE_STORAGE_ACCOUNT_URL, credential=uami_credential
                )
                logger.debug("BlobServiceClient successfully created with UAMI credential.")
            else:
                logger.error("UAMI client ID not provided.")
                raise

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
async_playwright_instance = None
async_browser = None

async def init_playwright():
    global async_playwright_instance, async_browser
    async_playwright_instance = await async_playwright().start()
    async_browser = await async_playwright_instance.chromium.launch()

# Initialize Playwright and browser before starting the app
asyncio.run(init_playwright())

@atexit.register
def cleanup():
    global async_browser
    if async_browser:
        asyncio.run(async_browser.close())

embeddings_model: str = "text-embedding-ada-002"
embeddings = OpenAIEmbeddings(
    model=embeddings_model,
    chunk_size=1000,
)

index_name = "agent-memory"

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

seed_conversation = Document(
    page_content="""
    User: My computer keeps crashing when I try to open the application.
    Support: I'm here to help! Could you let me know which operating system you're using?
    User: I'm on Windows 10.
    Support: Thank you. Let’s try reinstalling the application. Please go to the Control Panel, uninstall the app, and then download the latest version from our website.
    User: Okay, I’ll give that a try.
    """,
    metadata={
        "session_id": "7890",
        "title": "Technical Issue - Application Crashes",
        "timestamp": "2023-11-10T15:00:00Z",
        "agent_name": "TechSupportBot",
    },
)


seed_doc = Document(
    page_content=seed_conversation.page_content, metadata=seed_conversation.metadata
)

# this will force creation of the index if it doesn't exist
seed_doc_id = memory_vector_store.add_documents(
    documents=[seed_doc], index_name=index_name
)[0]

# delete the seed document
memory_search_client.delete_documents(documents=[{"id": seed_doc_id}])

# update the index to include the session_id field
index = index_client.get_index(index_name)

field_names = [field.name for field in index.fields]
if "session_id" not in field_names:
    new_field = SimpleField(
        name="session_id",
        type=SearchFieldDataType.String,
        searchable=True,
        filterable=True,
        facetable=False,
        sortable=False,
    )
    index.fields.append(new_field)
    index_client.create_or_update_index(index)
    print("Field 'session_id' added successfully.")
else:
    print("Field 'session_id' already exists in the index. No changes made.")


def add_to_memory(vector_store: AzureSearch, conversation: Document, session_id: str):
    id = vector_store.add_documents(
        documents=[conversation], index_name=memory_index_name
    )[0]
    update_document = {"id": id, "session_id": session_id}
    memory_search_client.merge_documents([update_document])


# we'll need memory we can pass to the model
memory_retriever = VectorStoreRetriever(vectorstore=memory_vector_store)

memory = VectorStoreRetrieverMemory(
    retriever=memory_retriever,
    memory_key="history",
    input_key="input",
    return_docs=False,
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


@tool("retrieve_wikipedia_article")
def retrieve_wikipedia_article(topic: str) -> dict:
    """
    Retrieve a Wikipedia article based on a specified topic.

    Args:
        topic (str): The topic to search for in Wikipedia.

    Returns:
        dict: A dictionary containing the result message and the article content.
    """
    try:
        logger.debug("Retrieving Wikipedia article for topic: %s", topic)

        # Initialize Wikipedia retriever
        retriever = WikipediaRetriever()
        docs = retriever.invoke(topic)
        # print(docs[0].page_content[:400])
        doc_result = "\n\n".join(doc.page_content for doc in docs)

        logger.debug("Wikipedia article retrieved successfully for topic: %s", topic)

        return {
            "message": "Wikipedia article retrieved successfully",
            "article": doc_result,
        }
    except Exception as e:
        logger.error("Error in retrieve_wikipedia_article: %s", str(e), exc_info=True)
        return {"error": str(e)}


api_wrapper = BingSearchAPIWrapper()
bing_tool = BingSearchResults(api_wrapper=api_wrapper)


@tool("retrieve_bing_search_results")
def retrieve_bing_search_results(query: str) -> dict:
    """
    Retrieve Bing search results based on a specified query.

    Args:
        query (str): The search query to retrieve results for.

    Returns:
        dict: A dictionary containing the result message and the search results.
    """
    try:
        logger.debug("Retrieving Bing search results for query: %s", query)

        # Perform the search and parse the response
        response = bing_tool.invoke(query)
        response = json.loads(response.replace("'", '"'))  # Ensure JSON formatting

        # Process and format results
        results = "\n\n".join(item["snippet"] for item in response if "snippet" in item)

        logger.debug("Bing search results retrieved successfully for query: %s", query)

        return {
            "message": "Bing search results retrieved successfully",
            "results": results,
        }
    except Exception as e:
        logger.error("Error in retrieve_bing_search_results: %s", str(e), exc_info=True)
        return {"error": str(e)}


@tool("retrieve_duckduckgo_search_results")
def retrieve_duckduckgo_search_results(
    query: str, region: str = "en-us", time_range: str = "d", max_results: int = 10
) -> dict:
    """
    Retrieve DuckDuckGo search results based on a specified query.

    Args:
        query (str): The search query to retrieve results for.
        region (str): The region to search in (default is "en-us").
        time_range (str): The time range to search within (default is "d" for past day). Can be "d" (day), "w" (week), "m" (month), or "y" (year).
        max_results (int): The maximum number of search results to retrieve (default is 10).

    Returns:
        dict: A dictionary containing the result message and the search results including snippets and URLs.
    """
    try:
        wrapper = DuckDuckGoSearchAPIWrapper(
            region=region, time=time_range, max_results=max_results
        )
        search = DuckDuckGoSearchResults(api_wrapper=wrapper)

        response = search.invoke(query)
        if not response:
            return {"error": "Received empty response from DuckDuckGo"}
        return {
            "message": "DuckDuckGo search results retrieved successfully",
            "results": response,
        }
    except Exception as e:
        logger.error(
            "Error in retrieve_duckduckgo_search_results: %s", str(e), exc_info=True
        )
        return {"error": str(e)}


# main agent functions
async def summarize_conversation(agent_executor, session_history, user_prompt, result):
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
    summary_result = await agent_executor.ainvoke({"input": summarization_prompt})
    summary = summary_result.get("output", "Summary could not be generated.")
    return summary


def store_summary(vector_store, session_id, summary):
    current_time_utc = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    summary_doc = Document(
        page_content=summary,
        metadata={
            "start_timestamp": current_time_utc,
            "session_id": session_id,
            "summary_version": "latest",
        },
    )
    add_to_memory(vector_store, summary_doc, session_id)
    print("Summary stored successfully.")


async def prune_old_conversations(vector_store, session_id, keep_latest=1):
    filter_expression = f"session_id eq '{session_id}'"
    results = await asyncio.to_thread(
        lambda: list(memory_search_client.search(search_text="", filter=filter_expression))
    )
    print(f"Results: {results}")  # Inspect the results
    for x in results:
        print(f"Document: {x}")
        print(f"Metadata Type: {type(x['metadata'])}")
        print(f"Metadata Content: {x['metadata']}")
    results.sort(
        key=lambda x: datetime.strptime(
            x["metadata"]["start_timestamp"], "%Y-%m-%dT%H:%M:%SZ"
        ),
        reverse=True,
    )

    old_docs_to_delete = [{"id": doc["id"]} for doc in results[keep_latest:]]
    if old_docs_to_delete:
        await asyncio.to_thread(
            await memory_search_client.delete_documents, documents=old_docs_to_delete
        )


async def call_agent(agent_executor, user_prompt, session_id):
    # Fetch session history
    filter_expression = f"session_id eq '{session_id}'"
    session_history = await asyncio.to_thread(
        lambda: list(memory_search_client.search(search_text="", filter=filter_expression))
    )
    print(f"Found {len(session_history)} documents for session {session_id}.")

    # Build session history content
    session_history_content = ""
    if len(session_history) > 0:
        for doc in session_history:
            session_history_content += doc["content"] + "\n"

    # Prepare input
    input_data = {
        "input": f"User Prompt: {user_prompt}\nHistory: \n{session_history_content}"
    }

    # Invoke the agent asynchronously with `ainvoke`
    result = await agent_executor.ainvoke(input_data)

    # Summarize conversation
    summary = await summarize_conversation(agent_executor, session_history_content, user_prompt, result)

    # Store summary in memory
    await asyncio.to_thread(store_summary, memory_vector_store, session_id, summary)

    # Prune old conversations
    await prune_old_conversations(memory_vector_store, session_id)

    return result




@app.route("/agents/mailworker", methods=["POST"])
async def invoke_mailworker():
    print("Got request to invoke mailworker agent.")
    model = "gpt-4o-mini"
    llm = ChatOpenAI(
        model=model,
        api_key=OPENAI_API_KEY,
    )

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
        retrieve_wikipedia_article,
        retrieve_bing_search_results,
    ]
    async_browser = create_async_playwright_browser()
    print(f"Browser created: {async_browser}")
    if not async_browser:
        return jsonify({"error": "Playwright browser not initialized"}), 500
    print("Creating browser context...")
    try:
        print("Entering try block...")
        # Await new_context and use the result as a context manager
        await async_browser.new_context(ignore_https_errors=True)
        print("Browser context created.")
        toolkit = PlayWrightBrowserToolkit.from_browser(async_browser=async_browser)
        browser_tools = toolkit.get_tools()
        tools.extend(browser_tools)

        llm_with_tools = llm.bind_tools(tools)
        system_message = SystemMessagePromptTemplate(
            prompt=PromptTemplate(
                input_variables=[],
                input_types={},
                partial_variables={},
                template="""You are an agent named Stockripper...""",
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
        agent_executor = AgentExecutor(
            agent=agent, tools=tools, verbose=True, memory=memory
        )

        data = request.get_json()
        user_prompt = data.get("input")
        session_id = data.get("session_id")

        if not user_prompt:
            return jsonify({"error": "Missing 'input' parameter in request body"}), 400
        if not session_id:
            return jsonify({"error": "Missing 'session_id' parameter in request body"}), 400

        result = await call_agent(agent_executor, user_prompt, session_id)
        return jsonify({"result": result})
    except Exception as e:
        logger.error("Error invoking agent: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500



if __name__ == "__main__":
    print("Starting app...")
    app.run(host="0.0.0.0", port=5000, debug=True)

