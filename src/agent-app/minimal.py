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

# Load environment variables from a .env file.
load_dotenv()

logging.basicConfig(level=logging.WARNING)
logger = logging.getLogger(__name__)

OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
COGNITIVE_SEARCH_URL = os.getenv("COGNITIVE_SEARCH_URL")
COGNITIVE_SEARCH_ADMIN_KEY = os.getenv("COGNITIVE_SEARCH_API_KEY")


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
agent_executor = AgentExecutor(agent=agent, tools=tools, verbose=True)


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
            )

        result = agent_executor.invoke({"input": user_prompt})
        return jsonify({"result": result})
    except Exception as e:
        logger.error("Error invoking agent: %s", str(e), exc_info=True)
        return jsonify({"error": str(e)}), 500


if __name__ == "__main__":
    # Run the Flask app on port 5000
    app.run(host="0.0.0.0", port=5000, debug=True)
