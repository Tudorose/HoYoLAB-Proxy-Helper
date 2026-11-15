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

    /// <summary>确保辅助进程在运行(带休眠的自启 + 首次复制到稳定目录)。返回 false 表示拉起失败,调用方可退回进程内代理。</summary>
    public static bool EnsureRunning(string caDir, string ownAssemblyDir, string token = "", string upstreamHost = "")
    {
        try
        {
            if (IsPortAlive())
            {
                Logger.Write("helper already running");
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
                Arguments = $"--port {Port} --ca-dir \"{stableDir}\" --reassert" + (string.IsNullOrEmpty(token) ? "" : $" --token \"{token}\"") + (string.IsNullOrEmpty(upstreamHost) ? "" : $" --upstream \"{upstreamHost}\""),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            // 由插件写入自启键(用稳定目录的绝对路径,避免辅助进程自写为空)
            try
            {
                using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                run?.SetValue("HutaoProxyPlugin", targetExe, Microsoft.Win32.RegistryValueKind.String);
            }
            catch { }

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
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("HutaoProxyPlugin", throwOnMissingValue: false);
        }
        catch { }
        SystemProxySwitch.Disable();
    }
}






