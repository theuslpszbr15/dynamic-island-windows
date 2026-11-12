// Dynamic Island relay: a blind pipe between the PC and the phone when they are not on the same network.
// (It also does the Discord token exchange for the island's Discord app: see "Discord token exchange" below.)
//
// Everything that matters stays end to end: the phone runs TLS against the PC's pinned certificate and sends the
// pairing token inside it, exactly as on the LAN. This Worker only sees the room name (a SHA-256 of the token,
// which doesn't reveal it) and opaque TLS bytes, so it can't read, approve or forge anything. It logs nothing.
//
//   wss://<host>/pc/<room>          the PC's control socket (one per room; a new one replaces the old once it proves
//                                   it holds the room's key, see below)
//   wss://<host>/phone/<room>       a phone joining; the PC is told {"type":"incoming","conn":"<id>"}
//   wss://<host>/pc/<room>/<id>     the PC's data socket for that phone
//
// Data sockets carry binary frames only: chunks of the TCP stream, no boundaries. A text frame there closes the
// pair; the one exception is the keepalive "ping", which the runtime answers "pong" without waking the room.
//
// The room's PC is trusted on first use: the control socket gets {"type":"challenge","nonce":"<hex>"} and must answer
// {"type":"auth","key":"<Ed25519 public key, hex>","sig":"<hex>"} signing "dynamicisland-relay-auth|<room>|<nonce>".
// The first key a room sees is kept; after that only that key gets in (or the socket is closed with 4401), so someone
// who learns the room name can't push the real PC out. "Parear de novo" on the PC means a new token, a new room and
// a new key.
//
// Access key: optional. With the RELAY_KEY secret set, a client that sends "Authorization: Bearer <key>" must send the
// right one (HTTP 401 otherwise); clients that send nothing are let in, so the relay works for anyone who pairs.
//
// Limits, so a public relay stays cheap: per room 50 MB of tunnel a day (both directions) and 30 new connections a
// minute; per IP address a rate limit. Over a limit the socket is closed with 4429 and a reason.
// Close codes: 4400 bad request, 4401 the PC didn't prove the room's key, 4404 the PC isn't in the room,
// 4429 a limit (reason "room full", "daily limit" or "rate limit").

import { DurableObject } from "cloudflare:workers";

export interface Env {
  ROOMS: DurableObjectNamespace<Room>;
  /** Optional access key (secret). When set, a client that sends a Bearer key must send this one. */
  RELAY_KEY?: string;
  /** Cloudflare's Rate Limiting binding, keyed by IP (see wrangler.toml). Without it a rough per-isolate count is used. */
  PER_IP?: RateLimit;
  /** The island's Discord application (secrets): see discordToken. Without them /discord/* answers 503. */
  DISCORD_CLIENT_ID?: string;
  DISCORD_CLIENT_SECRET?: string;
}

const PATH = /^\/(pc|phone)\/([0-9a-f]{64})(?:\/([0-9a-f]{32}))?$/;
const MAX_PHONES = 6; // per room, waiting or paired
const MAX_FRAME = 64 * 1024; // bytes per WebSocket message
const MAX_BUFFER = 64 * 1024; // phone bytes held while the PC's data socket is on its way
const PAIR_TIMEOUT_MS = 15_000; // a phone whose PC never opens the data socket is dropped
const AUTH_TIMEOUT_MS = 10_000; // a control socket that doesn't sign the challenge in time is dropped
const MAX_UNVERIFIED = 4; // control sockets still on the challenge, per room
const DAILY_BYTES = 50 * 1024 * 1024; // tunnel bytes per room per UTC day, both directions
const ROOM_CONNECTIONS_PER_MINUTE = 30;
const IP_CONNECTIONS_PER_MINUTE = 60; // the same number goes in wrangler.toml
const USAGE_SAVE_STEP = 1024 * 1024; // the day's count is written to storage every MB (and when a socket closes)
const AUTH_PREFIX = "dynamicisland-relay-auth|";
/** Set by the Worker (never by the client) when the request carried the right Bearer key. */
const KEYED_HEADER = "X-Relay-Keyed";

type Attachment =
  | { kind: "control"; room: string; nonce: string; at: number; verified: boolean; signed: boolean }
  | { kind: "phone"; conn: string; at: number }
  | { kind: "pc"; conn: string };

type Route = { role: "pc" | "phone"; room: string; conn?: string };

function route(url: URL): Route | null {
  const m = PATH.exec(url.pathname);
  if (!m || url.search) return null;
  const route: Route = { role: m[1] as "pc" | "phone", room: m[2], conn: m[3] };
  if (route.role === "phone" && route.conn) return null;
  return route;
}

function isUpgrade(request: Request): boolean {
  return request.headers.get("Upgrade")?.toLowerCase() === "websocket";
}

/** "none" (no Bearer, or no RELAY_KEY to check it against), "ok" (the right key) or "bad". Constant time. */
function keyCheck(request: Request, env: Env): "none" | "ok" | "bad" {
  const header = request.headers.get("Authorization") ?? "";
  if (!header.startsWith("Bearer ")) return "none";
  const expected = env.RELAY_KEY ?? "";
  if (expected.length < 16) return "none"; // no key configured: a Bearer means nothing here
  const a = new TextEncoder().encode(header.slice(7));
  const b = new TextEncoder().encode(expected);
  if (a.byteLength !== b.byteLength) {
    crypto.subtle.timingSafeEqual(b, b); // same work either way
    return "bad";
  }
  return crypto.subtle.timingSafeEqual(a, b) ? "ok" : "bad";
}

/** Accepts the socket only to close it with a code the app understands, then answers 101. */
function refuse(code: number, reason: string): Response {
  const [client, server] = Object.values(new WebSocketPair());
  server.accept();
  server.close(code, reason);
  return new Response(null, { status: 101, webSocket: client });
}

// Fallback when the PER_IP binding isn't there: a per-isolate count, rough (each isolate counts on its own) but
// enough to slow down one address hammering the same location.
const ipWindows = new Map<string, { minute: number; count: number }>();

async function ipAllowed(request: Request, env: Env): Promise<boolean> {
  const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
  if (env.PER_IP) {
    try {
      return (await env.PER_IP.limit({ key: ip })).success;
    } catch {
      // the binding failed: fall through to the local count rather than lock everyone out
    }
  }
  const minute = Math.floor(Date.now() / 60_000);
  if (ipWindows.size > 10_000) for (const [k, w] of ipWindows) if (w.minute !== minute) ipWindows.delete(k);
  const w = ipWindows.get(ip);
  if (!w || w.minute !== minute) {
    ipWindows.set(ip, { minute, count: 1 });
    return true;
  }
  return ++w.count <= IP_CONNECTIONS_PER_MINUTE;
}

// ───────────────────────── Discord token exchange ─────────────────────────
//
// The island shows who joins the user's Discord call through the Discord app on the PC (local RPC). Discord hands the
// island an authorization code; turning it into a token needs the application's client secret, which must not ship
// inside the island. So the island posts the code here and this Worker adds the secret:
//
//   GET  /discord/app     {"client_id":"…"}                          which app the island should ask Discord for
//   POST /discord/token   {"grant_type":"authorization_code","code":"…"}  or  {"grant_type":"refresh_token","refresh_token":"…"}
//                         → {"access_token","refresh_token","expires_in","scope","token_type"} or {"error":"…"}
//
// Nothing is stored or logged; codes and tokens only pass through. A code is useless without the user's consent in
// their own Discord, and only the app's owner and testers can use RPC at all, so the endpoint gives nobody new access.

const DISCORD_TOKEN_URL = "https://discord.com/api/v10/oauth2/token";
/** Must be listed under OAuth2 › Redirects of the app; the code exchange has to name one. */
const DISCORD_REDIRECT = "http://localhost";
const DISCORD_FIELD = /^[A-Za-z0-9._~-]{8,512}$/;
const DISCORD_CLIENT_ID = /^[0-9]{15,22}$/;
const DISCORD_MAX_BODY = 4096;

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", "cache-control": "no-store" },
  });
}

async function discord(request: Request, env: Env, path: string): Promise<Response> {
  const clientId = env.DISCORD_CLIENT_ID ?? "";
  const secret = env.DISCORD_CLIENT_SECRET ?? "";
  if (!DISCORD_CLIENT_ID.test(clientId) || secret.length < 16) return json(503, { error: "not_configured" });
  if (path === "/discord/app") {
    if (request.method !== "GET") return json(405, { error: "method" });
    return json(200, { client_id: clientId });
  }
  if (request.method !== "POST") return json(405, { error: "method" });
  if (!(await ipAllowed(request, env))) return json(429, { error: "rate_limit" });
  const text = await request.text();
  if (text.length > DISCORD_MAX_BODY) return json(413, { error: "too_large" });
  let body: { grant_type?: unknown; code?: unknown; refresh_token?: unknown };
  try {
    body = JSON.parse(text);
  } catch {
    return json(400, { error: "bad_request" });
  }
  const form = new URLSearchParams({ client_id: clientId, client_secret: secret });
  if (body?.grant_type === "authorization_code" && typeof body.code === "string" && DISCORD_FIELD.test(body.code)) {
    form.set("grant_type", "authorization_code");
    form.set("code", body.code);
    form.set("redirect_uri", DISCORD_REDIRECT);
  } else if (body?.grant_type === "refresh_token" && typeof body.refresh_token === "string" && DISCORD_FIELD.test(body.refresh_token)) {
    form.set("grant_type", "refresh_token");
    form.set("refresh_token", body.refresh_token);
  } else {
    return json(400, { error: "bad_request" });
  }
  let res: Response;
  let data: Record<string, unknown>;
  try {
    res = await fetch(DISCORD_TOKEN_URL, {
      method: "POST",
      body: form,
      headers: { "content-type": "application/x-www-form-urlencoded" },
    });
    data = (await res.json()) as Record<string, unknown>;
  } catch {
    return json(502, { error: "upstream" });
  }
  if (!res.ok || typeof data.access_token !== "string") {
    // Discord's own error name (invalid_grant…) helps the island word the message; nothing else is passed on.
    const error = typeof data.error === "string" && /^[a-z_]{1,40}$/.test(data.error) ? data.error : "invalid_grant";
    return json(res.status === 429 ? 429 : 400, { error });
  }
  return json(200, {
    access_token: data.access_token,
    refresh_token: typeof data.refresh_token === "string" ? data.refresh_token : undefined,
    expires_in: typeof data.expires_in === "number" ? data.expires_in : undefined,
    scope: typeof data.scope === "string" ? data.scope : undefined,
    token_type: typeof data.token_type === "string" ? data.token_type : undefined,
  });
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const path = new URL(request.url).pathname;
    if (path === "/discord/app" || path === "/discord/token") return discord(request, env, path);
    if (!isUpgrade(request)) return new Response("Dynamic Island relay\n", { status: 426, headers: { "content-type": "text/plain" } });
    // Before the upgrade and before anything touches a room.
    const key = keyCheck(request, env);
    if (key === "bad") return new Response("unauthorized\n", { status: 401, headers: { "content-type": "text/plain" } });
    const r = route(new URL(request.url));
    if (!r) return refuse(4400, "bad request");
    if (!(await ipAllowed(request, env))) return refuse(4429, "rate limit");
    const headers = new Headers(request.headers);
    headers.delete(KEYED_HEADER);
    if (key === "ok") headers.set(KEYED_HEADER, "1");
    const stub = env.ROOMS.get(env.ROOMS.idFromName(r.room));
    return stub.fetch(new Request(request, { headers }));
  },
} satisfies ExportedHandler<Env>;

type Usage = { day: string; bytes: number };

/** One room per pairing token. Uses WebSocket hibernation: idle rooms cost nothing. */
export class Room extends DurableObject<Env> {
  /** Today's tunnel bytes; `saved` is what storage has. Reloaded after hibernation. */
  private usage: (Usage & { saved: number }) | null = null;
  private connections = { minute: 0, count: 0 };

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    // Keepalives ("ping" on any socket) are answered without waking the object.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(request: Request): Promise<Response> {
    const r = route(new URL(request.url));
    if (!isUpgrade(request) || !r) return refuse(4400, "bad request");
    if (!this.takeConnection()) return refuse(4429, "rate limit");
    const [client, server] = Object.values(new WebSocketPair());

    if (r.role === "pc" && !r.conn) {
      // Still on the challenge: a flood of these can't pile up (the oldest goes) nor replace the verified one.
      const waiting = this.controls().filter((ws) => !isVerified(ws));
      if (waiting.length >= MAX_UNVERIFIED) safeClose(waiting[0], 4401, "too many waiting");
      const nonce = hex(crypto.getRandomValues(new Uint8Array(32)));
      this.ctx.acceptWebSocket(server, ["control"]);
      const a: Attachment = { kind: "control", room: r.room, nonce, at: Date.now(), verified: false, signed: false };
      // A PC with the relay's own key, in a room that never saw a signature, is let in at once: islands from before
      // the challenge keep working on a private relay. It still gets the challenge, and a key it signs is kept.
      if (request.headers.get(KEYED_HEADER) === "1" && !(await this.ctx.storage.get<string>("pcKey"))) {
        a.verified = true;
        this.replaceOthers(server);
      }
      server.serializeAttachment(a);
      server.send(JSON.stringify({ type: "challenge", nonce }));
      await this.scheduleCleanup(a.at + AUTH_TIMEOUT_MS);
    } else if (r.role === "pc" && r.conn) {
      const conn = r.conn;
      if (this.ctx.getWebSockets(`phone:${conn}`).length === 0 || this.ctx.getWebSockets(`pc:${conn}`).length > 0)
        return refuse(4400, "unknown connection");
      this.ctx.acceptWebSocket(server, ["pc", `pc:${conn}`]);
      server.serializeAttachment({ kind: "pc", conn } satisfies Attachment);
      // Bytes the phone sent before the PC arrived (its TLS hello).
      const held = await this.ctx.storage.get<ArrayBuffer[]>(bufferKey(conn));
      if (held) {
        for (const chunk of held) server.send(chunk);
        await this.ctx.storage.delete(bufferKey(conn));
      }
    } else {
      const control = this.controls().filter(isVerified);
      if (control.length === 0) return refuse(4404, "pc offline");
      if (await this.overQuota()) return refuse(4429, "daily limit");
      if (this.ctx.getWebSockets("phone").length >= MAX_PHONES) return refuse(4429, "room full");
      const conn = newConnId();
      this.ctx.acceptWebSocket(server, ["phone", `phone:${conn}`]);
      server.serializeAttachment({ kind: "phone", conn, at: Date.now() } satisfies Attachment);
      for (const pc of control) pc.send(JSON.stringify({ type: "incoming", conn }));
      await this.scheduleCleanup(Date.now() + PAIR_TIMEOUT_MS);
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const a = attachment(ws);
    if (!a) return;
    if (a.kind === "control") return this.controlMessage(ws, a, message);
    if (typeof message === "string") return this.closePair(a.conn, 4400, "binary frames only");
    if (message.byteLength > MAX_FRAME) return this.closePair(a.conn, 1009, "frame too large");
    if (await this.count(message.byteLength)) return this.closePair(a.conn, 4429, "daily limit");

    const partner = this.ctx.getWebSockets(a.kind === "phone" ? `pc:${a.conn}` : `phone:${a.conn}`)[0];
    if (partner) {
      partner.send(message);
      return;
    }
    if (a.kind !== "phone") return this.closePair(a.conn, 1000, "phone gone");
    // The PC's data socket isn't there yet: hold the bytes (in storage, so they survive hibernation).
    const key = bufferKey(a.conn);
    const held = (await this.ctx.storage.get<ArrayBuffer[]>(key)) ?? [];
    const size = held.reduce((total, chunk) => total + chunk.byteLength, 0) + message.byteLength;
    if (size > MAX_BUFFER) return this.closePair(a.conn, 1009, "too much before the pc answered");
    held.push(message);
    await this.ctx.storage.put(key, held);
  }

  /** The control socket only ever says one thing besides pings: its signature on the challenge. */
  private async controlMessage(ws: WebSocket, a: Extract<Attachment, { kind: "control" }>, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string" || a.signed || message.length > 1024) return;
    let auth: { type?: unknown; key?: unknown; sig?: unknown };
    try {
      auth = JSON.parse(message);
    } catch {
      return;
    }
    if (auth?.type !== "auth") return;
    const key = typeof auth.key === "string" ? auth.key : "";
    const sig = typeof auth.sig === "string" ? auth.sig : "";
    const known = await this.ctx.storage.get<string>("pcKey");
    const ok = /^[0-9a-f]{64}$/.test(key) && /^[0-9a-f]{128}$/.test(sig) && (known === undefined || known === key) &&
      (await verifyEd25519(key, sig, `${AUTH_PREFIX}${a.room}|${a.nonce}`));
    if (!ok) return safeClose(ws, 4401, "wrong key");
    if (known === undefined) await this.ctx.storage.put("pcKey", key); // trust on first use
    const wasVerified = a.verified;
    a.verified = true;
    a.signed = true;
    ws.serializeAttachment(a);
    if (!wasVerified) this.replaceOthers(ws);
    ws.send(JSON.stringify({ type: "ready" }));
  }

  async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    safeClose(ws, sendableCode(code), reason);
    await this.closeSide(ws);
    await this.saveUsage();
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    await this.closeSide(ws);
    await this.saveUsage();
  }

  /** Drops phones whose PC never opened the data socket, and control sockets that never signed. */
  async alarm(): Promise<void> {
    const now = Date.now();
    let next = Infinity;
    for (const phone of this.ctx.getWebSockets("phone")) {
      const a = attachment(phone);
      if (!a || a.kind !== "phone" || this.ctx.getWebSockets(`pc:${a.conn}`).length > 0) continue;
      const due = a.at + PAIR_TIMEOUT_MS;
      if (due <= now) await this.closePair(a.conn, 4404, "pc did not answer");
      else next = Math.min(next, due);
    }
    for (const ws of this.controls()) {
      const a = attachment(ws);
      if (!a || a.kind !== "control" || a.verified) continue;
      const due = a.at + AUTH_TIMEOUT_MS;
      if (due <= now) safeClose(ws, 4401, "no signature");
      else next = Math.min(next, due);
    }
    if (next !== Infinity) await this.ctx.storage.setAlarm(next);
  }

  private controls(): WebSocket[] {
    return this.ctx.getWebSockets("control");
  }

  private replaceOthers(keep: WebSocket): void {
    for (const old of this.controls()) if (old !== keep) safeClose(old, 4000, "replaced");
  }

  /** Counts a new connection in this minute; false over the room's limit. Kept in memory: a burst keeps the room awake. */
  private takeConnection(): boolean {
    const minute = Math.floor(Date.now() / 60_000);
    if (this.connections.minute !== minute) this.connections = { minute, count: 0 };
    return ++this.connections.count <= ROOM_CONNECTIONS_PER_MINUTE;
  }

  private async loadUsage(): Promise<Usage & { saved: number }> {
    const day = new Date().toISOString().slice(0, 10);
    if (!this.usage) {
      const stored = await this.ctx.storage.get<Usage>("usage");
      this.usage = stored?.day === day ? { ...stored, saved: stored.bytes } : { day, bytes: 0, saved: 0 };
    }
    if (this.usage.day !== day) this.usage = { day, bytes: 0, saved: -1 };
    return this.usage;
  }

  private async overQuota(): Promise<boolean> {
    return (await this.loadUsage()).bytes >= DAILY_BYTES;
  }

  /** Adds tunnel bytes to today's count; true once the room is over its daily limit. */
  private async count(bytes: number): Promise<boolean> {
    const u = await this.loadUsage();
    u.bytes += bytes;
    const over = u.bytes > DAILY_BYTES;
    if (over || u.bytes - u.saved >= USAGE_SAVE_STEP) await this.saveUsage();
    return over;
  }

  private async saveUsage(): Promise<void> {
    const u = this.usage;
    if (!u || u.bytes === u.saved) return;
    await this.ctx.storage.put("usage", { day: u.day, bytes: u.bytes } satisfies Usage);
    u.saved = u.bytes;
  }

  private async scheduleCleanup(at: number): Promise<void> {
    const current = await this.ctx.storage.getAlarm();
    if (current === null || current > at) await this.ctx.storage.setAlarm(at);
  }

  private async closeSide(ws: WebSocket): Promise<void> {
    const a = attachment(ws);
    if (a && a.kind !== "control") await this.closePair(a.conn, 1000, "peer closed");
  }

  /** Closing either side of a tunnel closes the other, and forgets anything held for it. */
  private async closePair(conn: string, code: number, reason: string): Promise<void> {
    for (const ws of [...this.ctx.getWebSockets(`phone:${conn}`), ...this.ctx.getWebSockets(`pc:${conn}`)]) safeClose(ws, code, reason);
    await this.ctx.storage.delete(bufferKey(conn));
  }
}

function attachment(ws: WebSocket): Attachment | null {
  return ws.deserializeAttachment() as Attachment | null;
}

async function verifyEd25519(keyHex: string, sigHex: string, message: string): Promise<boolean> {
  try {
    const key = await crypto.subtle.importKey("raw", unhex(keyHex), { name: "Ed25519" }, false, ["verify"]);
    return await crypto.subtle.verify({ name: "Ed25519" }, key, unhex(sigHex), new TextEncoder().encode(message));
  } catch {
    return false;
  }
}

function bufferKey(conn: string): string {
  return `buf:${conn}`;
}

function hex(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

function unhex(text: string): Uint8Array {
  const out = new Uint8Array(text.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(text.substr(i * 2, 2), 16);
  return out;
}

function newConnId(): string {
  return hex(crypto.getRandomValues(new Uint8Array(16)));
}

// 1005/1006/1015 and other reserved codes can't be sent in a close frame.
function sendableCode(code: number): number {
  return code === 1000 || (code >= 3000 && code <= 4999) ? code : 1000;
}

function safeClose(ws: WebSocket, code: number, reason: string): void {
  try {
    ws.close(code, reason);
  } catch {
    // already closed
  }
}

function isVerified(ws: WebSocket): boolean {
  const a = attachment(ws);
  return a?.kind === "control" && a.verified;
}
