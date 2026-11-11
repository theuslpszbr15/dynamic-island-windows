// Dynamic Island relay: a blind pipe between the PC and the phone when they are not on the same network.
//
// Everything that matters stays end to end: the phone runs TLS against the PC's pinned certificate and sends the
// pairing token inside it, exactly as on the LAN. This Worker only sees the room name (a SHA-256 of the token,
// which doesn't reveal it) and opaque TLS bytes, so it can't read, approve or forge anything. It logs nothing.
//
//   wss://<host>/pc/<room>          the PC's control socket (one per room; a new one replaces the old)
//   wss://<host>/phone/<room>       a phone joining; the PC is told {"type":"incoming","conn":"<id>"}
//   wss://<host>/pc/<room>/<id>     the PC's data socket for that phone
//
// Data sockets carry binary frames only: chunks of the TCP stream, no boundaries. A text frame there closes the
// pair. Close codes: 4400 bad request, 4404 the PC isn't in the room, 4429 room full.
// Every connection needs "Authorization: Bearer <RELAY_KEY>"; without it the answer is HTTP 401, no upgrade.

import { DurableObject } from "cloudflare:workers";

export interface Env {
  ROOMS: DurableObjectNamespace<Room>;
  /** Access key (secret): every connection must send "Authorization: Bearer <key>". Keeps strangers from using
   * the relay as a free tunnel; set with `npx wrangler secret put RELAY_KEY`. Without it the relay refuses all. */
  RELAY_KEY?: string;
}

/** Constant-time check of the Authorization header against RELAY_KEY. */
function authorized(request: Request, env: Env): boolean {
  const expected = env.RELAY_KEY ?? "";
  if (expected.length < 16) return false; // no key configured: closed, not open
  const header = request.headers.get("Authorization") ?? "";
  const given = header.startsWith("Bearer ") ? header.slice(7) : "";
  const a = new TextEncoder().encode(given);
  const b = new TextEncoder().encode(expected);
  if (a.byteLength !== b.byteLength) {
    crypto.subtle.timingSafeEqual(b, b); // same work either way
    return false;
  }
  return crypto.subtle.timingSafeEqual(a, b);
}

const PATH = /^\/(pc|phone)\/([0-9a-f]{64})(?:\/([0-9a-f]{32}))?$/;
const MAX_PHONES = 6; // per room, waiting or paired
const MAX_FRAME = 64 * 1024; // bytes per WebSocket message
const MAX_BUFFER = 64 * 1024; // phone bytes held while the PC's data socket is on its way
const PAIR_TIMEOUT_MS = 15_000; // a phone whose PC never opens the data socket is dropped

type Attachment =
  | { kind: "control" }
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

/** Accepts the socket only to close it with a code the app understands, then answers 101. */
function refuse(code: number, reason: string): Response {
  const [client, server] = Object.values(new WebSocketPair());
  server.accept();
  server.close(code, reason);
  return new Response(null, { status: 101, webSocket: client });
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    if (!isUpgrade(request)) return new Response("Dynamic Island relay\n", { status: 426, headers: { "content-type": "text/plain" } });
    // Before the upgrade and before anything touches a room.
    if (!authorized(request, env)) return new Response("unauthorized\n", { status: 401, headers: { "content-type": "text/plain" } });
    const r = route(new URL(request.url));
    if (!r) return refuse(4400, "bad request");
    const stub = env.ROOMS.get(env.ROOMS.idFromName(r.room));
    return stub.fetch(request);
  },
} satisfies ExportedHandler<Env>;

/** One room per pairing token. Uses WebSocket hibernation: idle rooms cost nothing. */
export class Room extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    // Keepalives ("ping" from the PC's control socket) are answered without waking the object.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(request: Request): Promise<Response> {
    const r = route(new URL(request.url));
    if (!isUpgrade(request) || !r) return refuse(4400, "bad request");
    const [client, server] = Object.values(new WebSocketPair());

    if (r.role === "pc" && !r.conn) {
      for (const old of this.ctx.getWebSockets("control")) safeClose(old, 4000, "replaced");
      this.ctx.acceptWebSocket(server, ["control"]);
      server.serializeAttachment({ kind: "control" } satisfies Attachment);
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
      const control = this.ctx.getWebSockets("control");
      if (control.length === 0) return refuse(4404, "pc offline");
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
    const a = ws.deserializeAttachment() as Attachment | null;
    if (!a || a.kind === "control") return; // the PC has nothing to say on its control socket besides pings
    if (typeof message === "string") return this.closePair(a.conn, 4400, "binary frames only");
    if (message.byteLength > MAX_FRAME) return this.closePair(a.conn, 1009, "frame too large");

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

  async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    safeClose(ws, sendableCode(code), reason);
    await this.closeSide(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    await this.closeSide(ws);
  }

  /** Drops phones whose PC never opened the data socket. */
  async alarm(): Promise<void> {
    const now = Date.now();
    let next = Infinity;
    for (const phone of this.ctx.getWebSockets("phone")) {
      const a = phone.deserializeAttachment() as Attachment | null;
      if (!a || a.kind !== "phone" || this.ctx.getWebSockets(`pc:${a.conn}`).length > 0) continue;
      const due = a.at + PAIR_TIMEOUT_MS;
      if (due <= now) await this.closePair(a.conn, 4404, "pc did not answer");
      else next = Math.min(next, due);
    }
    if (next !== Infinity) await this.ctx.storage.setAlarm(next);
  }

  private async scheduleCleanup(at: number): Promise<void> {
    const current = await this.ctx.storage.getAlarm();
    if (current === null || current > at) await this.ctx.storage.setAlarm(at);
  }

  private async closeSide(ws: WebSocket): Promise<void> {
    const a = ws.deserializeAttachment() as Attachment | null;
    if (a && a.kind !== "control") await this.closePair(a.conn, 1000, "peer closed");
  }

  /** Closing either side of a tunnel closes the other, and forgets anything held for it. */
  private async closePair(conn: string, code: number, reason: string): Promise<void> {
    for (const ws of [...this.ctx.getWebSockets(`phone:${conn}`), ...this.ctx.getWebSockets(`pc:${conn}`)]) safeClose(ws, code, reason);
    await this.ctx.storage.delete(bufferKey(conn));
  }
}

function bufferKey(conn: string): string {
  return `buf:${conn}`;
}

function newConnId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
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
