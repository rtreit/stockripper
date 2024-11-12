import unittest
import requests, json
import os
import uuid, random

class TestAgents(unittest.TestCase):

    BASE_URL = os.getenv("BASE_URL", "http://localhost:5000")

    def test_invoke_mailworker(self):
        input = """
        How many containers are there?
        """
        headers = {"Content-Type": "application/json"}
        # use fixed seed to ensure reproducibility for the session id - this will be used to identify the conversation for memory purposes
        #session_id = uuid.UUID(int=random.getrandbits(128))
        session_id = "704e0233-da20-431e-be57-0f6ffc94bf32"
        payload = {"input": f"{input}", "session_id": f"{session_id}"}
        response = requests.post(
            f"{self.BASE_URL}/agents/mailworker",
            headers=headers,
            data=json.dumps(payload),
        )
        self.assertEqual(response.status_code, 200)


if __name__ == "__main__":
    unittest.main()
