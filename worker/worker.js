export default {
  async fetch(request) {
    const orig = request.headers.get("x-original-host") || "bbs-api-os.hoyolab.com";
    const url = new URL(request.url);
    url.hostname = orig;
    const headers = new Headers(request.headers);
    headers.delete("x-original-host");
    const resp = await fetch(url, {
      method: request.method,
      headers,
      body: ["GET", "HEAD"].includes(request.method) ? undefined : request.body,
      redirect: "manual",
    });
    const out = new Headers(resp.headers);
    out.delete("set-cookie");
    return new Response(resp.body, { status: resp.status, headers: out });
  },
};
