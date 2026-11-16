// worker.js — HoYoLAB reverse proxy worker (with generic /gh/ GitHub mirror mode).
//
// Access control: every request must carry the proxy token equal to the
// PROXY_TOKEN secret configured for this Worker (Cloudflare dashboard ->
// Workers -> Settings -> Variables and Secrets). The token can be supplied
// via any of:
//   1. `x-proxy-token` request header (recommended for API clients)
//   2. `?t=<token>` query parameter (sets a long-lived proxy_token cookie
//      so root-relative form posts and links keep working)
//   3. `proxy_token` cookie (set automatically by a previous ?t= request)
// Requests without a valid token are rejected with 403 before any upstream
// traffic happens.
//
// Routing:
//   1. /gh/<path>            -> https://github.com/<path>  (GitHub mirror)
//   2. x-original-host head  -> that host (default bbs-api-os.hoyolab.com)
// In /gh/ mode, GitHub session cookies are passed through (minus
// proxy_token), and Origin/Referer are rewritten to the upstream host so
// GitHub accepts the request.
export default {
  async fetch(request, env) {
    const expected = env.PROXY_TOKEN;
    const url = new URL(request.url);
    const tokenParam = url.searchParams.get("t");
    const cookieHeader = request.headers.get("cookie") || "";
    const cookieToken =
      (cookieHeader.match(/(?:^|;\s*)proxy_token=([^;]*)/) || [])[1] || null;
    const supplied =
      request.headers.get("x-proxy-token") || tokenParam || cookieToken;
    if (!expected || supplied !== expected) {
      return new Response("Forbidden", { status: 403 });
    }

    const setAuthCookie = tokenParam === expected;

    // Strip the token query parameter before proxying.
    if (tokenParam) url.searchParams.delete("t");

    const headers = new Headers(request.headers);
    headers.delete("x-proxy-token");
    headers.delete("host");

    const stripProxyTokenCookie = (h) =>
      (h || "")
        .replace(/(^|;\s*)proxy_token=[^;]*;?/g, "$1")
        .replace(/;\s*$/, "");

    // Route 1: /gh/<path> -> GitHub mirror.
    if (url.pathname === "/gh" || url.pathname.startsWith("/gh/")) {
      const rest = url.pathname.slice(3) || "/";
      const target = new URL("https://github.com" + rest + url.search);

      headers.set("cookie", stripProxyTokenCookie(cookieHeader));

      const upstreamHost = "github.com";
      for (const name of ["origin", "referer"]) {
        const value = headers.get(name);
        if (!value) continue;
        try {
          const u = new URL(value);
          u.protocol = "https:";
          u.host = upstreamHost;
          headers.set(name, u.toString().replace(/\/$/, ""));
        } catch (e) {}
      }

      const resp = await fetch(target, {
        method: request.method,
        headers,
        body: ["GET", "HEAD"].includes(request.method)
          ? undefined
          : await request.clone().arrayBuffer(),
        redirect: "follow",
      });

      const out = new Headers(resp.headers);
      out.set("x-worker-hit", "1");
      if (setAuthCookie) {
        out.append(
          "set-cookie",
          `proxy_token=${expected}; Path=/; Max-Age=31536000; Secure; HttpOnly; SameSite=Lax`
        );
      }
      return new Response(resp.body, { status: resp.status, headers: out });
    }

    // Route 2: HoYoLAB API proxy (default).
    const originalHost = request.headers.get("x-original-host");
    url.hostname = originalHost || "bbs-api-os.hoyolab.com";
    url.protocol = "https:";

    headers.delete("x-original-host");
    headers.delete("cookie");
    headers.delete("referer");

    const resp = await fetch(url, {
      method: request.method,
      headers,
      body: ["GET", "HEAD"].includes(request.method)
        ? undefined
        : await request.clone().arrayBuffer(),
      redirect: "follow",
    });

    const out = new Headers(resp.headers);
    if (setAuthCookie) {
      out.append(
        "set-cookie",
        `proxy_token=${expected}; Path=/; Max-Age=31536000; Secure; HttpOnly; SameSite=Lax`
      );
    }
    return new Response(resp.body, { status: resp.status, headers: out });
  },
};
