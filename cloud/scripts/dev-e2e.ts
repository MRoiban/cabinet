import { mkdir } from "node:fs/promises";
import { dirname, join } from "node:path";

// This is intentionally an opt-in smoke test: it creates local development data.
const dir = dirname(import.meta.dir), port = 3137;
const base = `http://cabinet.localhost:${port}`;
const config = join(dir, ".bedrock", "daemon", "config.json");
if (!await Bun.file(config).exists()) {
  await mkdir(dirname(config), { recursive: true });
  await Bun.write(config, JSON.stringify({ domain: "localhost", creators: ["owner@example.test"], port }));
}
const cli = join(dirname(Bun.resolveSync("bedrock", dir)), "cli", "index.ts");
const child = Bun.spawn([process.execPath, cli, "dev", "--port", String(port)], {
  cwd: dir, env: process.env, stdout: "pipe", stderr: "pipe",
});
let cookie = "", bearer = "";
function check(ok: boolean, message: string): asserts ok { if (!ok) throw new Error(message); }
async function request(path: string, options: RequestInit = {}) {
  const headers = new Headers(options.headers);
  headers.set("host", new URL(base).host);
  // The daemon drops Authorization when a session cookie is present: functions use the
  // creator session, /api uses the bearer.
  if (path.startsWith("/_bedrock/")) { if (cookie) headers.set("cookie", cookie); }
  else if (bearer) headers.set("authorization", `Bearer ${bearer}`);
  if (options.method && options.method !== "GET") headers.set("origin", base);
  // Windows does not always resolve wildcard localhost; Host still exercises routing.
  return fetch(`http://127.0.0.1:${port}${path}`, { ...options, headers, redirect: "manual" });
}
async function report(path: string, options: RequestInit = {}) {
  const response = await request(path, options);
  const value: unknown = await response.json();
  console.log(`${options.method ?? "GET"} ${path} -> ${response.status} ${JSON.stringify(value)}`);
  return { response, value };
}
try {
  let ready = false;
  for (let i = 0; i < 100; i++) {
    try { if ((await request("/api/me")).status === 401) { ready = true; break; } } catch {}
    if (child.exitCode !== null) break;
    await Bun.sleep(100);
  }
  check(ready, "bedrock dev did not become ready.");
  check((await report("/api/me")).response.status === 401, "Anonymous API failed.");
  const anonymous = await request("/_bedrock/q/adminOverview", { method: "POST", body: "null" });
  check(anonymous.status === 401, "Anonymous admin functions must be refused.");
  console.log("POST /_bedrock/q/adminOverview (anonymous) -> 401");
  const login = await request("/_bedrock/dev-login?return=" + encodeURIComponent(base + "/admin"), {
    method: "POST", body: new URLSearchParams({ email: "owner@example.test", name: "Owner" }),
  });
  check(login.status === 302, "Dev login failed.");
  cookie = login.headers.get("set-cookie")?.split(";")[0] ?? "";
  check(!!cookie, "Dev login did not set session.");
  console.log("POST /_bedrock/dev-login -> 302 (session cookie redacted)");
  const admin = await request("/admin"); check(admin.status === 200 && (await admin.text()).includes('id="root"'), "Admin page failed.");
  console.log("GET /admin -> 200 (web app)");
  const overview = await request("/_bedrock/q/adminOverview", { method: "POST", body: "null" });
  check(overview.status === 200, "Creator admin functions failed.");
  console.log("POST /_bedrock/q/adminOverview (creator) -> 200");
  const created = await request("/_bedrock/m/createUser", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ name: "E2E user" }) });
  check(created.status === 200, "Create user failed.");
  bearer = /\/connect#(cab_[A-Za-z0-9_-]{43})/.exec(((await created.json()) as { value: { link: string } }).value.link)?.[1] ?? "";
  check(!!bearer, "No one-time connect link.");
  console.log("POST /_bedrock/m/createUser -> 200 (one-time connect link and code redacted)");
  const me = await report("/api/me"); check(me.response.status === 200, "Authenticated API failed.");
  const userId = (me.value as { user: { id: string } }).user.id;
  for (const body of [
    { deviceId: "pc-a", deviceName: "Desktop", achievements: { FIRST: 200 }, stats: { score: 10 }, statsUpdatedAt: 200, playtimeMinutes: 30 },
    { deviceId: "pc-b", deviceName: "Laptop", achievements: { FIRST: 100, SECOND: 300 }, stats: { score: 5 }, statsUpdatedAt: 100, playtimeMinutes: 40 },
    { deviceId: "pc-a", deviceName: "Desktop", achievements: {}, playtimeMinutes: 20 },
  ]) {
    console.log("Progress body: " + JSON.stringify(body));
    check((await report("/api/apps/123/progress", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) })).response.status === 200, "Progress failed.");
  }
  check((await report("/api/apps/123/save")).response.status === 404, "Missing save failed.");
  const zip = new Uint8Array(22); zip.set([0x50, 0x4b, 0x05, 0x06]);
  const hash = new Bun.CryptoHasher("sha256").update(zip).digest("hex");
  const save = (time: number) => report(`/api/apps/123/save?deviceId=pc-a&updatedAt=${time}&sha256=${hash}`, {
    method: "PUT", headers: { "content-type": "application/zip" }, body: zip,
  });
  check((await save(500)).response.status === 200, "Upload failed.");
  const download = await request("/api/apps/123/save");
  check(download.status === 200 && download.headers.get("etag") === hash && Buffer.from(await download.arrayBuffer()).equals(Buffer.from(zip)), "Download failed.");
  console.log(`GET /api/apps/123/save -> 200 application/zip, 22 bytes match, ETag=${hash}`);
  check((await save(499)).response.status === 409, "Conflict failed.");
  await report("/api/state");
  const revoked = await request("/_bedrock/m/revokeAccess", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ id: userId }) });
  check(revoked.status === 200, "Revoke failed.");
  console.log("POST /_bedrock/m/revokeAccess -> 200");
  check((await report("/api/me")).response.status === 401, "Revocation failed.");
  console.log("Dev HTTP E2E passed. All writes sent Origin; no raw token logged.");
} finally {
  // SIGINT lets the CLI stop its daemon and pebble children before exiting.
  child.kill("SIGINT");
  await child.exited;
}
