import { and, eq, isNull, BedrockError, type FunctionContext, type RouteHandler } from "bedrock";
import { creator, newToken, origin } from "./auth";
import { tokens, users } from "./schema";
import { allStates } from "./api";
import { deleteAll, usage } from "./cloud";
import { escape, page } from "./pages";
import { label, smallBody } from "./validation";

function gate(request: Request, ctx: FunctionContext): Response | null {
  if (!ctx.user) {
    const base = origin(request), url = new URL(base);
    const login = url.hostname.endsWith(".localhost") ? `${base}/_bedrock/dev-login` : `https://auth.${url.hostname.slice("cabinet.".length)}/login`;
    return new Response(null, { status: 302, headers: { location: `${login}?return=${encodeURIComponent(base + "/admin")}`, "cache-control": "no-store" } });
  }
  return creator(ctx) ? null : page("Cabinet Cloud", "<p>This account is not a Bedrock creator.</p>", 403);
}
function render(ctx: FunctionContext, token?: string): Response {
  const once = token ? `<section><h2>Connect link — shown once</h2><p>Copy it now and share it with this user. Regenerate it if lost.</p><input class="link" readonly aria-label="Connect link" value="${escape(origin(ctx.request) + "/connect#" + token)}"><a href="/admin">Done</a></section>` : "";
  const rows = ctx.db.select().from(users).all().map(user => {
    const cloud = usage(ctx, user.id);
    const active = ctx.db.select().from(tokens).where(and(eq(tokens.userId, user.id), isNull(tokens.revokedAt))).all().length;
    const summaries = Object.entries(allStates(ctx, user.id)).map(([id, state]) => `<li>App ${escape(id)} · ${Object.keys(state.achievements).length} achievements · ${Object.keys(state.stats).length} stats · ${state.playtimeTotal} min · ${state.save ? `${state.save.size} bytes saved (${escape(state.save.deviceName)})` : "no save"}</li>`).join("");
    return `<section><h2>${escape(user.name)}</h2><p>Last seen: ${user.lastSeen ? escape(new Date(user.lastSeen).toISOString()) : "Never"} · Token ${active ? "active" : "revoked"} · Steam Cloud: ${cloud.files} files, ${cloud.bytes} bytes</p><ul>${summaries || "<li>No apps yet</li>"}</ul><form method="post" action="/admin"><input type="hidden" name="userId" value="${escape(user.id)}"><button name="action" value="regenerate">Regenerate token</button><button name="action" value="revoke">Revoke token</button><button name="action" value="delete-cloud" onclick="return confirm('Delete all Steam Cloud files for this user?')">Delete cloud files</button></form></section>`;
  }).join("");
  return page("Cabinet Cloud · Admin", `${once}<section><h2>Create user</h2><form method="post" action="/admin"><label for="name">Name</label><input id="name" name="name" maxlength="200" required><button name="action" value="create">Create user</button></form></section>${rows || "<p>No users yet.</p>"}`);
}
export const adminRoutes: Record<string, RouteHandler> = {
  "GET /admin": (request, _server, ctx) => gate(request, ctx) ?? render(ctx),
  "POST /admin": async (request, _server, ctx) => {
    const denied = gate(request, ctx);
    if (denied) return denied;
    if (request.headers.get("origin") !== origin(request)) return page("Cabinet Cloud", "<p>Invalid Origin.</p>", 403);
    try {
      const form = new URLSearchParams(await smallBody(request));
      const action = form.get("action");
      if (action === "create") {
        const name = label(form.get("name")).trim(), id = crypto.randomUUID();
        ctx.db.insert(users).values({ id, name, createdAt: Date.now() }).run();
        return render(ctx, newToken(ctx, id));
      }
      const id = label(form.get("userId"));
      if (!ctx.db.select().from(users).where(eq(users.id, id)).get()) return page("Cabinet Cloud", "<p>User not found.</p>", 404);
      if (action === "delete-cloud") {
        await deleteAll(ctx, id);
        return render(ctx);
      }
      if (action !== "revoke" && action !== "regenerate") return page("Cabinet Cloud", "<p>Unknown action.</p>", 400);
      ctx.db.update(tokens).set({ revokedAt: Date.now() }).where(and(eq(tokens.userId, id), isNull(tokens.revokedAt))).run();
      return render(ctx, action === "regenerate" ? newToken(ctx, id) : undefined);
    } catch (error) {
      if (error instanceof BedrockError && error.code === "INVALID_ARGS") return page("Cabinet Cloud", `<p>${escape(error.message)}</p>`, 400);
      throw error;
    }
  },
};
