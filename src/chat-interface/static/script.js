// Generate a unique UUID for session ID
function generateSessionId() {
    return 'xxxx-xxxx-xxxx-xxxx'.replace(/[x]/g, function () {
        const random = (Math.random() * 16) | 0;
        return random.toString(16);
    });
}

// Retrieve or set a unique session ID in sessionStorage
function getSessionId() {
    let sessionId = sessionStorage.getItem("session_id");
    if (!sessionId) {
        sessionId = generateSessionId();
        sessionStorage.setItem("session_id", sessionId);
    }
    return sessionId;
}

// Function to send a message to the backend
function sendMessage() {
    const userInput = document.getElementById("user-input").value;
    if (!userInput) return;

    // Display user message
    const chatBox = document.getElementById("chat-box");
    const userMessage = document.createElement("p");
    userMessage.textContent = `You: ${userInput}`;
    chatBox.appendChild(userMessage);

    // Clear input field
    document.getElementById("user-input").value = "";

    // Send message to backend
    fetch("/chat", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            agent_name: "mailworker",
            input: userInput,
            session_id: getSessionId() // Use the unique session ID for each browser session
        })
    })
    .then(response => response.json())
    .then(data => {
        console.log("Agent Response:", data); // Log the entire response object for inspection

        // Extract the 'output' from the 'result' object
        const agentMessage = data.result && data.result.output ? data.result.output : JSON.stringify(data);

        // Render Markdown as HTML
        const botMessage = document.createElement("p");
        botMessage.innerHTML = marked.parse(agentMessage); // Use marked to convert Markdown to HTML
        chatBox.appendChild(botMessage);
        chatBox.scrollTop = chatBox.scrollHeight; // Scroll to bottom
    })
    .catch(error => {
        console.error("Error:", error);
        alert("An error occurred while sending the message. Please check the console for details.");
    });
}
