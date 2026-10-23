// The connect page talks to the bearer API only; the token never leaves this origin.
export const TOKEN = /^cab_[A-Za-z0-9_-]{43}$/;
export interface SaveMeta { sha256: string; size: number; updatedAt: number; deviceId: string; deviceName: string }
export interface AppState { achievements: Record<string, number>; playtimeTotal: number; save: SaveMeta | null }
export interface Game { appid: string; name: string | null; achievements: number; playtimeMinutes: number; save: SaveMeta | null; cloud: boolean }
export interface Portal { name: string; games: Game[]; usage: { files: number; bytes: number; quota: number } }
export class Unauthorized extends Error {}

export function bearer(token: string) {
  return async <T,>(path: string, init: RequestInit = {}): Promise<T> => {
    // Omit cookies: the daemon drops Authorization when a Bedrock session cookie rides along.
    const response = await fetch(path, { ...init, cache: "no-store", credentials: "omit", headers: { ...init.headers, authorization: `Bearer ${token}` } });
    if (response.status === 401) throw new Unauthorized();
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return response.json() as Promise<T>;
  };
}

export async function loadPortal(token: string): Promise<Portal> {
  const call = bearer(token);
  const [me, state, usage, cloud] = await Promise.all([
    call<{ user: { name: string } }>("/api/me"), call<{ apps: Record<string, AppState> }>("/api/state"),
    call<Portal["usage"]>("/api/cloud/usage"), call<{ apps: Record<string, unknown> }>("/api/cloud/apps"),
  ]);
  const ids = [...new Set([...Object.keys(state.apps), ...Object.keys(cloud.apps)])];
  // Names are a nicety: a failed lookup falls back to "App <id>".
  const none = { names: {} as Record<string, string> };
  const names = ids.length ? await call<typeof none>(`/api/app-names?ids=${ids.slice(0, 500).join(",")}`).catch(() => none) : none;
  const games = ids.map(appid => {
    const app = state.apps[appid];
    return { appid, name: names.names[appid] ?? null, achievements: Object.keys(app?.achievements ?? {}).length,
      playtimeMinutes: app?.playtimeTotal ?? 0, save: app?.save ?? null, cloud: appid in cloud.apps };
  }).sort((a, b) => b.playtimeMinutes - a.playtimeMinutes || (b.save?.updatedAt ?? 0) - (a.save?.updatedAt ?? 0));
  return { name: me.user.name, games, usage };
}
