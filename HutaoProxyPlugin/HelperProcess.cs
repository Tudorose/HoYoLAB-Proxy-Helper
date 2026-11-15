using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;

namespace HutaoProxyPlugin;

internal static class HelperProcess
{
    public const int Port = 8888;
    private const string HelperName = "ProxyHelper";
    private static Process? process;
    private static DataReceivedEventHandler? errHandler;
    private static DataReceivedEventHandler? outHandler;

    /// <summary>确保辅助进程在运行(带休眠的自启 + 首次复制到稳定目录)。返回 false 表示拉起失败,调用方可退回进程内代理。</summary>
    public static bool EnsureRunning(string caDir, string ownAssemblyDir, string token = "", string upstreamHost = "")
    {
        try
        {
            if (IsPortAlive())
            {
                Logger.Write("helper already running");
                // 常驻模式下重新启用插件时 Run 键可能已被禁用流程移除, 这里补写。
                string? existingExe = FindHelperExe(ownAssemblyDir);
                if (existingExe != null)
                {
                    try
                    {
                        using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                        run?.SetValue("HutaoProxyPlugin", Path.Combine(caDir, HelperName + ".exe"), Microsoft.Win32.RegistryValueKind.String);
                    }
                    catch { }
                }
                return true;
            }

            string stableDir = caDir;
            string sourceExe = FindHelperExe(ownAssemblyDir);
            if (sourceExe == null)
            {
                Logger.Write("helper exe not found in package");
                return false;
            }

            Directory.CreateDirectory(stableDir);
            string sourceDir = Path.GetDirectoryName(sourceExe)!;
            foreach (string file in Directory.GetFiles(sourceDir, HelperName + ".*"))
            {
                if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(file);
                string target = Path.Combine(stableDir, name);
                if (!File.Exists(target) || File.GetLastWriteTime(target) < File.GetLastWriteTime(file))
                {
                    File.Copy(file, target, overwrite: true);
                }
            }
            string targetExe = Path.Combine(stableDir, HelperName + ".exe");

            process = Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"--port {Port} --ca-dir \"{stableDir}\" --reassert --token \"{token}\" --upstream \"{upstreamHost}\"",
            });
            // 常驻模式: 写 Run 键, 登录时自启 helper, 保证开机后首次启动胡桃零窗口。
            try
            {
                using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                run?.SetValue("HutaoProxyPlugin", targetExe, Microsoft.Win32.RegistryValueKind.String);
            }
            catch { }
            // OS 级保底: helper 加入 Job Object, 宿主死亡时内核立即终止它。
            // 常驻模式下宿主(胡桃)退出不应杀 helper - 因此不使用 KILL_ON_JOB_CLOSE 绑定。
            errHandler = (_, e) => { if (e.Data != null) Logger.Write($"[helper-stderr] {e.Data}"); };
            outHandler = (_, e) => { if (e.Data != null) Logger.Write($"[helper-stdout] {e.Data}"); };
            process.ErrorDataReceived += errHandler;
            process.OutputDataReceived += outHandler;
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // 等待监听就绪;辅助进程存活即视为成功(它自己会完成绑定并接管系统代理)
            for (int i = 0; i < 75; i++)
            {
                if (IsPortAlive())
                {
                    Logger.Write("helper spawned and listening");
                    return true;
                }
                bool alive = false;
                try { alive = !process!.HasExited; } catch { }
                if (alive && i % 10 == 9)
                {
                    Logger.Write($"helper starting, waiting ({i + 1}/75)");
                }
                if (!alive)
                {
                    Logger.Write("helper exited before listening");
                    return false;
                }
                Thread.Sleep(200);
            }
            Logger.Write("helper did not become ready in time; assuming it will");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Write($"EnsureRunning failed: {ex}");
            return false;
        }
    }

    private static string? FindHelperExe(string ownAssemblyDir)
    {
        string[] candidates =
        {
            Path.Combine(ownAssemblyDir, "lib", "ProxyHelper", HelperName + ".exe"),
            Path.Combine(ownAssemblyDir, "lib", HelperName + ".exe"),
            Path.Combine(ownAssemblyDir, HelperName + ".exe"),
        };
        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static bool IsPortAlive()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(System.Net.IPAddress.Loopback, Port).WaitAsync(TimeSpan.FromMilliseconds(400));
            connect.Wait(400);
            if (client.Connected) return true;
            client.Close();
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>停止辅助进程并移除自启项(仅卸载/禁用时调用)。</summary>
    public static void StopAndDeregister()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(HelperName))
            {
                try
                {
                    Logger.Write($"killing helper pid={p.Id}");
                    p.Kill();
                    p.WaitForExit(5000);
                    Logger.Write($"helper pid={p.Id} exited={p.HasExited}");
                }
                catch (Exception ex)
                {
                    Logger.Write($"kill helper failed: {ex.Message}");
                }
            }
        }
        catch { }

        // 关键: 解除对插件程序集代码的委托引用并释放 Process 对象,
        // 否则默认上下文中的 Process 永久钉住 collectible ALC, 禁用后无法卸载。
        try
        {
            if (process != null)
            {
                if (errHandler != null) process.ErrorDataReceived -= errHandler;
                if (outHandler != null) process.OutputDataReceived -= outHandler;
                process.Dispose();
            }
        }
        catch { }
        finally
        {
            process = null;
            errHandler = null;
            outHandler = null;
        }
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("HutaoProxyPlugin", throwOnMissingValue: false);
        }
        catch { }
        SystemProxySwitch.Disable();
    }
}







