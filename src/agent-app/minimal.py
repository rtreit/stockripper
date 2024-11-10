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

app = Flask(__name__)


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
docs = vector_store.similarity_search(
    query="What is an aggressive investment strategy?",
    k=3,
    search_type="similarity",
)
print(docs[0].page_content)


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


tools = [
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
