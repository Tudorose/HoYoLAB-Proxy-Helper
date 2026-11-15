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
    private const string ProxyBase = "https://api.example.com";
    private const string StoreDirName = "HutaoProxyPlugin";

    private X509Certificate2? ca;
    private EmbeddedProxy? proxy;
    private PluginSetting<bool>? enabledSetting;
    private PluginSetting<string>? proxyBaseSetting;
    private PluginSetting<bool>? fastStartupSetting;
    private bool helperRunning;

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

        enabledSetting = new PluginSetting<bool>(context.PluginService, "Enabled", true);
        proxyBaseSetting = new PluginSetting<string>(context.PluginService, "ProxyBase", ProxyBase);
        fastStartupSetting = new PluginSetting<bool>(context.PluginService, "加快插件启动速度", true);
        enabledSetting.Register(runtimeId, "Redirect HoYoLAB-family domains through the embedded proxy");
        proxyBaseSetting.Register(runtimeId, "Reverse proxy base URL (forwards to bbs-api-os.hoyolab.com)");
        fastStartupSetting.Register(runtimeId, "开启:使用常驻辅助进程,重启胡桃零等待;关闭:代理仅在胡桃运行期间存在(启动后约4秒内的请求可能失败,点击重试即可)");

        if (runtimeId != Manifest.Id)
        {
            try
            {
                var enabled2 = new PluginSetting<bool>(context.PluginService, "Enabled", true);
                var proxy2 = new PluginSetting<string>(context.PluginService, "ProxyBase", ProxyBase);
                var fast2 = new PluginSetting<bool>(context.PluginService, "加快插件启动速度", true);
                enabled2.Register(Manifest.Id, "Redirect HoYoLAB-family domains through the embedded proxy");
                proxy2.Register(Manifest.Id, "Reverse proxy base URL (forwards to bbs-api-os.hoyolab.com)");
                fast2.Register(Manifest.Id, "开启:使用常驻辅助进程,重启胡桃零等待;关闭:代理仅在胡桃运行期间存在(启动后约4秒内的请求可能失败,点击重试即可)");
            }
            catch (Exception ex)
            {
                Logger.Write($"manifest-id registration skipped: {ex.Message}");
            }
        }

        if (enabledSetting.Value)
        {
            StartProxy();
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        Trace.WriteLine($"[HutaoProxyPlugin] Loaded. Id={Manifest.Id} Version={Manifest.Version}");
    }

    public override void OnEnable()
    {
        if (enabledSetting?.Value ?? true)
        {
            StartProxy();
        }
    }

    public override void OnDisable()
    {
        StopProxy();
    }

    public override void OnUninstall()
    {
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
        if (!fastStartup)
        {
            // 关闭加快启动:不使用常驻辅助进程,退回进程内代理(方案A)
            HelperProcess.StopAndDeregister();
            Logger.Write("fast startup disabled, using in-process proxy");
        }

        if (fastStartup && HelperProcess.EnsureRunning(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapHutaoRemastered", StoreDirName),
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!))
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
                proxy = new EmbeddedProxy(ca!, SystemProxySwitch.ListenPort);
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

    private void Cleanup()
    {
        if (!helperRunning)
        {
            SystemProxySwitch.Disable();
        }
        proxy?.Dispose();
        proxy = null;
    }
}




