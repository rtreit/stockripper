# src/agent-app/tests/test_storage_api.py
import unittest
import requests
import os

class TestStorageAPI(unittest.TestCase):
    
    BASE_URL = os.getenv("BASE_URL", "http://localhost:5000/api")
    
    def test_send_mail(self):
        # Prepare the payload for the email sending request
        payload = {
            "recipient": "fchopin@outlook.com",
            "subject": "Test Email",
            "body": "This is a test email sent by the automated test case."
        }
        
        # Send the POST request to the /mail/send endpoint
        response = requests.post(f"{self.BASE_URL}/mail/send", json=payload)
        
        # Assertions
        self.assertEqual(response.status_code, 201)
        self.assertIn("E-mail sent", response.json().get("message", ""))

if __name__ == "__main__":
    import requests
    unittest.main()
