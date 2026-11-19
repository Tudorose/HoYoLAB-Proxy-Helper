import json, re, urllib.request, pathlib, os
cfg = pathlib.Path(os.environ["APPDATA"], "xdg.config", ".wrangler", "config", "default.toml")
tok = re.search(r'oauth_token = "(.*?)"', cfg.read_text()).group(1)
acct = "redacted-account-id"
token = open(r"C:\Users\user\Documents\Codex\2026-11-14\new-chat\work\proxy_token.txt").read().strip()
body = json.dumps({"name": "PROXY_TOKEN", "text": token, "type": "secret_text"}).encode()
req = urllib.request.Request(
    f"https://api.cloudflare.com/client/v4/accounts/{acct}/workers/scripts/hoyolab-os-proxy/secrets",
    data=body, method="PUT",
    headers={"Authorization": f"Bearer {tok}", "Content-Type": "application/json"})
proxy = urllib.request.ProxyHandler({"https": "http://127.0.0.1:7897"})
opener = urllib.request.build_opener()
try:
    resp = opener.open(req, timeout=60)
    print(resp.status, resp.read().decode()[:300])
except urllib.error.HTTPError as e:
    print("HTTP", e.code, e.read().decode()[:300])
except Exception as e:
    print("ERR", e)


