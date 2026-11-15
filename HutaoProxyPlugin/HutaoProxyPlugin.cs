using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Snap.Hutao.Remastered.API.Annotation;
using Snap.Hutao.Remastered.API.Model;
using Snap.Hutao.Remastered.API.Setting;

namespace HutaoProxyPlugin;

[PluginMain]
public sealed class HutaoProxyPluginMain : HutaoPlugin
{
    private const string ProxyBase = "";
    private const string StoreDirName = "HutaoProxyPlugin";

    private X509Certificate2? ca;
    private EmbeddedProxy? proxy;
    private PluginSetting<string>? proxyBaseSetting;
    private PluginSetting<bool>? fastStartupSetting;
    private PluginSetting<string>? proxyTokenSetting;
    private bool helperRunning;
    private EventHandler? processExitHandler;

    public override void OnInstall()
    {
        EnsureCaInstalled();
        Trace.WriteLine("[HutaoProxyPlugin] Installed (CA ready).");
    }

    public override void OnLoad(PluginContext context)
    {
        EnsureCaInstalled();

        // 运行时插件 ID 取自 .hutao 文件名(PluginService.LoadAllPluginsAsync),与 Manifest.Id 可能不同。
        // 行为读取走 runtimeId(设置页保存也写 runtimeId);manifestId 再注册一份仅为兼容。
        string runtimeId = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location);

        proxyBaseSetting = new PluginSetting<string>(context.PluginService, "ProxyBase", ProxyBase);
        proxyTokenSetting = new PluginSetting<string>(context.PluginService, "ProxyToken", "");
        fastStartupSetting = new PluginSetting<bool>(context.PluginService, "加快插件启动速度", true);
        proxyBaseSetting.Register(runtimeId, "反向代理地址");
            proxyTokenSetting.Register(runtimeId, "访问令牌,需与 Worker 端 PROXY_TOKEN 一致");
        fastStartupSetting.Register(runtimeId, "开启:提前插件启动时机避免胡桃工具箱报错;关闭:胡桃工具箱可能出现一两个403报错显示网络异常,但不实际影响功能");

        if (runtimeId != Manifest.Id)
        {
            try
            {
                var proxy2 = new PluginSetting<string>(context.PluginService, "ProxyBase", ProxyBase);
                var fast2 = new PluginSetting<bool>(context.PluginService, "加快插件启动速度", true);
                proxy2.Register(Manifest.Id, "Reverse proxy base URL (forwards to bbs-api-os.hoyolab.com)");
                fast2.Register(Manifest.Id, "开启:使用常驻辅助进程,重启胡桃零等待;关闭:代理仅在胡桃运行期间存在(启动后约4秒内的请求可能失败,点击重试即可)");
            }
            catch (Exception ex)
            {
                Logger.Write($"manifest-id registration skipped: {ex.Message}");
            }
        }

        StartProxy();

        processExitHandler = (_, _) => Cleanup();
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;
        Trace.WriteLine($"[HutaoProxyPlugin] Loaded. Id={Manifest.Id} Version={Manifest.Version}");
    }

    public override void OnEnable()
    {
        StartProxy();
    }

    public override void OnDisable()
    {
        // 禁用插件 = 停止常驻 helper + 移除 Run 自启 + 清系统代理(与 Enabled=false 行为一致)。
        AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        HelperProcess.StopAndDeregister();
        helperRunning = false;
        StopProxy();


    }

    public override void OnUninstall()
    {
        AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        HelperProcess.StopAndDeregister();
        helperRunning = false;
        StopProxy();


        if (ca != null)
        {
            CertKit.UninstallCa(ca);
        }
    }

    private void EnsureCaInstalled()
    {
        if (ca != null && CertKit.IsCaInstalled(ca)) return;
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapHutaoRemastered", StoreDirName);
        ca = CertKit.LoadOrCreateCa(dir);
        if (!CertKit.IsCaInstalled(ca))
        {
            CertKit.InstallCa(ca); // one-time Windows trust dialog
        }
    }

    private void StartProxy()
    {
        // OnLoad 与 OnEnable 都会调用本方法;任何一种代理模式已在运行时直接返回。
        if (helperRunning || proxy != null) return;
        if (ca == null) EnsureCaInstalled();
        bool fastStartup = fastStartupSetting?.Value ?? true;
        string token = proxyTokenSetting?.Value ?? "";
        Logger.Write($"StartProxy: fastStartup={fastStartup} tokenLen={token.Length} proxyBase=\"{proxyBaseSetting?.Value}\"");
        string upstreamHost = "";
        try { upstreamHost = new Uri(proxyBaseSetting?.Value ?? "").Host; } catch { }

        if (!fastStartup)
        {
            HelperProcess.StopAndDeregister();
            Logger.Write("fast startup disabled, using in-process proxy");
        }

        if (fastStartup && HelperProcess.EnsureRunning(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapHutaoRemastered", StoreDirName),
                System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                token,
                upstreamHost))
        {
            helperRunning = true;
            Trace.WriteLine("[HutaoProxyPlugin] helper proxy is serving.");
            return;
        }

        // 进程内代理绑定失败(端口被占)不应炸掉插件加载:等待辅助进程退出后重试
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (string.IsNullOrEmpty(upstreamHost))
                {
                    Logger.Write("ProxyBase is empty; cannot start in-process proxy");
                    return;
                }
                proxy = new EmbeddedProxy(ca!, SystemProxySwitch.ListenPort, token, upstreamHost);
                proxy.Start();
                SystemProxySwitch.Enable();
                Trace.WriteLine($"[HutaoProxyPlugin] Embedded proxy listening on 127.0.0.1:{SystemProxySwitch.ListenPort}.");
                return;
            }
            catch (Exception ex)
            {
                Logger.Write($"in-process proxy bind attempt {attempt} failed: {ex.Message}");
                proxy?.Dispose();
                proxy = null;
                if (attempt < 5) Thread.Sleep(2000);
            }
        }
        Trace.WriteLine("[HutaoProxyPlugin] in-process proxy could not bind; giving up (system proxy left disabled).");
    }

    private void StopProxy()
    {
        if (helperRunning)
        {
            // 辅助进程常驻,不清代理不杀进程;卸载插件时才彻底清理。
            Trace.WriteLine("[HutaoProxyPlugin] helper proxy keeps running (uninstall to stop).");
            return;
        }
        SystemProxySwitch.Disable();
        try { proxy?.Dispose(); } catch { }
        proxy = null;
        Trace.WriteLine("[HutaoProxyPlugin] Embedded proxy stopped.");
    }

    private void WriteHelperArgsJson()
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapHutaoRemastered", StoreDirName);
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Port = HelperProcess.Port,
                CaDir = dir,
                Token = proxyTokenSetting?.Value ?? "",
                UpstreamHost = TryGetUpstreamHost(),
                Reassert = true,
            });
            File.WriteAllText(System.IO.Path.Combine(dir, "ProxyHelper.args.json"), json);
        }
        catch (Exception ex) { Logger.Write($"write helper args failed: {ex.Message}"); }
    }

    private string TryGetUpstreamHost()
    {
        try { return new Uri(proxyBaseSetting?.Value ?? "").Host; } catch { return ""; }
    }

    private void Cleanup()
    {
        // 常驻模式: 胡桃退出不影响 helper 与系统代理; 仅卸载插件(OnUninstall)时彻底清理。
        if (!helperRunning)
        {
            SystemProxySwitch.Disable();
        }
        proxy?.Dispose();
        proxy = null;
    }
}