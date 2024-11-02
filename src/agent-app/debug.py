# health_check.py
import requests
from flask import Flask, jsonify

app = Flask(__name__)

@app.route('/health', methods=['GET'])
def health_check():
    response = requests.get("http://stockripper-fsharp-app.stockripper.internal:5001/health")
    health_data = response.json()
    health_check = health_data.get("status")
    print(response)
    print(health_data)
    return jsonify(status=f"I'm alive - response from calling FSharp service: {health_check}"), 200

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000)  # Expose on all interfaces on port 5000
