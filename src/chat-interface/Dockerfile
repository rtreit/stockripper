FROM python:3.13-slim

# Install necessary packages
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        gcc build-essential curl nginx supervisor && \
    curl -sL https://aka.ms/InstallAzureCLIDeb | bash && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*


# Environment variables
ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

# Create appuser and directories
RUN adduser -u 5678 --disabled-password --gecos "" appuser && \
    mkdir -p /home/appuser/.azure /app && \
    chown -R appuser:appuser /home/appuser /app

# Update permissions for the NGINX directories
RUN mkdir -p /var/lib/nginx/body && \
    chown -R appuser:appuser /var/lib/nginx && \
    chmod -R 755 /var/lib/nginx

RUN mkdir -p /var/log/nginx && \
chown -R appuser:appuser /var/log/nginx && \
chmod -R 755 /var/log/nginx

RUN mkdir -p /run && \
    chown -R appuser:appuser /run && \
    chmod -R 755 /run

RUN mkdir -p /app/logs && \
    chown -R appuser:appuser /app/logs && \
    chmod -R 755 /app/logs


# Install Python dependencies
COPY requirements.txt /app/
WORKDIR /app
RUN python -m pip install -r requirements.txt

# Copy application code
COPY . /app

# Copy NGINX configuration
COPY nginx.conf /etc/nginx/nginx.conf

COPY supervisord.conf /etc/supervisor/conf.d/supervisord.conf

# Permissions and set user
RUN chown -R appuser /app
USER appuser

# Expose necessary ports
EXPOSE 80

# Command to run both NGINX and Gunicorn
#CMD ["sh", "-c", "nginx && gunicorn -b 127.0.0.1:5000 --workers 3 app:app"]
CMD ["supervisord", "-c", "/etc/supervisor/conf.d/supervisord.conf"]
