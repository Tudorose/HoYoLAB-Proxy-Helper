import subprocess, os, io, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf8", errors="replace")
r = subprocess.run(["cmd","/c","npx wrangler whoami"], capture_output=True, timeout=120,
                   cwd=r"C:\Users\user\HoYoLab-Proxy-Helper\worker")
print(r.stdout.decode(errors="replace")[:200])
print("ERR:", r.stderr.decode(errors="replace")[:100])
