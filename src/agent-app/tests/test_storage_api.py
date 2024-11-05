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
            # Note: JSON data must be separate from the files
            json_data = {"blob_name": "test_blob.txt"}
            response = requests.post(f"{self.BASE_URL}/save/test-container", files=files, data=json_data)

        self.assertEqual(response.status_code, 201)
        self.assertIn("File uploaded successfully", response.json()["message"])


    def test_get_from_storage(self):
        response = requests.get(f"{self.BASE_URL}/get/test-container/test_blob.txt")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.headers['Content-Type'], 'application/octet-stream')
        self.assertEqual(response.text, "This is a test file.")

    def test_list_containers(self):
        response = requests.get(f"{self.BASE_URL}/list/containers")
        self.assertEqual(response.status_code, 200)
        self.assertIsInstance(response.json()["containers"], list)
        self.assertIn("test-container", response.json()["containers"])

    def test_list_blobs(self):
        response = requests.get(f"{self.BASE_URL}/list/blobs/test-container")
        self.assertEqual(response.status_code, 200)
        self.assertIsInstance(response.json()["blobs"], list)
        self.assertIn("test_blob.txt", response.json()["blobs"])

    def test_create_container(self):
        response = requests.post(f"{self.BASE_URL}/create-container", json={"container_name": "new-container"})
        self.assertEqual(response.status_code, 201)
        self.assertIn("Container created successfully", response.json()["message"])

    def test_delete_container(self):
        response = requests.delete(f"{self.BASE_URL}/delete-container/new-container")
        self.assertEqual(response.status_code, 200)
        self.assertIn("Container deleted successfully", response.json()["message"])

if __name__ == "__main__":
    unittest.main()
