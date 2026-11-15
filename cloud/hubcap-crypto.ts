// Hubcap keys at rest (hubcap.ts): AES-256-GCM with a per-row random IV, plus an HMAC-SHA256 digest so a
// duplicate key can be found without decrypting every row. Both subkeys are derived (HKDF-SHA256) from
// the pebble secret HUBCAP_KEY_SECRET: 32 random bytes, base64. Without a valid secret nothing is
// sealed or opened, and the relay reports itself disabled. Neither the secret nor a key is ever logged.

export const SECRET_ENV = "HUBCAP_KEY_SECRET";
export type SecretState = "ok" | "missing" | "invalid";
const AAD = new TextEncoder().encode("cabinet hubcap key v1");
const B64_32 = /^[A-Za-z0-9+/]{43}=$/;

function rawSecret(): Uint8Array | null {
  const value = process.env[SECRET_ENV]?.trim();
  if (!value || !B64_32.test(value)) return null;
  const bytes = new Uint8Array(Buffer.from(value, "base64"));
  return bytes.length === 32 ? bytes : null;
}
/** Whether the secret is set and is 32 bytes of base64. Read on every call, so a restart picks up a change. */
export function secretState(): SecretState {
  if (!process.env[SECRET_ENV]?.trim()) return "missing";
  return rawSecret() ? "ok" : "invalid";
}

type Keys = { enc: CryptoKey; mac: CryptoKey };
let derived: { source: string; keys: Promise<Keys> } | null = null;
function keys(): Promise<Keys> | null {
  const raw = rawSecret();
  if (!raw) return null;
  const source = process.env[SECRET_ENV]!.trim();
  if (derived?.source !== source) {
    derived = { source, keys: (async () => {
      const base = await crypto.subtle.importKey("raw", raw as Uint8Array<ArrayBuffer>, "HKDF", false, ["deriveKey"]);
      const hkdf = (info: string) => ({ name: "HKDF", hash: "SHA-256", salt: new Uint8Array(32), info: new TextEncoder().encode(info) });
      return {
        enc: await crypto.subtle.deriveKey(hkdf("cabinet hubcap key encryption v1"), base, { name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]),
        mac: await crypto.subtle.deriveKey(hkdf("cabinet hubcap key digest v1"), base, { name: "HMAC", hash: "SHA-256", length: 256 }, false, ["sign"]),
      };
    })() };
  }
  return derived.keys;
}

export class SecretNotSet extends Error {
  constructor() { super("HUBCAP_KEY_SECRET is not set to 32 bytes of base64."); this.name = "SecretNotSet"; }
}

const b64url = (bytes: ArrayBuffer | Uint8Array) => Buffer.from(bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes)).toString("base64url");

/** The digest that identifies a key (duplicate detection) without revealing it. */
export async function digestKey(key: string): Promise<string> {
  const k = keys();
  if (!k) throw new SecretNotSet();
  return Buffer.from(await crypto.subtle.sign("HMAC", (await k).mac, new TextEncoder().encode(key))).toString("hex");
}
/** `v1.<iv>.<ciphertext+tag>`, base64url. */
export async function sealKey(key: string): Promise<string> {
  const k = keys();
  if (!k) throw new SecretNotSet();
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const sealed = await crypto.subtle.encrypt({ name: "AES-GCM", iv, additionalData: AAD }, (await k).enc, new TextEncoder().encode(key));
  return `v1.${b64url(iv)}.${b64url(sealed)}`;
}
/** The key, or null when the secret is missing/wrong or the value was tampered with. Never throws. */
export async function openKey(sealed: string): Promise<string | null> {
  try {
    const k = keys();
    const parts = sealed.split(".");
    if (!k || parts.length !== 3 || parts[0] !== "v1") return null;
    const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv: Buffer.from(parts[1]!, "base64url"), additionalData: AAD },
      (await k).enc, Buffer.from(parts[2]!, "base64url"));
    return new TextDecoder().decode(plain);
  } catch { return null; }
}
