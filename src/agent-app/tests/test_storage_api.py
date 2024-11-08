# src/agent-app/tests/test_storage_api.py
import unittest
import requests
import os

class TestStorageAPI(unittest.TestCase):
    
    BASE_URL = os.getenv("BASE_URL", "http://localhost:5000/api/storage")
    
    def test_save_to_storage(self):
        # Create three test files
        with open("test_file1.txt", "w") as f:
            f.write("This is the first test file.")
        with open("test_file2.txt", "w") as f:
            f.write("This is the second test file.")
        with open("test_file3.txt", "w") as f:
            f.write("This is the third test file.")
        
        # Open files and send them as a list of tuples with the same 'file' key
        files = [
            ('file', open("test_file1.txt", "rb")),
            ('file', open("test_file2.txt", "rb")),
            ('file', open("test_file3.txt", "rb"))
        ]
        # Add blob_name or other required fields in `data`
        data = {"blob_name": "test_blob.txt"}
        
        # Send POST request
        response = requests.post(f"{self.BASE_URL}/containers/test-container/blobs", files=files, data=data)

        # Check response status and message
        self.assertEqual(response.status_code, 201)
        self.assertIn("Files uploaded successfully", response.json().get("message", ""))

        # Close files after request
        for _, file in files:
            file.close()

        # Clean up created test files
        os.remove("test_file1.txt")
        os.remove("test_file2.txt")
        os.remove("test_file3.txt")

    def test_get_from_storage(self):
        response = requests.get(f"{self.BASE_URL}/containers/test-container/blobs/test_blob.txt")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.headers['Content-Type'], 'application/octet-stream')
        self.assertEqual(response.text, "This is the third test file.")

    def test_list_containers(self):
        response = requests.get(f"{self.BASE_URL}/containers")
        self.assertEqual(response.status_code, 200)
        self.assertIsInstance(response.json()["containers"], list)
        self.assertIn("test-container", response.json()["containers"])

    def test_list_blobs(self):
        response = requests.get(f"{self.BASE_URL}/containers/test-container/blobs")
        self.assertEqual(response.status_code, 200)
        self.assertIsInstance(response.json()["blobs"], list)
        self.assertIn("test_blob.txt", response.json()["blobs"])

    def test_create_container(self):
        response = requests.post(f"{self.BASE_URL}/containers", json={"container_name": "new-container"})
        self.assertEqual(response.status_code, 201)
        self.assertIn("Container created successfully", response.json()["message"])

    def test_delete_container(self):
        response = requests.delete(f"{self.BASE_URL}/containers/new-container")
        self.assertEqual(response.status_code, 200)
        self.assertIn("Container deleted successfully", response.json()["message"])

if __name__ == "__main__":
    import requests
    unittest.main()
