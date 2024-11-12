FROM python:3.13-slim

EXPOSE 5000
EXPOSE 5678

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

# Update and install required packages, Azure CLI, PowerShell, and Vim
RUN apt-get update && \
    apt-get install -y --no-install-recommends gcc build-essential curl vim && \
    curl -sL https://aka.ms/InstallAzureCLIDeb | bash && \
    apt-get install -y --no-install-recommends gnupg apt-transport-https && \
    curl https://packages.microsoft.com/keys/microsoft.asc | apt-key add - && \
    echo "deb [arch=amd64] https://packages.microsoft.com/repos/microsoft-debian-buster-prod buster main" > /etc/apt/sources.list.d/microsoft.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends powershell && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Create a non-root user and set permissions
RUN adduser -u 5678 --disabled-password --gecos "" appuser && \
    mkdir -p /home/appuser/.azure && \
    chown -R appuser:appuser /home/appuser/.azure

# Install Python dependencies
COPY requirements.txt .
RUN python -m pip install -r requirements.txt

# Set working directory and copy application code
WORKDIR /app
COPY . /app

# Set permissions for the application directory
RUN chown -R appuser /app
USER appuser

# Command to run the application
CMD ["python", "app.py"]
