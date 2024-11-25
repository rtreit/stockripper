# First Stage: Use Playwright image for all browsing dependencies
FROM mcr.microsoft.com/playwright:v1.48.0-noble AS playwright-stage

# Second Stage: Use a Python image
FROM python:3.11-slim-bullseye

EXPOSE 5000
EXPOSE 5678

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV PLAYWRIGHT_BROWSERS_PATH=0

# Copy Playwright dependencies from the first stage
COPY --from=playwright-stage /ms-playwright /ms-playwright
COPY --from=playwright-stage /opt /opt

# Install required packages and dependencies
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        gcc \
        build-essential \
        curl \
        wget \
        apt-transport-https \
        software-properties-common \
        gnupg \
        vim \
        ca-certificates

# Install the Azure CLI
RUN curl -sL https://aka.ms/InstallAzureCLIDeb | bash

# Import the Microsoft repository GPG keys and install PowerShell
RUN wget -q https://packages.microsoft.com/config/debian/11/packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y --no-install-recommends powershell && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Install Python dependencies early to enable caching
COPY requirements.txt .
RUN pip install -r requirements.txt

# Install Playwright as root before switching to appuser
RUN playwright install-deps && \
    playwright install

# Create a non-root user with a default shell
RUN adduser -u 5678 --disabled-password --gecos "" --shell /bin/bash appuser && \
    mkdir -p /home/appuser/.azure && \
    chown -R appuser:appuser /home/appuser/.azure

# Set the working directory and copy application code
WORKDIR /app
COPY . /app

# Adjust permissions for the application directory
RUN chown -R appuser:appuser /app

# Switch to the non-root user
USER appuser

# Ensure the default shell is bash
SHELL ["/bin/bash", "-c"]

# Command to run the application
CMD ["python", "app.py"]
