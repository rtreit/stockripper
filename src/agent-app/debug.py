import requests
from flask import Flask, jsonify
import logging

app = Flask(__name__)
fsharpUri = "http://stockripper-fsharp-app.stockripper.internal:5001/health"

# Configure logging
logging.basicConfig(level=logging.INFO)

@app.route('/health', methods=['GET'])
def health_check():
    logging.info('Received a health check request')
    response = requests.get(fsharpUri)
    health_check = response.json().get("status")
    logging.info(f'Response from FSharp service: {health_check}')
    return jsonify(status=f"I'm alive - response from calling FSharp service: {health_check}"), 200

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000)
