using Microsoft.Win32;

namespace HutaoProxyPlugin;

internal static class SystemProxySwitch
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    public const int ListenPort = 8888;

    public static void Enable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{ListenPort}", RegistryValueKind.String);
        TryNotify();
    }

    public static void Disable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;
        string? v = key.GetValue("ProxyServer") as string;
        // Only disable if it still points at us — don't clobber other tools' settings.
        if (v != null && v.Contains($":{ListenPort}", StringComparison.Ordinal))
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            TryNotify();
        }
    }

    public static void EnableIfUnowned()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;
        var enable = key.GetValue("ProxyEnable");
        var server = key.GetValue("ProxyServer") as string;
        // A missing (deleted) ProxyEnable or ProxyServer value means the proxy
        // is effectively off - treat it as unowned and take over. A non-zero
        // ProxyEnable pointing at a foreign server is genuinely owned by
        // another tool, so we leave it alone.
        bool unowned =
            enable == null ||
            server == null ||
            (enable is int v && v == 0) ||
            (server != null && server.Contains($":{ListenPort}", StringComparison.Ordinal));
        if (unowned)
        {
            Enable();
        }
        else
        {
            Logger.Write($"proxy owned by others ({server}), not asserting");
        }
    }

    // ---- Machine scope (used by the SYSTEM service at boot) ----
    // HKLM proxy + ProxySettingsPerUser=0 makes the proxy apply to every user
    // session, independent of per-user logon.

    public static void EnableMachine()
    {
        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            policy?.SetValue("ProxySettingsPerUser", 0, RegistryValueKind.DWord);
        }
        catch { }
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{ListenPort}", RegistryValueKind.String);
    }

    public static void DisableMachine()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
    }

    private static void TryNotify()
    {
        try
        {
            _ = WinInetNotify.InternetSetOption(default, 39, default, 0);
            _ = WinInetNotify.InternetSetOption(default, 37, default, 0);
        }
        catch
        {
        }
    }
}

internal static class WinInetNotify
{
    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    public static extern bool InternetSetOption(nint hInternet, int dwOption, nint lpBuffer, int dwBufferLength);
}




