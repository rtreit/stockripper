import unittest
import requests, json
import os


class TestAgents(unittest.TestCase):

    BASE_URL = os.getenv("BASE_URL", "http://localhost:5000")

    def test_invoke_mailworker(self):
        input = """
        Write a friendly e-mail to fchopin@outlook.com. Sign it as "Randy". Open with dear Frederic. 
        Compliment him on his latest composition. Ask him if he would like to collaborate on a new piece.
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
