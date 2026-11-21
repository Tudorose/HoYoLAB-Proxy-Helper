using System.Diagnostics;
using System.IO;
using HutaoProxyPlugin;
using Microsoft.Win32;
using System.Security.Cryptography.X509Certificates;

// ProxyHelper: resident reverse-proxy process managed by the Hutao plugin.
// Modes:
//   console (default): --port 8888 --ca-dir <dir> [--reassert] [--token t] [--upstream h]
//     - WMI fallback: with no CLI args, config is read from ProxyHelper.args.json
//   --machine: run with machine-scope proxy (HKLM + ProxySettingsPerUser=0).
//     Used by the "HutaoProxyHelper" scheduled task (SYSTEM, at system startup)
//     so the proxy is up for every user session, independent of user logon.

return ProxyHelperMain.Run(args);

public static class ProxyHelperMain
{
    private static EmbeddedProxy? proxy;
    private static int port = 8888;
    private static string caDir = "";
    private static bool reassert = false;
    private static string proxyToken = "";
    private static string upstreamHost = "";
    private static bool machineScope;

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            try
            {
                string cfgPath = Path.Combine(AppContext.BaseDirectory, "ProxyHelper.args.json");
                if (File.Exists(cfgPath))
                {
                    var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgPath)).RootElement;
                    if (json.TryGetProperty("Port", out var p)) port = p.GetInt32();
                    if (json.TryGetProperty("CaDir", out var d)) caDir = d.GetString() ?? "";
                    if (json.TryGetProperty("Token", out var t)) proxyToken = t.GetString() ?? "";
                    if (json.TryGetProperty("UpstreamHost", out var u)) upstreamHost = u.GetString() ?? "";
                    if (json.TryGetProperty("Reassert", out var r)) reassert = r.GetBoolean();
                    if (json.TryGetProperty("Machine", out var m)) machineScope = m.GetBoolean();
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
            else if (args[i] == "--machine") machineScope = true;
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
        var storeLocation = machineScope ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        if (!CertKit.IsCaInstalled(ca, storeLocation))
        {
            CertKit.InstallCa(ca, storeLocation);
        }

        if (string.IsNullOrEmpty(upstreamHost)) { Console.Error.WriteLine("--upstream required"); return 2; }
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

        if (machineScope)
        {
            // SYSTEM 上下文: 机器级代理, 对所有用户会话生效。
            SystemProxySwitch.EnableMachine();
            if (reassert) StartMachineReassertLoop();
            Logger.Write($"helper started (machine scope), port={port}, reassert={reassert}");

            // 驻留运行, 直到系统关机或进程被杀。
            Thread.Sleep(Timeout.Infinite);
            return 0;
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
    }

    private static void StartMachineReassertLoop()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                    var enable = key?.GetValue("ProxyEnable");
                    var server = key?.GetValue("ProxyServer") as string;
                    if (enable is int v && v == 0)
                    {
                        SystemProxySwitch.EnableMachine();
                    }
                    else if (server != null && server.Contains(":8888") == false && server.Contains(":7897") == false)
                    {
                        // foreign proxy; leave alone
                    }
                }
                catch { }
            }
        });
    }
}
