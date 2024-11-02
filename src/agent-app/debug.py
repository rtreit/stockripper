# health_check.py
from flask import Flask, jsonify

app = Flask(__name__)

@app.route('/health', methods=['GET'])
def health_check():
    return jsonify(status="I'm alive"), 200

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000)  # Expose on all interfaces on port 5000
