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
    userMessage.classList.add("fade-in");
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
            session_id: getSessionId()
        })
    })
    .then(response => response.json())
    .then(data => {
        console.log("Agent Response:", data);
    
        // Safely access `output`, or fallback to `data.result` or `data` as needed
        const agentMessage = data.result?.output || data.result || JSON.stringify(data);
    
        // Create and format the bot message for display
        const botMessage = document.createElement("div"); // Use div for markdown container
        botMessage.classList.add("markdown-content"); // Add markdown content class
        botMessage.innerHTML = marked.parse(agentMessage);
        botMessage.classList.add("fade-in");

        // Append message to the chat box and auto-scroll
        chatBox.appendChild(botMessage);

        // Add a separator for visual spacing between messages
        const separator = document.createElement("p");
        separator.innerHTML = "&nbsp;"; // Adding non-breaking space for spacing
        chatBox.appendChild(separator);

        // Auto-scroll to the bottom
        chatBox.scrollTop = chatBox.scrollHeight;
    })
    .catch(error => {
        console.error("Error:", error);
        alert("An error occurred while sending the message. Please check the console for details.");
    });
}

// Event listener to support Enter key for sending messages
document.getElementById("user-input").addEventListener("keypress", function (event) {
    if (event.key === "Enter") {
        event.preventDefault(); // Prevents form submission if the input is in a form
        sendMessage();
    }
});

document.addEventListener('DOMContentLoaded', function () {
    // Select the chat box element
    const chatBox = document.querySelector('.chat-box');

    // Function to scroll to the bottom
    function scrollToBottom() {
        chatBox.scrollTop = chatBox.scrollHeight;
    }

    // Call this function initially to scroll to the bottom when the page loads
    scrollToBottom();
});
