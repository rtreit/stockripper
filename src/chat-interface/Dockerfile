# Use an official Python runtime as a base image
FROM python:3.9-slim

# Set the working directory
WORKDIR /app

# Copy the current directory contents into the container at /app
COPY . /app

# Install any dependencies
RUN pip install -r requirements.txt

# Expose port 5001 for the chat interface
EXPOSE 5004

# Start the Flask app
CMD ["python", "app.py"]
