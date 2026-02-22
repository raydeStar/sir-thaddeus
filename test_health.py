import time
import requests
import subprocess
import os

print('Starting backend...')
proc = subprocess.Popen(['uv', 'run', 'server.py', '--port', '17845'], cwd='apps/voice-backend')
time.sleep(2)

print('Polling /health for 10 seconds...')
for i in range(10):
    try:
        resp = requests.get('http://127.0.0.1:17845/health')
        data = resp.json()
        print(f"[{i}] Ready: {data.get('ready')}, Message: {data.get('message')}")
        if data.get('ready'):
            print('Backend became ready!')
            break
    except Exception as e:
        print(f'[{i}] Could not connect: {e}')
    time.sleep(1)

proc.terminate()
print('Backend terminated.')
