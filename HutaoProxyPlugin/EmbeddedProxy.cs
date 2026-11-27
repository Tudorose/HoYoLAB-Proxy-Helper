using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HutaoProxyPlugin;

/// <summary>
/// Embedded HTTP(S) proxy. Rewrites HoYoLAB-family CONNECT tunnels through the
/// reverse proxy (upstream worker) by TLS-terminating with a locally signed cert and
/// re-issuing the request upstream. All other traffic is relayed untouched.
/// </summary>
internal sealed class EmbeddedProxy : IDisposable
{
    // 精确白名单：仅对胡桃实际调用的 API 主机做 TLS 终结并改写到 Worker。
    // 其余域名（原神登录 SDK、遥测、启动器等）一律盲转发，不拦截。
    // 名单来源：proxy.log 中 User-Agent=Snap Hutao 的全量请求统计（2026-11-26）。
    private static readonly HashSet<string> TargetHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "bbs-api-os.hoyolab.com",
        "api-os-takumi.hoyoverse.com",
        "sg-hk4e-api.hoyoverse.com",
        "sg-hyp-api.hoyoverse.com",
        "hyp-api.mihoyo.com",
        "sdk.hoyoverse.com",
        "sdk-webstatic.mihoyo.com",
        "sg-public-api.hoyolab.com",
        "sg-downloader-api.hoyoverse.com",
        "downloader-api.mihoyo.com",
        "act.hoyolab.com",
        "api-account-os.hoyoverse.com",
    };

    private readonly X509Certificate2 ca;
    private readonly string token;
    private readonly string upstreamHost;
    private readonly HttpClient upstream;
    private TcpListener? listener;
    private CancellationTokenSource? cts;
    private Task? acceptLoop;

    public int Port { get; }

    public EmbeddedProxy(X509Certificate2 ca, int port, string token, string upstreamHost)
    {
        this.ca = ca;
        this.token = token;
        this.upstreamHost = upstreamHost;
        Port = port;
        upstream = new HttpClient(new SocketsHttpHandler
        {
            Proxy = null,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
        upstream.DefaultRequestHeaders.ExpectContinue = false;
    }

    public void Start()
    {
        cts = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        acceptLoop = Task.Run(() => AcceptLoopAsync(cts.Token));
        Logger.Write($"proxy started on port {Port}");
    }

    public void Stop()
    {
        cts?.Cancel();
        listener?.Stop();
        try { acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    public void Dispose()
    {
        Stop();
        upstream.Dispose();
        cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string? connectHost = null;
        using TcpClient c = client;
        try
        {
            Logger.Write($"HandleClient start {connectHost}");
            NetworkStream raw = c.GetStream();
            connectHost = await ReadConnectTargetAsync(raw, ct);
            if (connectHost == null)
            {
                return;
            }

            bool isTarget = IsTarget(connectHost);
            if (!isTarget)
            {
                // Blind relay to the requested host:443
                Logger.Write($"blind relay {connectHost}");
                await raw.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), ct);
                await raw.FlushAsync(ct);
                await RelayAsync(raw, connectHost, 443, ct);
                return;
            }

            // TLS-terminate with a forged cert for the original host.
            await raw.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), ct);
            await raw.FlushAsync(ct);

            await using SslStream clientTls = new(raw, false);
            X509Certificate2 leaf = CertKit.GetLeaf(ca, connectHost);
            Logger.Write($"TLS server handshake start for {connectHost}");
            await clientTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = leaf,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            }, ct);
            Logger.Write($"TLS server handshake OK for {connectHost}");

            await ServeHttpAsync(clientTls, connectHost, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            Logger.Write($"client {connectHost}: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Write($"proxy error for {connectHost}: {ex}");
        }
    }

    private static async Task<string?> ReadConnectTargetAsync(NetworkStream raw, CancellationToken ct)
    {
        var sb = new StringBuilder();
        byte[] buf = new byte[1];
        while (true)
        {
            int n = await raw.ReadAsync(buf, ct);
            if (n == 0)
            {
                return null;
            }
            sb.Append((char)buf[0]);
            string s = sb.ToString();
            if (s.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
            if (sb.Length > 8192)
            {
                return null;
            }
        }
        string head = sb.ToString();
        string first = head.Split('\n')[0].Trim();
        string[] parts = first.Split(' ');
        if (parts.Length >= 2 && parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            string hostport = parts[1];
            int colon = hostport.LastIndexOf(':');
            return colon > 0 ? hostport[..colon] : hostport;
        }

        if (parts.Length >= 2 && Uri.TryCreate(parts[1], UriKind.Absolute, out Uri? abs))
        {
            await RelayPlainHttpAsync(raw, head, abs, ct);
        Logger.Write($"CONNECT target parse failed. first line: {first}");
        }
        return null;
    }
    private static bool IsTarget(string host)
    {
        return TargetHosts.Contains(host);
    }

    private async Task ServeHttpAsync(SslStream clientTls, string originalHost, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpRequestMessage? upstreamRequest;
            try
            {
                upstreamRequest = await ReadClientRequestAsync(clientTls, originalHost, ct);
            }
            catch (Exception ex)
            {
                Logger.Write($"read request failed for {originalHost}: {ex.Message}");
                return;
            }
            if (upstreamRequest == null)
            {
                return;
            }

            HttpResponseMessage resp;
            try
            {
                resp = await upstream.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                upstreamRequest.Dispose();
                Logger.Write($"upstream failed for {originalHost}: {ex.Message}");
                byte[] err = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                try { await clientTls.WriteAsync(err, ct); } catch { }
                return;
            }

            using (upstreamRequest)
            Logger.Write($"upstream response for {originalHost}: {(int)resp.StatusCode}");
            {
                try
                {
                    await WriteResponseAsync(clientTls, resp, ct);
                }
                catch (Exception ex)
                {
                    Logger.Write($"write response failed for {originalHost}: {ex.Message}");
                }
            }
            resp.Dispose();
        }
    }

    private async Task<HttpRequestMessage?> ReadClientRequestAsync(SslStream tls, string originalHost, CancellationToken ct)
    {
        byte[] leftover;
        string head;
        (head, leftover) = await ReadHeadAsync(tls, ct);
        if (head.Length == 0)
        {
            return null;
        }

        Logger.Write($"ReadClientRequest head len={head.Length} leftover={leftover.Length}");
        Logger.Write($"HEAD from {originalHost}: {head.Replace("\r\n", " | ")}");

        string[] lines = head.Replace("\r\n", "\n").Split('\n');
        string[] first = lines[0].Split(' ');
        if (first.Length < 2)
        {
            return null;
        }
        string path = first[1];
        var request = new HttpRequestMessage(new HttpMethod(first[0]), new Uri($"https://{upstreamHost}{path}"));

        bool hasContentLength = false;
        long contentLength = 0;
        bool chunked = false;
        List<KeyValuePair<string, string>> contentHeaders = new();
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            string name = line[..c].Trim();
            string value = line[(c + 1)..].Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("X-Original-Host", value);
                continue;
            }
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                hasContentLength = true;
                _ = long.TryParse(value, out contentLength);
                continue;
            }
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    chunked = true;
                }
                continue;
            }
            // Content-* 头属于 HttpContent.Headers; 塞进 HttpRequestHeaders 会被静默丢弃,
            // 导致 POST 的 JSON body 失去 Content-Type, HoYoLAB 返回「参数异常」。
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                contentHeaders.Add(new KeyValuePair<string, string>(name, value));
                continue;
            }
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.Host = upstreamHost;
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("x-proxy-token", token);
        }

        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
        {
            ApplyContentHeaders(request, contentHeaders);
            return request;
        }

        if (chunked)
        {
            byte[] body = await ReadChunkedBodyAsync(tls, leftover, ct);
            request.Content = new ByteArrayContent(body);
            ApplyContentHeaders(request, contentHeaders);
            return request;
        }

        if (hasContentLength && contentLength > 0)
        {
            byte[] body = await ReadBodyAsync(tls, leftover, contentLength, ct);
            request.Content = new ByteArrayContent(body);
        }

        ApplyContentHeaders(request, contentHeaders);
        return request;
    }

    private static void ApplyContentHeaders(HttpRequestMessage request, List<KeyValuePair<string, string>> contentHeaders)
    {
        if (contentHeaders.Count == 0)
        {
            return;
        }

        request.Content ??= new ByteArrayContent(Array.Empty<byte>());
        foreach (KeyValuePair<string, string> header in contentHeaders)
        {
            request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        Logger.Write($"applied content headers: {string.Join(", ", contentHeaders.Select(h => h.Key))}");
    }

    private static async Task<(string Head, byte[] Leftover)> ReadHeadAsync(SslStream tls, CancellationToken ct)
    {
        var buffer = new List<byte>();
        byte[] buf = new byte[4096];
        while (true)
        {
            int n = await tls.ReadAsync(buf, ct);
            if (n == 0) { Logger.Write($"HEAD read: stream closed, buffered {buffer.Count}"); break; }
            for (int i = 0; i < n; i++) buffer.Add(buf[i]);
            Logger.Write($"HEAD read {n} bytes, total {buffer.Count}, headEndFound={IndexOfHeadEnd(buffer) >= 0}");
            int idx = IndexOfHeadEnd(buffer);
            if (idx >= 0)
            {
                string head = Encoding.ASCII.GetString(buffer.ToArray(), 0, idx + 4);
                byte[] leftover = new byte[buffer.Count - idx - 4];
                buffer.CopyTo(idx + 4, leftover, 0, leftover.Length);
                return (head, leftover);
            }
            if (buffer.Count > 256 * 1024) break;
        }
        return (Encoding.ASCII.GetString(buffer.ToArray()), Array.Empty<byte>());
    }

    private static int IndexOfHeadEnd(List<byte> buffer)
    {
        for (int i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task<byte[]> ReadBodyAsync(SslStream tls, byte[] leftover, long contentLength, CancellationToken ct)
    {
        Logger.Write($"ReadBodyAsync contentLength={contentLength} leftover={leftover.Length}");
        byte[] body = new byte[contentLength];
        int fromLeftover = (int)Math.Min(leftover.Length, contentLength);
        Array.Copy(leftover, body, fromLeftover);
        int offset = fromLeftover;
        // If we over-read into the body beyond this request length, that would belong to a pipelined
        // request; Chromium/HttpClient do not pipeline, so this does not occur in practice.
        while (offset < contentLength)
        {
            int n = await tls.ReadAsync(body.AsMemory(offset), ct);
            if (n == 0) throw new IOException("client closed during body");
            offset += n;
        }
        return body;
    }

    private static async Task ReadExactlyAsync(Stream tls, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await tls.ReadAsync(buffer.AsMemory(offset), ct);
            if (n == 0) throw new IOException("client closed during body");
            offset += n;
        }
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(SslStream tls, byte[] leftover, CancellationToken ct)
    {
        await using Stream bodyStream = new PushbackStream(leftover, tls);
        using var ms = new MemoryStream();
        while (true)
        {
            string sizeLine = await ReadLineAsync(bodyStream, ct);
            int size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
            if (size == 0)
            {
                await ReadLineAsync(bodyStream, ct);
                break;
            }
            byte[] chunk = new byte[size];
            await ReadExactlyAsync(bodyStream, chunk, ct);
            ms.Write(chunk);
            await ReadLineAsync(bodyStream, ct);
        }
        return ms.ToArray();
    }

    private static async Task<string> ReadLineAsync(Stream tls, CancellationToken ct)
    {
        var sb = new StringBuilder();
        byte[] buf = new byte[1];
        while (true)
        {
            int n = await tls.ReadAsync(buf, ct);
            if (n == 0) break;
            char ch = (char)buf[0];
            if (ch == '\n') break;
            if (ch != '\r') sb.Append(ch);
            if (sb.Length > 1024) break;
        }
        return sb.ToString();
    }

    private static async Task WriteResponseAsync(SslStream tls, HttpResponseMessage resp, CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append((int)resp.StatusCode).Append(' ').Append(resp.ReasonPhrase).Append("\r\n");
        foreach (var h in resp.Headers)
        {
            if (h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string v in h.Value)
            {
                head.Append(h.Key).Append(": ").Append(v).Append("\r\n");
            }
        }
        foreach (var h in resp.Content.Headers)
        {
            if (h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string v in h.Value)
            {
                head.Append(h.Key).Append(": ").Append(v).Append("\r\n");
            }
        }

        bool hasLength = resp.Content.Headers.ContentLength.HasValue;
        if (!hasLength)
        {
            head.Append("Transfer-Encoding: chunked\r\n");
        }
        head.Append("\r\n");
        await tls.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);

        await using Stream content = await resp.Content.ReadAsStreamAsync(ct);
        byte[] buf = new byte[8192];
        while (true)
        {
            int n = await content.ReadAsync(buf, ct);
            if (n == 0) break;
            if (!hasLength)
            {
                await tls.WriteAsync(Encoding.ASCII.GetBytes(n.ToString("X") + "\r\n"), ct);
            }
            await tls.WriteAsync(buf.AsMemory(0, n), ct);
            if (!hasLength)
            {
                await tls.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ct);
            }
        }
        if (!hasLength)
        {
            await tls.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"), ct);
        }
        await tls.FlushAsync(ct);
    }

    private static async Task RelayAsync(NetworkStream client, string host, int port, CancellationToken ct)
    {
        using TcpClient remote = new();
        try
        {
            // 带超时连接: 被墙域可能无限丢包, 不设超时会让客户端吊死到系统级超时。
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));
            await remote.ConnectAsync(host, port, connectCts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            Logger.Write($"blind relay connect failed for {host}: {ex.GetType().Name}");
            try
            {
                byte[] err = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await client.WriteAsync(err, ct);
            }
            catch { }
            try { client.Close(); } catch { }
            return;
        }
        await using NetworkStream rs = remote.GetStream();
        Task a = client.CopyToAsync(rs, ct);
        Task b = rs.CopyToAsync(client, ct);
        await Task.WhenAny(a, b);
        try { client.Close(); } catch { }
    }

    private static async Task RelayPlainHttpAsync(NetworkStream client, string head, Uri abs, CancellationToken ct)
    {
        try
        {
            using TcpClient remote = new();
            await remote.ConnectAsync(abs.Host, abs.Port == -1 ? 80 : abs.Port, ct);
            await using NetworkStream rs = remote.GetStream();
            await rs.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            Task a = client.CopyToAsync(rs, ct);
            Task b = rs.CopyToAsync(client, ct);
            await Task.WhenAny(a, b);
        }
        catch
        {
            try { client.Close(); } catch { }
        }
    }
}










