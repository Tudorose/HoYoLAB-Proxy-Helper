import subprocess, time, os
subprocess.run(["taskkill", "/F", "/IM", "ProxyHelper.exe"], capture_output=True)
time.sleep(0.6)
token = open(r"C:\Users\user\Documents\Codex\2026-11-14\new-chat\work\proxy_token.txt").read().strip()
exe = r"C:\Users\user\AppData\Local\SnapHutaoRemastered\HutaoProxyPlugin\ProxyHelper.exe"
subprocess.Popen([exe, "--port", "8888", "--ca-dir",
                  r"C:\Users\user\AppData\Local\SnapHutaoRemastered\HutaoProxyPlugin",
                  "--reassert", "--token", token, "--upstream", "api.example.com"],
                 creationflags=subprocess.CREATE_NO_WINDOW)
time.sleep(1.5)
import socket
s = socket.socket(); s.settimeout(1)
try:
    s.connect(("127.0.0.1", 8888)); print("helper ALIVE")
except Exception as e:
    print("helper DEAD", e)
s.close()
