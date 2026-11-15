#!/usr/bin/env node
/**
 * HoYoLAB Proxy Helper installer.
 * Downloads the latest .hutao from GitHub Releases and installs it into
 * %LOCALAPPDATA%\SnapHutaoRemastered\Plugins (Windows only).
 */
const https = require("https");
const fs = require("fs");
const path = require("path");
const os = require("os");
const { execSync } = require("child_process");
const {pipeline} = require("stream/promises");
const { spawn } = require("child_process");

const REPO = "Tudorose/HoYoLAB-Proxy-Helper";
const PLUGIN_ID = "HutaoProxyPlugin";
const PLUGINS_DIR = path.join(os.homedir(), "AppData", "Local", "SnapHutaoRemastered", "Plugins");
const HELPER_DIR = path.join(os.homedir(), "AppData", "Local", "SnapHutaoRemastered", "HutaoProxyPlugin");

function fail(msg) { console.error("[install] ERROR: " + msg); process.exit(1); }
function log(msg) { console.log("[install] " + msg); }

function get(url, redirect) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { "User-Agent": "hoyolab-proxy-helper-installer" } }, res => {
      if ([301,302,303,307,308].includes(res.statusCode) && res.headers.location && redirect !== 0)
        return resolve(get(res.headers.location, (redirect||5)-1));
      resolve(res);
    }).on("error", reject);
  });
}

(async () => {
  if (process.platform !== "win32") fail("only Windows is supported (Snap.Hutao.Remastered plugin).");
  log("fetching latest release info...");
  const res = await get(`https://api.github.com/repos/${REPO}/releases/latest`);
  if (res.statusCode !== 200) fail("cannot fetch release info (HTTP " + res.statusCode + ")");
  let body = "";
  for await (const chunk of res) body += chunk;
  const rel = JSON.parse(body);
  const asset = (rel.assets || []).find(a => a.name.endsWith(".hutao"));
  if (!asset) fail("no .hutao asset in release " + rel.tag_name);
  log("downloading " + asset.name + " (" + Math.round(asset.size/1024) + " KB)...");
  const dl = await get(asset.browser_download_url);
  if (dl.statusCode !== 200) fail("download failed (HTTP " + dl.statusCode + ")");
  const tmp = path.join(os.tmpdir(), asset.name);
  await pipeline(dl, fs.createWriteStream(tmp));

  // best-effort: stop resident helper so files are not locked
  log("stopping helper if running...");
  try { execSync("taskkill /IM ProxyHelper.exe /F /T", { stdio: "ignore" }); } catch {}

  fs.mkdirSync(PLUGINS_DIR, { recursive: true });
  // remove stale temp dirs + old package states
  for (const f of [path.join(PLUGINS_DIR, PLUGIN_ID + ".hutao"), path.join(PLUGINS_DIR, PLUGIN_ID + ".hutaodisabled")])
    try { fs.rmSync(f, { force: true }); } catch {}
  const dest = path.join(PLUGINS_DIR, PLUGIN_ID + ".hutao");
  fs.copyFileSync(tmp, dest);
  log("installed: " + dest);

  // pre-warm helper dir so first Hutao start is instant (helper binaries shipped inside the package)
  try {
    const AdmZip = null; // no extra deps: use tar? .hutao is zip; use PowerShell Expand-Archive via rename
    const zipTmp = tmp + ".zip";
    fs.copyFileSync(tmp, zipTmp);
    execSync(`powershell -NoProfile -Command "Expand-Archive -LiteralPath '${zipTmp}' -DestinationPath '${HELPER_DIR}' -Force"`, { stdio: "ignore" });
    log("helper binaries pre-warmed to " + HELPER_DIR);
    fs.rmSync(zipTmp, { force: true });
  } catch (e) { log("pre-warm skipped (will be extracted by Hutao on first load)"); }

  // Restart resident helper so an already-running Hutao (plugin enabled) recovers
  // immediately and the system proxy keeps working (helper re-asserts proxy).
  try {
    if (fs.existsSync(path.join(HELPER_DIR, "ProxyHelper.exe"))) {
      log("restarting resident helper...");
      const helperExe = path.join(HELPER_DIR, "ProxyHelper.exe");
      spawn(helperExe, [], { detached: true, stdio: "ignore", windowsHide: true }).unref();
      log("helper restarted (reads ProxyHelper.args.json for config).");
    }
  } catch (e) { log("helper restart skipped: " + e.message); }
  log("DONE. Restart Snap.Hutao.Remastered and enable the plugin in plugin management.");
  log("Then set ProxyBase (your worker URL) and ProxyToken in plugin settings.");
})().catch(e => fail(e.message));
