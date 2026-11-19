// worker.js — HoYoLAB reverse proxy worker.
//
// Access control: every request must carry the proxy token equal to the
// PROXY_TOKEN secret configured for this Worker (Cloudflare dashboard ->
// Workers -> Settings -> Variables and Secrets). The token is read via the
// `x-proxy-token` request header. Requests without a valid token are
// rejected with 403 before any upstream traffic happens.
export default {
  async fetch(request, env) {
    // Trim guards against tokens uploaded with a trailing newline (e.g. via
    // PowerShell pipelines), which would otherwise never match.
    const expected = (env.PROXY_TOKEN || "").trim();
    if (!expected || request.headers.get("x-proxy-token") !== expected) {
      return new Response("Forbidden", { status: 403 });
    }

    const url = new URL(request.url);
    const originalHost = request.headers.get("x-original-host");
    url.hostname = originalHost || "bbs-api-os.hoyolab.com";
    url.protocol = "https:";

    const headers = new Headers(request.headers);
    headers.delete("x-proxy-token");
    headers.delete("x-original-host");
    headers.delete("host");
    headers.delete("cookie");
    headers.delete("referer");

    const resp = await fetch(url, {
      method: request.method,
      headers,
      body: ["GET", "HEAD"].includes(request.method) ? undefined : request.body,
      redirect: "follow",
    });

    return new Response(resp.body, {
      status: resp.status,
      headers: resp.headers,
    });
  },
};
