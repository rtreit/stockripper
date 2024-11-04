import requests
from flask import Flask, jsonify
import logging
import time
import debugpy, os

app = Flask(__name__)
fsharpUri = "http://stockripper-fsharp-app.stockripper.internal:5001/health"
rustUri = "http://stockripper-rust-app.stockripper.internal:5002/health"


if os.environ.get("FLASK_ENV") == "development":
    debugpy.listen(("0.0.0.0", 5678))
    print("Waiting for debugger to attach...")
    debugpy.wait_for_client()

# Configure logging
logging.basicConfig(level=logging.INFO)

@app.route('/health', methods=['GET'])
def health_check():
    logging.info('Received a health check request')
    response = requests.get(fsharpUri)
    health_check = response.json().get("status")
    logging.info(f'Response from FSharp service: {health_check}')
    rust_response = requests.get(rustUri)
    logging.info(f'Response from Rust service: {rust_response.text}')
    logging.info(f'Response from Rust service: {rust_response.json().get("status")}')    
    return jsonify(status=f"I'm alive - response from calling FSharp service: {health_check}"), 200

if __name__ == '__main__':
    logging.info('Agent app started')
    logging.info('Hello from the agent app running Python')
    time.sleep(5)
    try:
        rust_response = requests.get(rustUri)
        logging.info(f'Initial Response from Rust service: {rust_response.text}')
        logging.info(f'Response from Rust service: {rust_response.json().get("status")}')
    except Exception as e:
        logging.error(f'Error when calling Rust service: {e}')
    fsharp_response = requests.get(fsharpUri)
    logging.info(f'Response from FSharp service: {fsharp_response.json().get("status")}')    
    app.run(host='0.0.0.0', port=5000)