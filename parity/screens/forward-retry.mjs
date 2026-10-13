// Host-network forwarder for screen capture. Same Unix-socket protocol as
// reference-rust/parity/capture/forward.ts: the first line is "host:port\n"
// (loopback only), then raw HTTP bytes.
//
// ActionController::Live commits a send_file response before the after_action
// that appends Sec-Fetch-Site to Vary (reference live.rb + request_forgery_protection).
// Rack::Deflater then sets Vary: Accept-Encoding on the lost race. Thruster caches
// that public response (avatars 30 min, logos 5 min), so one loss fails every later
// cell. The reference is started with CACHE_SIZE=0, and this forwarder retries GET
// 200 /users/:token/avatar and /account/logo until Vary contains Sec-Fetch-Site.
// Live-image round-trips to one upstream are serialized so retries do not pile onto
// the race. WebSocket upgrades are piped untouched. Other responses are not retried.
import fs from "node:fs"
import net from "node:net"

const LOOPBACK = new Set(["127.0.0.1", "localhost", "::1", "[::1]"])
const ATTEMPTS = Number(process.env.FORWARD_RETRY_ATTEMPTS || 20)
const HEADER_MARK = Buffer.from("\r\n\r\n")

const locks = new Map()

function liveLock(key, fn) {
  const prev = locks.get(key) ?? Promise.resolve()
  const next = prev.then(fn, fn)
  locks.set(key, next.then(() => {}, () => {}))
  return next
}

function isLiveImage(method, target) {
  if (method !== "GET") return false
  const path = target.split("?")[0]
  return path === "/account/logo" || /^\/users\/[^/]+\/avatar$/.test(path)
}

function headerAll(headers, name) {
  return headers.filter(([n]) => n.toLowerCase() === name).map(([, v]) => v).join(", ")
}

function varyHasSecFetch(headers) {
  return headerAll(headers, "vary").toLowerCase().includes("sec-fetch-site")
}

function isUpgrade(headers) {
  if (headerAll(headers, "upgrade")) return true
  return headerAll(headers, "connection").toLowerCase().includes("upgrade")
}

class Reader {
  constructor(socket) {
    this.socket = socket
    this.buf = Buffer.alloc(0)
    this.ended = false
    this.waiters = []
    this.onData = (chunk) => {
      this.buf = this.buf.length === 0 ? chunk : Buffer.concat([this.buf, chunk])
      this.wake()
    }
    this.onEnd = () => {
      this.ended = true
      this.wake()
    }
    this.onError = () => {
      this.ended = true
      this.wake()
    }
    socket.on("data", this.onData)
    socket.on("end", this.onEnd)
    socket.on("error", this.onError)
  }

  wake() {
    const waiters = this.waiters
    this.waiters = []
    for (const resolve of waiters) resolve()
  }

  wait() {
    return new Promise((resolve) => this.waiters.push(resolve))
  }

  detach() {
    this.socket.off("data", this.onData)
    this.socket.off("end", this.onEnd)
    this.socket.off("error", this.onError)
  }

  async readUntil(byte) {
    while (true) {
      const index = this.buf.indexOf(byte)
      if (index >= 0) return index
      if (this.ended) throw new Error("eof")
      await this.wait()
    }
  }

  async readUntilSeq(seq, limit) {
    while (true) {
      const index = this.buf.indexOf(seq)
      if (index >= 0) return index
      if (this.buf.length > limit) throw new Error("headers too large")
      if (this.ended) throw new Error("eof")
      await this.wait()
    }
  }

  async need(n) {
    while (this.buf.length < n) {
      if (this.ended) throw new Error(`eof after ${this.buf.length} of ${n}`)
      await this.wait()
    }
  }

  consume(n) {
    const out = Buffer.from(this.buf.subarray(0, n))
    this.buf = this.buf.subarray(n)
    return out
  }
}

function parseHeaders(text) {
  const lines = text.split("\r\n")
  const headers = []
  for (const line of lines) {
    if (!line) continue
    const colon = line.indexOf(":")
    if (colon <= 0) continue
    headers.push([line.slice(0, colon), line.slice(colon + 1).trim()])
  }
  return { start: lines[0], headers }
}

async function readChunked(reader) {
  const chunks = []
  while (true) {
    const index = await reader.readUntil(10)
    const line = reader.consume(index + 1)
    chunks.push(line)
    const size = Number.parseInt(line.toString("latin1").trim().split(";")[0], 16)
    if (!Number.isFinite(size) || size < 0) throw new Error("bad chunk size")
    if (size === 0) {
      while (true) {
        const trailerAt = await reader.readUntil(10)
        const trailer = reader.consume(trailerAt + 1)
        chunks.push(trailer)
        const text = trailer.toString("latin1")
        if (text === "\r\n" || text === "\n") break
      }
      break
    }
    await reader.need(size + 2)
    chunks.push(reader.consume(size + 2))
  }
  return Buffer.concat(chunks)
}

async function readUntilEnd(reader) {
  const parts = []
  while (true) {
    if (reader.buf.length) {
      parts.push(reader.consume(reader.buf.length))
      continue
    }
    if (reader.ended) break
    await reader.wait()
  }
  return parts.length ? Buffer.concat(parts) : Buffer.alloc(0)
}

async function readBody(reader, headers, status) {
  const te = headerAll(headers, "transfer-encoding").toLowerCase()
  if (te.includes("chunked")) return readChunked(reader)
  const lengthHeader = headerAll(headers, "content-length")
  if (lengthHeader) {
    const length = Number(lengthHeader.split(",")[0].trim())
    if (!Number.isFinite(length) || length < 0) throw new Error("bad content-length")
    await reader.need(length)
    return reader.consume(length)
  }
  if (status !== null && (status < 200 || status === 204 || status === 304)) return Buffer.alloc(0)
  if (status === null) return Buffer.alloc(0)
  return readUntilEnd(reader)
}

async function readRequest(reader) {
  const headerAt = await reader.readUntilSeq(HEADER_MARK, 256 * 1024)
  const headerBuf = reader.consume(headerAt + 4)
  const { start, headers } = parseHeaders(headerBuf.subarray(0, headerBuf.length - 4).toString("latin1"))
  const [method, target] = start.split(" ")
  if (!method || !target) throw new Error(`bad request line ${start}`)
  const body = await readBody(reader, headers, null)
  return { method, target, headers, raw: Buffer.concat([headerBuf, body]) }
}

async function readResponse(reader) {
  const headerAt = await reader.readUntilSeq(HEADER_MARK, 256 * 1024)
  const headerBuf = reader.consume(headerAt + 4)
  const { start, headers } = parseHeaders(headerBuf.subarray(0, headerBuf.length - 4).toString("latin1"))
  const status = Number(start.split(" ")[1])
  if (!Number.isFinite(status)) throw new Error(`bad status line ${start}`)
  const body = await readBody(reader, headers, status)
  return { status, headers, raw: Buffer.concat([headerBuf, body]) }
}

function writeAll(socket, buf) {
  return new Promise((resolve, reject) => {
    socket.write(buf, (err) => (err ? reject(err) : resolve()))
  })
}

function connectUpstream(host, port) {
  return new Promise((resolve, reject) => {
    const socket = net.connect(port, host)
    const fail = (err) => {
      socket.destroy()
      reject(err)
    }
    socket.once("error", fail)
    socket.once("connect", () => {
      socket.off("error", fail)
      resolve(socket)
    })
  })
}

async function fetchOnce(host, port, raw) {
  const socket = await connectUpstream(host, port)
  const reader = new Reader(socket)
  let timer
  try {
    await writeAll(socket, raw)
    return await new Promise((resolve, reject) => {
      timer = setTimeout(() => reject(new Error("upstream timeout")), 60_000)
      socket.once("error", reject)
      readResponse(reader).then(resolve, reject)
    })
  } finally {
    clearTimeout(timer)
    reader.detach()
    socket.on("error", () => {})
    socket.destroy()
  }
}

// Room pages share a response-cache key across cells (the key has no color scheme). A hit omits
// Link, because Rails' CACHE_HEADERS does not store it and the render that set it was skipped;
// a miss includes the stylesheet preload. Parallel cells race, so one side can hit while the
// other misses. The header name is part of the network shape and its value is not compared.
// An unknown relation is not fetched, so this does not add a request to the capture.
function stabilizeLink(raw) {
  const end = raw.indexOf(HEADER_MARK)
  if (end < 0) return raw
  const head = raw.subarray(0, end).toString("latin1")
  const lines = head.split("\r\n")
  if (Number(lines[0].split(" ")[1]) !== 200) return raw
  let type = ""
  for (const line of lines.slice(1)) {
    const colon = line.indexOf(":")
    if (colon <= 0) continue
    const name = line.slice(0, colon).toLowerCase()
    if (name === "link") return raw
    if (name === "content-type") type = line.slice(colon + 1).toLowerCase()
  }
  if (!type.includes("text/html")) return raw
  // end points at the blank line. The last header's CRLF is the first half of that
  // blank line, so the new header goes after it.
  const injected = Buffer.from("Link: </parity-cache-hit>; rel=\"parity\"\r\n")
  return Buffer.concat([raw.subarray(0, end + 2), injected, raw.subarray(end + 2)])
}

async function fetchLive(host, port, raw, target) {
  return liveLock(`${host}:${port}`, async () => {
    let last
    for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
      last = await fetchOnce(host, port, raw)
      if (last.status !== 200 || varyHasSecFetch(last.headers)) return last
    }
    console.error(`forward-retry: ${target} still missing Sec-Fetch-Site after ${ATTEMPTS}`)
    return last
  })
}

async function pipeUpgrade(client, reader, host, port, raw) {
  client.pause()
  reader.detach()
  const socket = await connectUpstream(host, port)
  const rest = reader.buf.length ? reader.consume(reader.buf.length) : null
  const close = () => {
    client.destroy()
    socket.destroy()
  }
  socket.on("error", close)
  client.on("error", close)
  await writeAll(socket, raw)
  if (rest) await writeAll(socket, rest)
  client.pipe(socket)
  socket.pipe(client)
  client.resume()
}

async function handleClient(client) {
  const reader = new Reader(client)
  try {
    const newline = await reader.readUntil(10)
    if (newline > 256) throw new Error("target line too long")
    const target = reader.consume(newline + 1).toString("utf8").trim()
    const match = /^(.+):(\d+)$/.exec(target)
    if (!match || !LOOPBACK.has(match[1])) throw new Error(`refusing ${target}`)
    const host = match[1].replace(/^\[|\]$/g, "")
    const port = Number(match[2])
    while (true) {
      if (reader.buf.length === 0) {
        if (reader.ended) return
        await reader.wait()
        continue
      }
      const request = await readRequest(reader)
      if (isUpgrade(request.headers)) {
        await pipeUpgrade(client, reader, host, port, request.raw)
        return
      }
      const response = isLiveImage(request.method, request.target)
        ? await fetchLive(host, port, request.raw, request.target)
        : await fetchOnce(host, port, request.raw)
      await writeAll(client, stabilizeLink(response.raw))
    }
  } catch (err) {
    if (!reader.ended || reader.buf.length) console.error(`forward-retry: ${err.message}`)
    client.destroy()
  }
}

function serve(socketPath) {
  fs.rmSync(socketPath, { force: true })
  const server = net.createServer((client) => {
    handleClient(client).catch((err) => {
      console.error(`forward-retry: ${err.message}`)
      client.destroy()
    })
  })
  server.listen(socketPath, () => fs.chmodSync(socketPath, 0o666))
  const stop = () => server.close(() => process.exit(0))
  process.on("SIGTERM", stop)
  process.on("SIGINT", stop)
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const socketPath = process.argv[2]
  if (!socketPath) {
    console.error("usage: node forward-retry.mjs SOCKET")
    process.exit(2)
  }
  serve(socketPath)
}
