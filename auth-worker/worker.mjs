const redirect = "http://127.0.0.1:17842/callback/";
const json = (body, status = 200) => Response.json(body, { status, headers: { "Cache-Control": "no-store", "Pragma": "no-cache" } });
export default {
  async fetch(request, env) {
    if (new URL(request.url).pathname !== "/token") return json({ error: "not_found" }, 404);
    if (request.method !== "POST") return json({ error: "method_not_allowed" }, 405);
    if (!env.SHIKIMORI_CLIENT_ID || !env.SHIKIMORI_CLIENT_SECRET || !env.TOKEN_LIMIT) return json({ error: "not_configured" }, 503);
    const allowed = await env.TOKEN_LIMIT.limit({ key: request.headers.get("CF-Connecting-IP") || "unknown" });
    if (!allowed.success) return json({ error: "rate_limited" }, 429);
    if (!(request.headers.get("Content-Type") || "").startsWith("application/x-www-form-urlencoded")) return json({ error: "invalid_request" }, 400);
    // Bound streaming input too: Content-Length cannot be trusted.
    const reader = request.body?.getReader();
    if (!reader) return json({ error: "invalid_request" }, 400);
    let text = "", size = 0;
    const decoder = new TextDecoder();
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > 8192) { await reader.cancel(); return json({ error: "too_large" }, 413); }
      text += decoder.decode(value, { stream: true });
    }
    text += decoder.decode();
    const input = new URLSearchParams(text), grant = input.get("grant_type");
    if (!["authorization_code", "refresh_token"].includes(grant)) return json({ error: "invalid_grant" }, 400);
    if (grant === "authorization_code" && input.get("redirect_uri") !== redirect) return json({ error: "invalid_redirect" }, 400);
    const key = grant === "authorization_code" ? "code" : "refresh_token", token = input.get(key);
    if (!token || token.length > 4096) return json({ error: "invalid_request" }, 400);
    const body = new URLSearchParams({ grant_type: grant, client_id: env.SHIKIMORI_CLIENT_ID, client_secret: env.SHIKIMORI_CLIENT_SECRET, [key]: token });
    if (grant === "authorization_code") body.set("redirect_uri", redirect);
    try {
      const upstream = await fetch("https://shikimori.io/oauth/token", { method: "POST", body, headers: { "User-Agent": "AnimeWatchka" }, redirect: "manual", signal: AbortSignal.timeout(20000) });
      // Workers supports manual/follow; manual also prevents forwarding secrets to redirects.
      if (upstream.status >= 300 && upstream.status < 400) return json({ error: "upstream_redirect" }, 502);
      if (!upstream.ok) return json({ error: "authorization_failed", upstream_status: upstream.status }, upstream.status === 429 ? 429 : 400);
      const value = await upstream.json();
      if (typeof value.access_token !== "string" || typeof value.refresh_token !== "string" || !Number.isFinite(value.expires_in)) return json({ error: "invalid_response" }, 502);
      return json({ access_token: value.access_token, refresh_token: value.refresh_token, expires_in: value.expires_in });
    } catch { return json({ error: "service_unavailable" }, 502); }
  }
};
