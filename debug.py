import os
import time
print("Starting the debug.py script...")
while True:
    # Get the current time
    current_time = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime())

    # Get the environment variables
    env_vars = os.environ

    # Print the current time
    print(f"Current Time: {current_time}")

    # Print the environment variables
    print("Environment Variables:")
    for key, value in env_vars.items():
        print(f"{key}: {value}")

    # Wait for 10 seconds
    time.sleep(10)