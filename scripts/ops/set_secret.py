import subprocess, os
token = open(r"C:\Users\user\Documents\Codex\2026-11-14\new-chat\work\proxy_token.txt").read().strip()
print("token len:", len(token))
env = dict(os.environ)
env["HTTPS_PROXY"] = "http://127.0.0.1:7897"
env["HTTP_PROXY"] = "http://127.0.0.1:7897"
r = subprocess.run(["cmd", "/c", "npx wrangler secret put PROXY_TOKEN"],
                   input=token.encode(), capture_output=True, cwd=r"C:\Users\user\HoYoLab-Proxy-Helper\worker",
                   env=env, timeout=240)
print(r.stdout.decode(errors="replace")[-500:])
print(r.stderr.decode(errors="replace")[-300:])
