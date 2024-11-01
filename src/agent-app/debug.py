import os
import time

print ("Hello from Python")
while True:
    current_time = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime())
    env_vars = os.environ
    print(f"Current Time: {current_time}")
    print("Environment Variables:")
    for key, value in env_vars.items():
        print(f"{key}: {value}")
    time.sleep(60)