FROM python:3.13-slim

# Install necessary packages in a single RUN command
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        gcc build-essential curl nginx supervisor && \
    curl -sL https://aka.ms/InstallAzureCLIDeb | bash && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# Set environment variables
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

# Create directories and set permissions
RUN adduser -u 5678 --disabled-password --gecos "" appuser && \
    mkdir -p /home/appuser/.azure /app /app/logs /var/lib/nginx/body /var/log/nginx /run && \
    chown -R appuser:appuser /home/appuser /app /app/logs /var/lib/nginx /var/log/nginx /run && \
    chmod -R 755 /var/lib/nginx /var/log/nginx /run /app/logs

# Set working directory and copy dependencies
WORKDIR /app
COPY --chown=appuser:appuser requirements.txt /app/
RUN python -m pip install --no-cache-dir -r requirements.txt

# Copy application code and configurations
COPY --chown=appuser:appuser . /app
COPY nginx.conf /etc/nginx/nginx.conf
COPY supervisord.conf /etc/supervisor/conf.d/supervisord.conf

EXPOSE 80

RUN setcap 'cap_net_bind_service=+ep' /usr/sbin/nginx

USER appuser

# Use supervisord to run both NGINX and Gunicorn
CMD ["supervisord", "-c", "/etc/supervisor/conf.d/supervisord.conf"]
