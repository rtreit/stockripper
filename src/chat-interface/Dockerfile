FROM python:3.13-slim

EXPOSE 5000
EXPOSE 5678

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

RUN apt-get update && \
    apt-get install -y --no-install-recommends gcc build-essential curl && \
    curl -sL https://aka.ms/InstallAzureCLIDeb | bash && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

RUN adduser -u 5678 --disabled-password --gecos "" appuser && \
    mkdir -p /home/appuser/.azure && \
    chown -R appuser:appuser /home/appuser/.azure

COPY requirements.txt .
RUN python -m pip install -r requirements.txt

WORKDIR /app
COPY . /app

RUN chown -R appuser /app
USER appuser

CMD ["python", "app.py"]
