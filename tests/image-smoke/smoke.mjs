// Smoke test for the production image: drives a running container the way a browser would.
// run.sh starts the image (bin/boot: Thruster in front of `campfire server`) on an empty storage
// directory and then runs this against its base URL.
//
//   node tests/image-smoke/smoke.mjs http://127.0.0.1:<port>
//
// Steps, as the P08 card lists them: /up answers 200; the first visit goes to first run; the first
// run creates an account and signs in; the room page renders; a message posts and arrives over
// /cable; and an uploaded image gets its thumbnail, which must be the reference's bytes
// (vectors/storage/moon-thumb.jpg, made by the Rails app).

import assert from "node:assert/strict";
import { createHash, randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const base = new URL(process.argv[2] ?? "http://127.0.0.1:80");
const timeoutMs = 30_000;

const account = {
  name: "Smoke Tester",
  email: "smoke@example.com",
  password: "secret123456",
};
const cookies = new Map();

const steps = [];
function step(name, fn) {
  steps.push({ name, fn });
}

function cookieHeader() {
  return [...cookies].map(([name, value]) => `${name}=${value}`).join("; ");
}

function storeCookies(response) {
  for (const header of response.headers.getSetCookie()) {
    const [pair] = header.split(";");
    const index = pair.indexOf("=");
    const name = pair.slice(0, index);
    const value = pair.slice(index + 1);
    if (value === "") cookies.delete(name);
    else cookies.set(name, value);
  }
}

// One request with the cookie jar, never following redirects, so each step can check them.
async function send(path, { method = "GET", headers = {}, body, accept } = {}) {
  const requestHeaders = { ...headers, Cookie: cookieHeader() };
  if (accept) requestHeaders.Accept = accept;
  const response = await fetch(new URL(path, base), {
    method,
    headers: requestHeaders,
    body,
    redirect: "manual",
    signal: AbortSignal.timeout(timeoutMs),
  });
  storeCookies(response);
  return response;
}

// Follows redirects (GETs only). Returns the final response and the path it was served from.
async function follow(path) {
  let location = path;
  for (let hops = 0; ; hops++) {
    const response = await send(location);
    const next = response.headers.get("location");
    if (response.status < 300 || response.status >= 400 || !next || hops >= 5) {
      return { response, path: location };
    }
    const url = new URL(next, base);
    location = url.pathname + url.search;
  }
}

function csrfToken(html) {
  const match = html.match(/<meta name="csrf-token" content="([^"]+)"/);
  assert.ok(match, "the page has no csrf-token meta tag");
  return match[1];
}

async function waitForUp() {
  const deadline = Date.now() + 120_000;
  for (;;) {
    try {
      const response = await send("/up");
      if (response.status === 200) return;
    } catch {
      // The container is still starting.
    }
    assert.ok(Date.now() < deadline, "/up didn't answer 200 within 120 seconds");
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

let roomPath;
let roomHtml;

step("/up answers 200", async () => {
  await waitForUp();
  const response = await send("/up");
  assert.equal(response.status, 200);
});

step("first visit goes to first run", async () => {
  const { response, path } = await follow("/");
  assert.equal(response.status, 200);
  assert.equal(path, "/first_run");
});

step("first run creates the account and signs in", async () => {
  const page = await (await send("/first_run")).text();
  const token = page.match(/name="authenticity_token" value="([^"]+)"/);
  assert.ok(token, "the first run page has no authenticity_token");

  const body = new URLSearchParams({
    authenticity_token: token[1],
    "user[name]": account.name,
    "user[email_address]": account.email,
    "user[password]": account.password,
  });
  const response = await send("/first_run", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body,
  });
  assert.equal(response.status, 302, "first run should redirect");
  assert.ok(cookies.has("session_token"), "first run should set the session_token cookie");
});

step("the room page renders", async () => {
  const { response, path } = await follow("/");
  assert.equal(response.status, 200);
  roomPath = path;
  roomHtml = await response.text();
  assert.match(roomPath, /^\/rooms\/\d+$/, `signed in home should be a room, got ${roomPath}`);
  assert.match(roomHtml, /All Talk/, "the room page should name the first room");
});

step("a message posts and arrives over /cable", async () => {
  const streamSource = roomHtml.match(
    /<turbo-cable-stream-source channel="RoomMessagesChannel" signed-stream-name="([^"]+)"/,
  );
  assert.ok(streamSource, "the room page subscribes to no RoomMessagesChannel stream");
  const identifier = JSON.stringify({ channel: "RoomMessagesChannel", signed_stream_name: streamSource[1] });

  const socket = await openCable();
  try {
    socket.send(JSON.stringify({ command: "subscribe", identifier }));
    await socket.until((frame) => frame.type === "confirm_subscription" && frame.identifier === identifier);

    const text = `smoke ${randomBytes(6).toString("hex")}`;
    const response = await send(`${roomPath}/messages`, {
      method: "POST",
      accept: "text/vnd.turbo-stream.html",
      headers: { "X-CSRF-Token": csrfToken(roomHtml), "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        "message[body]": text,
        "message[client_message_id]": randomBytes(8).toString("hex"),
      }),
    });
    assert.equal(response.status, 200, "posting a message should answer 200");

    await socket.until((frame) => frame.identifier === identifier && String(frame.message).includes(text));
  } finally {
    socket.close();
  }
});

step("an uploaded image gets its thumbnail", async () => {
  const fixture = join(root, "reference", "test", "fixtures", "files", "moon.jpg");
  const form = new FormData();
  form.append("message[body]", "");
  form.append("message[client_message_id]", randomBytes(8).toString("hex"));
  form.append("message[attachment]", new Blob([readFileSync(fixture)], { type: "image/jpeg" }), "moon.jpg");

  const response = await send(`${roomPath}/messages`, {
    method: "POST",
    accept: "text/vnd.turbo-stream.html",
    headers: { "X-CSRF-Token": csrfToken(roomHtml) },
    body: form,
  });
  assert.equal(response.status, 200, "uploading an image should answer 200");

  const link = (await response.text()).match(/\/rails\/active_storage\/representations\/redirect\/[^"'\s<>]+/);
  assert.ok(link, "the upload's reply links no thumbnail");

  const { response: thumbnail } = await follow(link[0]);
  assert.equal(thumbnail.status, 200);
  assert.equal(thumbnail.headers.get("content-type"), "image/jpeg");

  const bytes = Buffer.from(await thumbnail.arrayBuffer());
  const expected = readFileSync(join(root, "vectors", "storage", "moon-thumb.jpg"));
  assert.equal(sha256(bytes), sha256(expected), "the thumbnail should be the reference's bytes");
});

function sha256(buffer) {
  return createHash("sha256").update(buffer).digest("hex");
}

// Connects to /cable the way a browser does (the session cookie and an Origin of the app).
function openCable() {
  const url = new URL("/cable", base);
  url.protocol = url.protocol.replace("http", "ws");
  const socket = new WebSocket(url, {
    protocols: ["actioncable-v1-json"],
    headers: { Cookie: cookieHeader(), Origin: base.origin },
  });
  const frames = [];
  const waiters = [];

  socket.addEventListener("message", (event) => {
    frames.push(JSON.parse(event.data));
    for (const waiter of [...waiters]) waiter();
  });

  const opened = new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", () => reject(new Error("the /cable connection failed")), { once: true });
  });

  return opened.then(() => ({
    send: (text) => socket.send(text),
    close: () => socket.close(),
    // Resolves with the first frame matching `predicate`, which may already have arrived.
    until(predicate) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("no matching /cable frame within 30 seconds")), timeoutMs);
        const check = () => {
          const frame = frames.find(predicate);
          if (frame) {
            clearTimeout(timer);
            waiters.splice(waiters.indexOf(check), 1);
            resolve(frame);
          }
        };
        waiters.push(check);
        check();
      });
    },
  }));
}

let failures = 0;
for (const { name, fn } of steps) {
  try {
    await fn();
    console.log(`ok   ${name}`);
  } catch (error) {
    failures++;
    console.log(`FAIL ${name}: ${error.message}`);
    break;
  }
}

if (failures > 0) {
  console.log(`image smoke test failed (${failures} failed step)`);
  process.exit(1);
}
console.log(`image smoke test passed (${steps.length} steps)`);
