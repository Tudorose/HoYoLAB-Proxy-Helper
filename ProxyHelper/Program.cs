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
using var proxy = new EmbeddedProxy(ca, port, proxyToken, upstreamHost);
proxy.Start();
SystemProxySwitch.EnableIfUnowned();

// Auto-start at logon so the proxy exists before Hutao ever launches.
try
{
    using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
    run?.SetValue("HutaoProxyPlugin", Environment.ProcessPath ?? "", RegistryValueKind.String); // 兜底,插件也会写
}
catch { }

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



