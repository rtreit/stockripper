from flask import Flask, request, jsonify
import os
import logging
import random
import json
from dotenv import load_dotenv
from datetime import datetime, timezone
import requests
from collections import defaultdict
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
from langchain.memory import VectorStoreRetrieverMemory, ConversationBufferWindowMemory
from langchain.vectorstores.base import VectorStoreRetriever
from langchain_core.documents import Document

from azure.storage.blob import BlobServiceClient
from azure.search.documents import SearchClient

from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    SimpleField,
    SearchFieldDataType,
)
from azure.core.credentials import AzureKeyCredential, TokenCredential
from azure.identity import DefaultAzureCredential, CredentialUnavailableError
import os
from datetime import datetime, timedelta
from azure.core.credentials import TokenCredential, AccessToken
from azure.storage.blob import BlobServiceClient
from azure.identity import (
    DefaultAzureCredential,
    CredentialUnavailableError,
    ManagedIdentityCredential,
)
import logging
from langchain_community.tools.bing_search import BingSearchResults
from langchain_community.utilities import BingSearchAPIWrapper
from langchain_community.tools import DuckDuckGoSearchResults
from langchain_community.utilities import DuckDuckGoSearchAPIWrapper
from langchain_community.agent_toolkits import PlayWrightBrowserToolkit
from langchain_community.tools.playwright.utils import (
    create_sync_playwright_browser,
)
from langchain.schema import AIMessage, HumanMessage

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
        print("BlobServiceClient successfully created with environment token.")
    else:
        logger.warning(
            "AZURE_STORAGE_TOKEN not set. Falling back to DefaultAzureCredential."
        )
        try:
            credential = DefaultAzureCredential()
            blob_service_client = BlobServiceClient(
                account_url=AZURE_STORAGE_ACCOUNT_URL, credential=credential
            )
            print("BlobServiceClient successfully created with DefaultAzureCredential.")
        except CredentialUnavailableError as e:
            logger.error("DefaultAzureCredential unavailable: %s", str(e))
            logger.warning("Falling back to UAMI credential.")
            if UAMI_CLIENT_ID:
                uami_credential = ManagedIdentityCredential(client_id=UAMI_CLIENT_ID)
                blob_service_client = BlobServiceClient(
                    account_url=AZURE_STORAGE_ACCOUNT_URL, credential=uami_credential
                )
                print("BlobServiceClient successfully created with UAMI credential.")
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

global_memory_window = ConversationBufferWindowMemory(
    memory_key="chat_history",
    input_key="user_input",
    output_key="output",
    k=5,  # Keep the last 5 exchanges
    return_messages=True,
)

session_memory_windows = defaultdict(
    lambda: ConversationBufferWindowMemory(
        memory_key="chat_history",
        input_key="user_input",
        output_key="output",
        k=5,  # Keep the last 5 exchanges
        return_messages=True,
    )
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
        print("Received request to send e-mail.")
        if not recipient:
            raise ValueError("Recipient address is required.")
        if not subject:
            raise ValueError("Subject is required.")
        if not body:
            raise ValueError("Body is required.")

        print(f"Sending e-mail to: {recipient}")
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
            print("Email sent successfully to %s", recipient)
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
        print(
            "Saving file to blob storage. Container: %s, Blob: %s",
            container_name,
            blob_name,
        )
        container_client = blob_service_client.get_container_client(container_name)

        if not container_client.exists():
            container_client.create_container()
            print("Container created: %s", container_name)

        blob_client = container_client.get_blob_client(blob_name)
        blob_client.upload_blob(file_content, overwrite=True)
        print("File uploaded successfully: %s", blob_name)

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
        print("Listing blobs in container: %s", container_name)
        container_client = blob_service_client.get_container_client(container_name)

        blob_list = container_client.list_blobs()
        blobs = [blob.name for blob in blob_list]
        print("Blobs listed successfully: %s", blobs)

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
        print("Listing all containers in blob storage")
        containers = blob_service_client.list_containers()
        container_names = [container.name for container in containers]
        print("Containers listed successfully: %s", container_names)

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
        print("Retrieving Wikipedia article for topic: %s", topic)

        # Initialize Wikipedia retriever
        retriever = WikipediaRetriever()
        docs = retriever.invoke(topic)
        # print(docs[0].page_content[:400])
        doc_result = "\n\n".join(doc.page_content for doc in docs)

        print("Wikipedia article retrieved successfully for topic: %s", topic)

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
        print("Retrieving Bing search results for query: %s", query)

        # Perform the search and parse the response
        response = bing_tool.invoke(query)
        response = json.loads(response.replace("'", '"'))  # Ensure JSON formatting

        # Process and format results
        results = "\n\n".join(item["snippet"] for item in response if "snippet" in item)

        print("Bing search results retrieved successfully for query: %s", query)

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


def summarize_conversation(llm, session_history, user_prompt, agent_response):
    """
    Generate a concise session summary while keeping verbatim details separate.
    """
    summarization_prompt = f"""
    You are maintaining a memory of this conversation. Include:
    - Verbatim details from recent interactions.
    - A concise summary of key points and decisions made so far.

    Recent History:
    {session_history}

    Latest Interaction:
    User: {user_prompt}
    Agent: {agent_response}

    Generate a concise summary of the key details discussed.
    """

    try:
        # Use invoke() and extract content
        summary_message = llm.invoke(summarization_prompt)
        summary = summary_message.content
    except Exception as e:
        logger.error(f"Error generating summary: {str(e)}", exc_info=True)
        summary = "Summary could not be generated due to an error."

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


def prune_old_conversations(vector_store, session_id, keep_latest=1):
    filter_expression = f"session_id eq '{session_id}'"
    results = list(
        memory_search_client.search(search_text="", filter=filter_expression)
    )
    for result in results:
        result["metadata"] = json.loads(result["metadata"])
    results.sort(
        key=lambda x: datetime.strptime(
            x["metadata"]["start_timestamp"], "%Y-%m-%dT%H:%M:%SZ"
        ),
        reverse=True,
    )

    old_docs_to_delete = [{"id": doc["id"]} for doc in results[keep_latest:]]
    if old_docs_to_delete:
        memory_search_client.delete_documents(documents=old_docs_to_delete)
        print(
            f"Deleted {len(old_docs_to_delete)} old documents for session {session_id}."
        )
    else:
        print("No old documents to delete.")


def format_messages(messages):
    formatted_messages = []
    for message in messages:
        if isinstance(message, (HumanMessage, AIMessage)):
            formatted_messages.append({"type": type(message).__name__, "content": message.content})
        else:
            formatted_messages.append(str(message))
    return formatted_messages



def call_agent_with_context(agent_executor, llm, user_prompt, session_id):
    try:
        # Get or create a memory window for this session_id
        session_memory_window = session_memory_windows[session_id]

        # Retrieve verbatim session history from memory window
        recent_history_text = format_messages(session_memory_window.chat_memory.messages)

        # Retrieve relevant context using RAG
        rag_results = rag_vector_store.similarity_search(user_prompt, k=3)
        rag_content = "\n".join([doc.page_content for doc in rag_results])

        # Retrieve long-term memory (summaries) from the vector store
        filter_expression = f"session_id eq '{session_id}'"
        session_history_docs = list(
            memory_search_client.search(search_text="", filter=filter_expression)
        )
        session_history_content = (
            "\n".join(doc["content"] for doc in session_history_docs)
            if session_history_docs
            else ""
        )

        print(f"\nInvoking agent with context for session {session_id}...")
        print(f"\Current User Request: {user_prompt}")
        print(f"\nKnowledge Context: {rag_content}")
        print(f"\nRecent Verbatim History: {recent_history_text}")
        print(f"\nPast Conversation Summary: {session_history_content}")

        # Invoke the agent with the contextual information
        result = agent_executor.invoke(
            {
                "user_input": user_prompt,
                "knowledge_context": rag_content,
                "recent_history": recent_history_text,
                "chat_history": session_history_content,
            }
        )

        agent_output = result.get("output", "No response provided.")
        if hasattr(agent_output, "content"):
            agent_output = agent_output.content

        # Save verbatim interaction to session-specific memory
        session_memory_window.save_context(
            {"user_input": user_prompt}, {"output": agent_output}
        )

        # Generate a concise summary and store in long-term memory
        summarized_history = summarize_conversation(
            llm, session_history_content, user_prompt, agent_output
        )
        store_summary(memory_vector_store, session_id, summarized_history)
        prune_old_conversations(memory_vector_store, session_id)

        return result
    except Exception as e:
        logger.error(f"Error in call_agent_with_context: {str(e)}", exc_info=True)
        return {"error": str(e)}


# add /health route for health checks
@app.route("/health", methods=["GET"])
def health_check():
    return "OK", 200


@app.route("/agents/balanced", methods=["POST"])
def invoke_balanced():
    model = "gpt-4o"
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

    # Add browsing support
    sync_browser = create_sync_playwright_browser()
    sync_browser.new_context(ignore_https_errors=True)
    toolkit = PlayWrightBrowserToolkit.from_browser(sync_browser=sync_browser)
    browser_tools = toolkit.get_tools()
    tools.extend(browser_tools)

    llm_with_tools = llm.bind_tools(tools)
    system_message = SystemMessagePromptTemplate(
        prompt=PromptTemplate(
            input_variables=[],
            input_types={},
            partial_variables={},
            template="""
            You are Stockripper, a financial genius. You are sarcastic and grumpy, but helpful. Your job is to assist users by answering their questions and performing tasks using the available tools.

            Focus on the user's current request. Use prior context only if it helps answer the question. Do not repeat previous actions unless the user asks you to do so explicitly.
            """,
        )
    )

    human_message = HumanMessagePromptTemplate(
        prompt=PromptTemplate(
            input_variables=[
                "user_input",
                "knowledge_context",
                "recent_history",
                "chat_history",
            ],
            template="""
            Current User Request: {user_input}
            ** IMPORTANT: the above request is what I'd like you to respond to. What follows is additional context to help you understand my needs better, but you can ignore it if it's not relevant to my main request. **
            Knowledge Context: {knowledge_context}
            Recent Verbatim History: {recent_history}
            Past Conversation Summary: {chat_history}
            """,
        )
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
        agent=agent, tools=tools, verbose=True, memory=global_memory_window
    )

    try:
        data = request.get_json()
        user_prompt = data.get("input")
        session_id = data.get("session_id")

        if not user_prompt:
            return jsonify({"error": "Missing 'input' parameter in request body"}), 400
        if not session_id:
            return (
                jsonify({"error": "Missing 'session_id' parameter in request body"}), 400
            )

        # Get or create a memory window for this session_id
        memory_window = session_memory_windows[session_id]

        # Create agent with session-specific memory
        agent_executor = AgentExecutor(
            agent=agent, tools=tools, verbose=True, memory=memory_window
        )

        # Invoke the agent with memory and RAG context
        result = call_agent_with_context(agent_executor, llm, user_prompt, session_id)

        # Ensure the result is JSON-serializable
        if isinstance(result, dict):
            serializable_result = {k: (format_messages(v) if isinstance(v, list) else str(v)) for k, v in result.items()}
        else:
            serializable_result = {"output": str(result)}

        return jsonify({"result": serializable_result})
    except Exception as e:
        logger.error("Error invoking agent: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500


if __name__ == "__main__":
    # Run the Flask app on port 5000
    app.run(host="0.0.0.0", port=5000, debug=True)
