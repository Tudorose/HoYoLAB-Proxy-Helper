using System.Diagnostics;
using System.IO;
using HutaoProxyPlugin;
using Microsoft.Win32;
using System.Security.Cryptography.X509Certificates;

// ProxyHelper: tiny resident reverse-proxy process managed by the Hutao plugin.
// Usage: ProxyHelper.exe --port 8888 --ca-dir <dir> [--reassert]
//  --reassert: periodically re-assert system proxy only when ProxyEnable=0 (nothing else owns it).

int port = 8888;
string? caDir = null;
bool reassert = false;
string proxyToken = "";
string upstreamHost = "";
// WMI 模式: 无命令行参数时从同目录 ProxyHelper.args.json 读取配置 (由插件维护)。
if (args.Length == 0)
{
    try
    {
        string cfgPath = Path.Combine(AppContext.BaseDirectory, "ProxyHelper.args.json");
        if (File.Exists(cfgPath))
        {
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgPath)).RootElement;
            if (json.TryGetProperty("Port", out var p)) port = p.GetInt32();
            if (json.TryGetProperty("CaDir", out var d)) caDir = d.GetString();
            if (json.TryGetProperty("Token", out var t)) proxyToken = t.GetString() ?? "";
            if (json.TryGetProperty("UpstreamHost", out var u)) upstreamHost = u.GetString() ?? "";
            if (json.TryGetProperty("Reassert", out var r)) reassert = r.GetBoolean();
        }
    }
    catch (Exception ex) { Console.Error.WriteLine("config read failed: " + ex.Message); }
}
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
    else if (args[i] == "--ca-dir" && i + 1 < args.Length) caDir = args[++i];
    else if (args[i] == "--reassert") reassert = true;
    else if (args[i] == "--token" && i + 1 < args.Length) proxyToken = args[++i];
    else if (args[i] == "--upstream" && i + 1 < args.Length) upstreamHost = args[++i];
}
if (caDir == null)
{
    Console.Error.WriteLine("--ca-dir required");
    return 2;
}

var mutex = new Mutex(true, "HutaoProxyPluginHelper", out bool createdNew);
if (!createdNew)
{
    return 0; // already running
}

X509Certificate2 ca = CertKit.LoadOrCreateCa(caDir);
if (!CertKit.IsCaInstalled(ca))
{
    CertKit.InstallCa(ca); // one-time Windows trust dialog
}

if (string.IsNullOrEmpty(upstreamHost)) { Console.Error.WriteLine("--upstream required"); return 2; }
EmbeddedProxy? proxy = null;
for (int attempt = 1; attempt <= 30; attempt++)
{
    try
    {
        proxy = new EmbeddedProxy(ca, port, proxyToken, upstreamHost);
        proxy.Start();
        break;
    }
    catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
    {
        proxy?.Dispose();
        proxy = null;
        Console.Error.WriteLine($"port {port} busy, retry {attempt}/30");
        Thread.Sleep(1000);
    }
}
if (proxy == null)
{
    Console.Error.WriteLine($"could not bind port {port} after retries");
    return 3;
}
SystemProxySwitch.EnableIfUnowned();

// 启动时模式: helper 由胡桃启动时拉起, 不再登录自启。清理旧 Run 键避免开机拉起。
try
{
    using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
    run?.DeleteValue("HutaoProxyPlugin", throwOnMissingValue: false);
}
catch { }


// 生命周期: 随胡桃启动由插件拉起 (不再登录自启); 仅卸载插件时停止。
// 宿主(胡桃)退出/崩溃不影响本进程 - 零窗口形态的基础。

Logger.Write($"helper started, port={port}, reassert={reassert}");

if (reassert)
{
    _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                var enable = key?.GetValue("ProxyEnable");
                var server = key?.GetValue("ProxyServer") as string;
                // Re-assert only when nothing else owns the proxy.
                if (enable is int v && v == 0)
                {
                    SystemProxySwitch.EnableIfUnowned();
                }
                else if (server != null && server.Contains(":8888") == false && server.Contains(":7897") == false)
                {
                    // someone set an unknown proxy; leave it alone
                }
            }
            catch { }
        }
    });
}

Thread.Sleep(Timeout.Infinite);
return 0;




void Shutdown()
{
    try { Logger.Write("helper shutting down (host exited); disabling system proxy"); } catch { }
    try { SystemProxySwitch.Disable(); } catch { }
    Environment.Exit(0);
}
