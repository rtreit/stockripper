import asyncio
import os
from semantic_kernel import Kernel
from semantic_kernel.connectors.ai.open_ai import OpenAIChatCompletion

# Initialize the kernel
kernel = Kernel()

# Add the OpenAI GPT-4 service
OPENAI_API_KEY = os.environ['OPENAI_API_KEY']
gpt4_service = OpenAIChatCompletion(OPENAI_API_KEY, "gpt-4")
kernel.add_service("chat", gpt4_service)

# Function to ask a question using the service
async def ask_question(kernel, service_id, prompt, max_tokens=150):
    # Define the request settings
    settings = kernel.get_prompt_execution_settings_from_service_id(service_id)
    settings.max_tokens = max_tokens

    # Add the user prompt to the chat history
    chat_history = kernel.get_chat_history(service_id)
    chat_history.add_user_message(prompt)

    # Generate the response
    response = await kernel.invoke_chat(service_id=service_id, chat_history=chat_history)
    return response.text.strip()
# Use the function to ask a question
async def main():
    # Define your question
    question = "What are some good stocks to invest in for short-term growth?"

    # Ask the question using the function
    response = await ask_question(kernel, "chat-gpt-4", question)

    # Print the response
    print(response)

# Run the main function
asyncio.run(main())
