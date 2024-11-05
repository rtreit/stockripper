# src/agent-app/tests/test_storage_api.py
import unittest
import requests
import os

class TestStorageAPI(unittest.TestCase):
    
    BASE_URL = os.getenv("BASE_URL", "http://localhost:5000/api/storage")
    
    def test_save_to_storage(self):
        with open("test_file.txt", "w") as f:
            f.write("This is a test file.")
        
        with open("test_file.txt", "rb") as file:
            files = {'file': file}
            response = requests.post(f"{self.BASE_URL}/save", files=files, data={"blob_name": "test_blob.txt"})
        
        self.assertEqual(response.status_code, 201)
        self.assertIn("File uploaded successfully", response.json()["message"])

    def test_get_from_storage(self):
        response = requests.get(f"{self.BASE_URL}/get/test_blob.txt")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.headers['Content-Type'], 'application/octet-stream')
        self.assertEqual(response.text, "This is a test file.")
    
if __name__ == "__main__":
    unittest.main()
