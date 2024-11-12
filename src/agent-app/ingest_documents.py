import os
import logging
from dotenv import load_dotenv
from langchain_community.retrievers import WikipediaRetriever
from langchain_community.document_loaders import TextLoader
from langchain_text_splitters import CharacterTextSplitter
from langchain.embeddings.openai import OpenAIEmbeddings
from langchain_community.vectorstores.azuresearch import AzureSearch

load_dotenv()

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
COGNITIVE_SEARCH_URL = os.getenv("COGNITIVE_SEARCH_URL")
COGNITIVE_SEARCH_ADMIN_KEY = os.getenv("COGNITIVE_SEARCH_ADMIN_KEY")

vector_store_address = COGNITIVE_SEARCH_URL
vector_store_password = COGNITIVE_SEARCH_ADMIN_KEY
index_name = "stockripper-documents"
embeddings_model = "text-embedding-ada-002"

embeddings = OpenAIEmbeddings(openai_api_key=OPENAI_API_KEY, model=embeddings_model)
vector_store = AzureSearch(
    azure_search_endpoint=vector_store_address,
    azure_search_key=vector_store_password,
    index_name=index_name,
    embedding_function=embeddings.embed_query,
)

documents_folder = "documents"
loaded_docs_log = "loaded_documents.log"

os.makedirs(documents_folder, exist_ok=True)
if not os.path.exists(loaded_docs_log):
    open(loaded_docs_log, 'w').close()

with open(loaded_docs_log, "r") as log_file:
    loaded_documents = set(log_file.read().splitlines())

def fetch_and_save_wikipedia_articles(topics):
    retriever = WikipediaRetriever()
    for topic in topics:
        articles = retriever.invoke(topic)
        for article in articles:
            title = article.metadata["title"].replace("/", "_")  # Sanitize title for filename
            file_path = os.path.join(documents_folder, f"{title}.txt")
            
            if title not in loaded_documents:
                with open(file_path, "w", encoding="utf-8") as f:
                    f.write(article.page_content)
                logger.info(f"Saved Wikipedia article '{title}' to {file_path}")
            else:
                logger.info(f"Article '{title}' already exists in documents folder, skipping.")

def ingest_documents():
    text_splitter = CharacterTextSplitter(chunk_size=1000, chunk_overlap=0)

    for file in os.listdir(documents_folder):
        if file.endswith(".txt") and file not in loaded_documents:
            file_path = os.path.join(documents_folder, file)
            
            loader = TextLoader(file_path, encoding="utf-8")
            documents = loader.load()
            docs = text_splitter.split_documents(documents)
            
            vector_store.add_documents(documents=docs)
            
            with open(loaded_docs_log, "a") as log_file:
                log_file.write(f"{file}\n")
            logger.info(f"Ingested and logged document '{file}'")

def main():
    topics = ["Aggressive Investment Strategy", "Passive Investing", "Value Investing", "Investment Strategy", "Investing", "Securities Analysis"]

    fetch_and_save_wikipedia_articles(topics)

    ingest_documents()

if __name__ == "__main__":
    main()
