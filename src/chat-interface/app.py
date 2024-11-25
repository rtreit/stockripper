import logging
from flask import Flask, render_template, request, jsonify
import requests
import os

app = Flask(__name__)

# Set up logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# URL of the agent service
AGENT_SERVICE_URL = os.getenv(
    "AGENT_SERVICE_URL", "http://stockripper-agent-app:5000/agents"
)


@app.route("/health", methods=["GET"])
def health_check():
    return "OK", 200


@app.route("/")
def index():
    return render_template("index.html")


@app.route("/chat", methods=["POST"])
def chat():
    data = request.get_json()
    agent_name = data.get("agent_name", "default")
    user_input = data.get("input")
    session_id = data.get("session_id", "default-session")

    # Construct the URL for the agent service
    agent_url = f"{AGENT_SERVICE_URL}/{agent_name}"

    # Log the exact URL and payload
    logger.info(f"Sending request to agent service at: {agent_url}")
    logger.info(f"Payload: input='{user_input}', session_id='{session_id}'")

    try:
        # Forward the message to the agent service
        response = requests.post(
            agent_url,
            json={"input": user_input, "session_id": session_id},
        )
        return jsonify(response.json())
    except Exception as e:
        logger.error("Error communicating with the agent service: %s", str(e))
        return jsonify({"error": str(e)}), 500


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=80, debug=False)
