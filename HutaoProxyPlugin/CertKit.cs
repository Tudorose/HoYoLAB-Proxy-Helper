using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HutaoProxyPlugin;

internal static class CertKit
{
    private const string CaPassword = "hutao-proxy-plugin";

    public static X509Certificate2 LoadOrCreateCa(string dir)
    {
        Directory.CreateDirectory(dir);
        string pfx = Path.Combine(dir, "ca.pfx");
        if (File.Exists(pfx))
        {
            return new X509Certificate2(pfx, CaPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }

        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=HutaoProxyPlugin CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        X509Certificate2 ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(pfx, ca.Export(X509ContentType.Pfx, CaPassword));
        // Reload from disk so we own a stable, non-ephemeral instance (do NOT dispose this one).
        return new X509Certificate2(pfx, CaPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    private static readonly object LeafLock = new();
    private static readonly Dictionary<string, X509Certificate2> LeafCache = new(StringComparer.OrdinalIgnoreCase);

    public static X509Certificate2 GetLeaf(X509Certificate2 ca, string host)
    {
        lock (LeafLock)
        {
            if (LeafCache.TryGetValue(host, out X509Certificate2? cached))
            {
                return cached;
            }
        }

        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(BuildDnsSanExtension(host));
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using X509Certificate2 signed = req.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30), serial);
        using X509Certificate2 withKey = signed.CopyWithPrivateKey(rsa);
        byte[] pfx = withKey.Export(X509ContentType.Pfx, "hutao-proxy-leaf");
        X509Certificate2 leaf = new(pfx, "hutao-proxy-leaf", X509KeyStorageFlags.UserKeySet);

        lock (LeafLock)
        {
            LeafCache[host] = leaf;
        }
        return leaf;
    }

    private static X509Extension BuildDnsSanExtension(string host)
    {
        byte[] dns = System.Text.Encoding.ASCII.GetBytes(host);
        byte[] tagged = DerTagLengthValue(0x82, dns);   // [2] dNSName
        byte[] sequence = DerTagLengthValue(0x30, tagged); // SEQUENCE
        return new X509Extension("2.5.29.17", sequence, false);
    }

    private static byte[] DerTagLengthValue(int tag, byte[] payload)
    {
        if (payload.Length < 128)
        {
            byte[] out1 = new byte[2 + payload.Length];
            out1[0] = (byte)tag;
            out1[1] = (byte)payload.Length;
            payload.CopyTo(out1, 2);
            return out1;
        }
        byte[] lenBytes = BitConverter.GetBytes(payload.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
        int lenLen = lenBytes.Length;
        while (lenLen > 1 && lenBytes[^lenLen] == 0) lenLen--;
        byte[] outN = new byte[2 + lenLen + payload.Length];
        outN[0] = (byte)tag;
        outN[1] = (byte)(0x80 | lenLen);
        Array.Copy(lenBytes, lenBytes.Length - lenLen, outN, 2, lenLen);
        payload.CopyTo(outN, 2 + lenLen);
        return outN;
    }

    public static bool IsCaInstalled(X509Certificate2 ca)
    {
        using X509Store store = new(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (X509Certificate2 c in store.Certificates)
        {
            if (c.Thumbprint == ca.Thumbprint)
            {
                return true;
            }
        }
        return false;
    }

    public static void InstallCa(X509Certificate2 ca)
    {
        using X509Store store = new(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (!store.Certificates.Contains(ca))
        {
            store.Add(ca);
        }
    }

    public static void UninstallCa(X509Certificate2 ca)
    {
        try
        {
            using X509Store store = new(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (X509Certificate2 c in store.Certificates)
            {
                if (c.Thumbprint == ca.Thumbprint)
                {
                    store.Remove(c);
                }
            }
        }
        catch
        {
        }
    }
}

