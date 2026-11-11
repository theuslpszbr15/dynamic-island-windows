// End-to-end tests of the relay on a local workerd (Miniflare): the Worker and the Room Durable Object, talking to
// fake PCs and phones over real WebSockets. Run with `npm test`.

import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { createHash, generateKeyPairSync, sign, randomBytes } from "node:crypto";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { unstable_startWorker } from "wrangler";

const KEY = "Relay_key_0123456789abcdef";
let dir;

before(() => { dir = mkdtempSync(join(tmpdir(), "relay-test-")); });
after(() => rmSync(dir, { recursive: true, force: true }));

let runs = 0;

/** A fresh relay from wrangler.toml (its own storage, rooms and counters), served by a local workerd. */
async function relay(key = KEY) {
  const worker = await unstable_startWorker({
    config: fileURLToPath(new URL("../wrangler.toml", import.meta.url)),
    bindings: key ? { RELAY_KEY: { type: "plain_text", value: key } } : {},
    dev: { server: { port: 0 }, inspector: false, persist: join(dir, String(++runs)), logLevel: "none" },
  });
  await worker.ready;
  return { dispatchFetch: (url, init) => worker.fetch(url, init), dispose: () => worker.dispose() };
}

const room = (token = randomBytes(16).toString("hex")) =>
  createHash("sha256").update("dynamicisland-room|" + token).digest("hex");

let ipCounter = 0;
const newIp = () => `203.0.113.${++ipCounter % 250}`;

/** Opens a WebSocket; resolves with { ws, status, messages, next(), closed } even when the relay refuses it. */
async function open(mf, path, { bearer, ip = newIp() } = {}) {
  const headers = { Upgrade: "websocket", "CF-Connecting-IP": ip };
  if (bearer) headers.Authorization = `Bearer ${bearer}`;
  const res = await mf.dispatchFetch(`https://relay.test${path}`, { headers });
  const ws = res.webSocket;
  const socket = { ws, status: res.status, messages: [], waiters: [], close: null };
  let closedResolve;
  socket.closed = new Promise((r) => (closedResolve = r));
  if (!ws) return socket;
  ws.addEventListener("message", (e) => {
    const w = socket.waiters.shift();
    if (w) w(e.data);
    else socket.messages.push(e.data);
  });
  ws.addEventListener("close", (e) => {
    socket.close = { code: e.code, reason: e.reason };
    closedResolve(socket.close);
    for (const w of socket.waiters.splice(0)) w(null);
  });
  ws.accept();
  socket.next = (ms = 3000) =>
    socket.messages.length > 0 ? Promise.resolve(socket.messages.shift())
      : socket.close ? Promise.resolve(null)
      : new Promise((resolve, reject) => {
          const t = setTimeout(() => reject(new Error("no message")), ms);
          socket.waiters.push((m) => { clearTimeout(t); resolve(m); });
        });
  return socket;
}

const json = (m) => JSON.parse(typeof m === "string" ? m : new TextDecoder().decode(m));
const within = (p, ms = 3000) => Promise.race([p, new Promise((_, rej) => setTimeout(() => rej(new Error("timeout")), ms))]);

function pcKey() {
  const { publicKey, privateKey } = generateKeyPairSync("ed25519");
  const raw = Buffer.from(publicKey.export({ format: "jwk" }).x, "base64url").toString("hex");
  return { raw, sign: (text) => sign(null, Buffer.from(text), privateKey).toString("hex") };
}

/** A PC's control socket that answered the challenge; resolves once the relay says "ready". */
async function pc(mf, r, key = pcKey(), opts = {}) {
  const s = await open(mf, `/pc/${r}`, opts);
  const challenge = json(await s.next());
  assert.equal(challenge.type, "challenge");
  assert.match(challenge.nonce, /^[0-9a-f]{64}$/);
  const sig = opts.badSig ? "00".repeat(64) : key.sign(`dynamicisland-relay-auth|${r}|${challenge.nonce}`);
  s.ws.send(JSON.stringify({ type: "auth", key: key.raw, sig }));
  return { s, key };
}

/** A phone joined and the PC opened its data socket: the two ends of one tunnel. */
async function tunnel(mf, r, control) {
  const phone = await open(mf, `/phone/${r}`);
  const incoming = json(await control.next());
  assert.equal(incoming.type, "incoming");
  const data = await open(mf, `/pc/${r}/${incoming.conn}`);
  return { phone, data };
}

test("phones and PCs without a Bearer key are let in; a wrong key is refused with 401", async () => {
  const mf = await relay();
  try {
    const r = room();
    const { s } = await pc(mf, r);
    assert.equal(json(await s.next()).type, "ready");
    const wrong = await mf.dispatchFetch(`https://relay.test/phone/${r}`, { headers: { Upgrade: "websocket", Authorization: "Bearer nope-nope-nope-nope" } });
    assert.equal(wrong.status, 401);
    const right = await open(mf, `/phone/${r}`, { bearer: KEY });
    assert.equal(right.status, 101);
    assert.equal(json(await s.next()).type, "incoming");
  } finally {
    await mf.dispose();
  }
});

test("without RELAY_KEY a Bearer header is simply ignored", async () => {
  const mf = await relay(null);
  try {
    const r = room();
    const { s } = await pc(mf, r, pcKey(), { bearer: "whatever-whatever-1234" });
    assert.equal(json(await s.next()).type, "ready");
  } finally {
    await mf.dispose();
  }
});

test("bytes go both ways; text in the tunnel closes the pair; ping is answered", async () => {
  const mf = await relay();
  try {
    const r = room();
    const { s } = await pc(mf, r);
    await s.next(); // ready
    const { phone, data } = await tunnel(mf, r, s);
    phone.ws.send(new Uint8Array([1, 2, 3]));
    assert.deepEqual([...new Uint8Array(await data.next())], [1, 2, 3]);
    data.ws.send(new Uint8Array([9, 8]));
    assert.deepEqual([...new Uint8Array(await phone.next())], [9, 8]);
    // keepalive on a data socket: the auto-response, and the tunnel stays up
    phone.ws.send("ping");
    assert.equal(await phone.next(), "pong");
    phone.ws.send(new Uint8Array([7]));
    assert.deepEqual([...new Uint8Array(await data.next())], [7]);
    data.ws.send("hello");
    const closed = await within(phone.closed);
    assert.equal(closed.code, 4400);
  } finally {
    await mf.dispose();
  }
});

test("the first PC key is kept: another key is refused with 4401 and the real PC stays", async () => {
  const mf = await relay();
  try {
    const r = room();
    const real = await pc(mf, r);
    assert.equal(json(await real.s.next()).type, "ready");
    const intruder = await pc(mf, r); // a different key, correctly signed
    assert.equal((await within(intruder.s.closed)).code, 4401);
    assert.equal(real.s.close, null, "the real PC must not be pushed out");
    // phones still reach the real PC
    await open(mf, `/phone/${r}`);
    assert.equal(json(await real.s.next()).type, "incoming");
    // the real PC reconnecting with its key replaces its old socket
    const again = await pc(mf, r, real.key);
    assert.equal(json(await again.s.next()).type, "ready");
    assert.equal((await within(real.s.closed)).code, 4000);
  } finally {
    await mf.dispose();
  }
});

test("a wrong signature is refused with 4401, even for the right key", async () => {
  const mf = await relay();
  try {
    const r = room();
    const real = await pc(mf, r);
    await real.s.next();
    const forged = await pc(mf, r, real.key, { badSig: true });
    assert.equal((await within(forged.s.closed)).code, 4401);
    // and on a fresh room nothing is kept from a bad signature
    const r2 = room();
    const first = await pc(mf, r2, pcKey(), { badSig: true });
    assert.equal((await within(first.s.closed)).code, 4401);
    const later = await pc(mf, r2);
    assert.equal(json(await later.s.next()).type, "ready");
  } finally {
    await mf.dispose();
  }
});

test("a PC that hasn't signed isn't in the room: phones get 4404", async () => {
  const mf = await relay();
  try {
    const r = room();
    const silent = await open(mf, `/pc/${r}`);
    assert.equal(json(await silent.next()).type, "challenge");
    const phone = await open(mf, `/phone/${r}`);
    assert.equal((await within(phone.closed)).code, 4404);
  } finally {
    await mf.dispose();
  }
});

test("an island from before the challenge still works on a relay with its own key", async () => {
  const mf = await relay();
  try {
    const r = room();
    const legacy = await open(mf, `/pc/${r}`, { bearer: KEY });
    assert.equal(json(await legacy.next()).type, "challenge"); // ignored by the old island
    await open(mf, `/phone/${r}`, { bearer: KEY });
    assert.equal(json(await legacy.next()).type, "incoming");
  } finally {
    await mf.dispose();
  }
});

test("more than 30 new connections a minute in one room: 4429 rate limit", async () => {
  const mf = await relay();
  try {
    const r = room();
    const { s } = await pc(mf, r);
    await s.next();
    let refused = null;
    for (let i = 0; i < 35 && !refused; i++) {
      const phone = await open(mf, `/phone/${r}`);
      const closed = await Promise.race([phone.closed, new Promise((res) => setTimeout(() => res(null), 50))]);
      if (closed && closed.code === 4429 && closed.reason === "rate limit") refused = i;
      else phone.ws.close(1000);
    }
    assert.notEqual(refused, null);
    assert.ok(refused <= 30, `refused at ${refused}`);
  } finally {
    await mf.dispose();
  }
});

test("more than 60 new connections a minute from one address: 4429 rate limit", async () => {
  const mf = await relay();
  try {
    const ip = "198.51.100.7";
    let refusedAt = null;
    for (let i = 0; i < 70 && refusedAt === null; i++) {
      const s = await open(mf, `/pc/${room()}`, { ip }); // a different room each time
      const first = await s.next().catch(() => null);
      if (first === null && s.close?.code === 4429) refusedAt = i;
      else s.ws.close(1000);
    }
    assert.equal(refusedAt, 60);
  } finally {
    await mf.dispose();
  }
});

test("50 MB a day per room: the tunnel is closed with 4429 and new phones are refused", async () => {
  const mf = await relay();
  try {
    const r = room();
    const { s } = await pc(mf, r);
    await s.next();
    const { phone, data } = await tunnel(mf, r, s);
    const chunk = new Uint8Array(64 * 1024);
    let received = 0;
    data.ws.addEventListener("message", (e) => { received += e.data.byteLength ?? 0; });
    for (let i = 0; i < 801 && !phone.close; i++) {
      phone.ws.send(chunk);
      if (i % 50 === 0) await new Promise((res) => setTimeout(res, 0));
    }
    const closed = await within(phone.closed, 10_000);
    assert.equal(closed.code, 4429);
    assert.equal(closed.reason, "daily limit");
    assert.ok(received <= 50 * 1024 * 1024);
    const late = await open(mf, `/phone/${r}`);
    const refused = await within(late.closed);
    assert.equal(refused.code, 4429);
    assert.equal(refused.reason, "daily limit");
  } finally {
    await mf.dispose();
  }
});
