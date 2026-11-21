using System;
using System.IO;

namespace HutaoProxyPlugin;

internal static class Logger
{
    private static readonly object Lock = new();
    public static string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapHutaoRemastered", "HutaoProxyPlugin");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
                Directory.CreateDirectory(Dir);
            {
                File.AppendAllText(Path.Combine(Dir, "proxy.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
