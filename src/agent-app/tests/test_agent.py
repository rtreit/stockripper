import unittest
import requests, json
import os


class TestAgents(unittest.TestCase):

    BASE_URL = os.getenv("BASE_URL", "http://localhost:5000")

    def test_invoke_mailworker(self):
        input = """
        Generate a random number between 1 and 10.
        Count the number of blobs in the storage account. 
        Compare them with the random number. 
        If the number of blobs is greater than the random number, send an ascii image of a chicken to charlottetreit@outlook.com
        If the number of blobs is less than the random number, send an ascii image of a golden retriever to charlottetreit@outlook.com.
        In each case include a poem about the animal.
        Repeat this process 3 times, using a different random number each time. 
        """
        headers = {"Content-Type": "application/json"}
        payload = {"input": f"{input}"}
        response = requests.post(
            f"{self.BASE_URL}/agents/mailworker",
            headers=headers,
            data=json.dumps(payload),
        )
        self.assertEqual(response.status_code, 200)


if __name__ == "__main__":
    unittest.main()
