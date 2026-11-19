import os, shutil, subprocess, time
stable = r"C:\Users\user\AppData\Local\SnapHutaoRemastered\HutaoProxyPlugin"
srcbin = r"C:\Users\user\Documents\Codex\2026-11-14\new-chat\work\HutaoProxyPlugin\ProxyHelper\bin\Debug"
subprocess.run(["taskkill", "/F", "/IM", "ProxyHelper.exe"], capture_output=True)
time.sleep(0.5)
for f in ["ProxyHelper.exe", "ProxyHelper.dll", "ProxyHelper.runtimeconfig.json", "ProxyHelper.deps.json"]:
    shutil.copy2(os.path.join(srcbin, f), os.path.join(stable, f))
token = open(r"C:\Users\user\Documents\Codex\2026-11-14\new-chat\work\proxy_token.txt").read().strip()
subprocess.Popen([os.path.join(stable, "ProxyHelper.exe"), "--port", "8888",
                  "--ca-dir", stable, "--reassert", "--token", token,
                  "--upstream", "api.example.com"], creationflags=subprocess.CREATE_NO_WINDOW)
time.sleep(1.5)
import socket
s = socket.socket(); s.settimeout(1)
try:
    s.connect(("127.0.0.1", 8888)); print("helper ALIVE")
except Exception as e:
    print("helper DEAD", e)
s.close()
