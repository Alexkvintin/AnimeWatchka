import test from "node:test";
import assert from "node:assert/strict";
import worker from "./worker.mjs";
const env = { SHIKIMORI_CLIENT_ID: "test-client", SHIKIMORI_CLIENT_SECRET: "test-secret", TOKEN_LIMIT: { limit: async () => ({ success: true }) } };
const request = values => new Request("https://auth.example/token", { method: "POST", body: new URLSearchParams(values) });
test("rejects arbitrary grants, redirects and oversized input before upstream", async () => {
  for (const data of [{ grant_type: "password" }, { grant_type: "authorization_code", code: "a", redirect_uri: "https://evil.example" }]) assert.equal((await worker.fetch(request(data), env)).status, 400);
  assert.equal((await worker.fetch(request({ code: "x".repeat(9000) }), env)).status, 413);
});
test("requires configured rate limiting", async () => {
  assert.equal((await worker.fetch(request({}), {})).status, 503);
  assert.equal((await worker.fetch(request({}), { ...env, TOKEN_LIMIT: { limit: async () => ({ success: false }) } })).status, 429);
});
test("fixed upstream, server secret and minimal uncached response", async () => {
  const saved = globalThis.fetch;
  globalThis.fetch = async (url, options) => {
    assert.equal(url, "https://shikimori.io/oauth/token");
    assert.equal(options.redirect, "manual");
    assert.equal(options.body.get("client_secret"), "test-secret");
    assert.equal(options.body.get("refresh_token"), "test-refresh");
    return Response.json({ access_token: "test-access", refresh_token: "test-new-refresh", expires_in: 100, secret: "must-not-leak" });
  };
  try {
    const response = await worker.fetch(request({ grant_type: "refresh_token", refresh_token: "test-refresh", client_secret: "attacker" }), env);
    assert.equal(response.headers.get("Cache-Control"), "no-store");
    assert.deepEqual(await response.json(), { access_token: "test-access", refresh_token: "test-new-refresh", expires_in: 100 });
  } finally { globalThis.fetch = saved; }
});
test("never forwards secrets to an upstream redirect", async () => {
  const saved = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async () => { calls++; return new Response(null, { status: 302, headers: { Location: "https://other.example" } }); };
  try {
    const response = await worker.fetch(request({ grant_type: "refresh_token", refresh_token: "test-refresh" }), env);
    assert.equal(response.status, 502); assert.equal(calls, 1);
    assert.deepEqual(await response.json(), { error: "upstream_redirect" });
  } finally { globalThis.fetch = saved; }
});
