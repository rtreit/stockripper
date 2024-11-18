import msal
import os
import json
from flask import Flask, request, redirect, url_for
from dotenv import load_dotenv

# Load environment variables from .env
load_dotenv()

# Flask app for redirect URI handling
app = Flask(__name__)

# Configurations
CLIENT_ID = os.getenv("CLIENT_ID")
CLIENT_SECRET = os.getenv("CLIENT_SECRET")
TENANT_ID = os.getenv("TENANT_ID")
AUTHORITY = f"https://login.microsoftonline.com/{TENANT_ID}"
REDIRECT_URI = "http://localhost:5000/getAToken"

# Only include non-reserved scopes for the authorization request
NON_RESERVED_SCOPES = ["Mail.Send"]

# Create an MSAL ConfidentialClientApplication instance
msal_app = msal.ConfidentialClientApplication(
    client_id=CLIENT_ID,
    authority=AUTHORITY,
    client_credential=CLIENT_SECRET,
)

@app.route('/')
def index():
    # Create the authorization request URL using only non-reserved scopes
    auth_url = msal_app.get_authorization_request_url(NON_RESERVED_SCOPES, redirect_uri=REDIRECT_URI)
    print("Redirecting to Authorization URL:", auth_url)  # Debugging
    return redirect(auth_url)

@app.route("/getAToken")
def get_a_token():
    # Debugging to ensure endpoint is hit
    print("get_a_token endpoint hit.")
    
    # Get the authorization code from the request
    code = request.args.get('code')
    if not code:
        return "No authorization code provided."

    # Exchange the authorization code for an access token
    # Use only non-reserved scopes when exchanging the code
    result = msal_app.acquire_token_by_authorization_code(code, NON_RESERVED_SCOPES, redirect_uri=REDIRECT_URI)

    if "access_token" in result:
        access_token = result["access_token"]
        refresh_token = result.get("refresh_token")

        # Display the access token and refresh token
        print("Access Token:", access_token)
        print("Refresh Token:", refresh_token)

        # Save refresh token to .env
        if refresh_token:
            with open(".env", "a") as env_file:
                env_file.write(f"\nREFRESH_TOKEN={refresh_token}")

            return "Refresh token has been saved to .env. You may now close this page."
        else:
            return "Failed to acquire a refresh token."

    return f"Failed to acquire token: {result.get('error_description')}"

if __name__ == '__main__':
    # Start the Flask app
    app.run(port=5000)
