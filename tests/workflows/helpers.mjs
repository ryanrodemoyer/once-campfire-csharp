import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Load labels if available
let LABELS = {};
try {
  const labelsPath = path.resolve(__dirname, "../../parity/.seed/default/labels.json");
  if (fs.existsSync(labelsPath)) {
    LABELS = JSON.parse(fs.readFileSync(labelsPath, "utf8"));
  }
} catch {
  // fallback if file not read
}

export const FIXTURES = {
  rooms: {
    designers: LABELS["rooms.designers"] || 654632876,
    hq: LABELS["rooms.hq"] || 201306877,
  },
  messages: {
    first: "0001",
    second: "0002",
    third: "0003",
  },
  boosts: {
    first: LABELS["boosts.first"] || 309456473,
  },
  users: {
    david: { email: "david@37signals.com", password: "secret123456", name: "David" },
    kevin: { email: "kevin@37signals.com", password: "secret123456", name: "Kevin" },
    jz: { email: "jz@37signals.com", password: "secret123456", name: "JZ" },
    jason: { email: "jason@37signals.com", password: "secret123456", name: "Jason" },
  },
};

export async function createBrowser(options = {}) {
  const launchOptions = {
    headless: true,
    channel: process.env.CHROME_CHANNEL || "chrome",
    ...options,
  };
  if (process.env.CHROME_BIN) {
    launchOptions.executablePath = process.env.CHROME_BIN;
    delete launchOptions.channel;
  }
  return await chromium.launch(launchOptions);
}

export async function createSession(browser, viewport = { width: 1400, height: 900 }) {
  const context = await browser.newContext({ viewport });
  const page = await context.newPage();
  return { context, page };
}

const COOKIE_CACHE = new Map();

export function clearCookieCache() {
  COOKIE_CACHE.clear();
}

export async function signIn(page, baseUrl, email, password = "secret123456") {
  const context = page.context();
  const cacheKey = `${baseUrl}:${email}`;
  const cachedCookies = COOKIE_CACHE.get(cacheKey);

  if (cachedCookies && cachedCookies.length > 0) {
    await context.addCookies(cachedCookies);
    await page.goto(`${baseUrl}/`);
    const loggedIn = await page.waitForSelector('a.btn:has-text("Designers"), .rooms a:has-text("Designers")', { timeout: 3000 }).catch(() => null);
    if (loggedIn) {
      return;
    }
  }

  await page.goto(`${baseUrl}/session/new`);
  await page.fill('input[name="email_address"]', email);
  await page.fill('input[name="password"]', password);
  await Promise.all([
    page.waitForNavigation().catch(() => {}),
    page.click('button[name="log_in"]'),
  ]);
  // Wait for navigation and authenticated page indicator (room links in sidebar)
  await page.waitForSelector('a.btn:has-text("Designers"), .rooms a:has-text("Designers")', { timeout: 10000 });

  const cookies = await context.cookies();
  if (cookies && cookies.length > 0) {
    COOKIE_CACHE.set(cacheKey, cookies);
  }
}

export async function joinRoom(page, baseUrl, roomId) {
  await page.goto(`${baseUrl}/rooms/${roomId}`);
  // Wait for composer and turbo-cable-stream-source
  await page.waitForSelector("#composer lexxy-editor", { state: "attached", timeout: 10000 });
  await page.waitForSelector("turbo-cable-stream-source", { state: "attached", timeout: 10000 }).catch(() => {});
  // Dismiss PWA install prompt if visible
  await dismissPwaPrompt(page);
}

export async function dismissPwaPrompt(page) {
  try {
    const prompt = page.locator("[data-pwa-install-target~='dialog']");
    if (await prompt.isVisible({ timeout: 1000 })) {
      const closeBtn = prompt.locator("button:has-text('Close'), a:has-text('Close')");
      if (await closeBtn.isVisible({ timeout: 500 })) {
        await closeBtn.click();
      }
    }
  } catch {
    // ignore
  }
}

export function composerEditor(page) {
  return page.locator("#composer lexxy-editor .lexxy-editor__content");
}

export async function typeInComposer(page, text) {
  const editor = composerEditor(page);
  await editor.click();
  await page.keyboard.type(text);
}

export async function pressInComposer(page, key) {
  const editor = composerEditor(page);
  await editor.focus();
  if (Array.isArray(key)) {
    const shortcut = key.map((k) => (k === "control" ? "Control" : k === "enter" ? "Enter" : k)).join("+");
    await page.keyboard.press(shortcut);
  } else if (key === "enter" || key === ":enter") {
    await page.keyboard.press("Enter");
  } else if (key === "up" || key === ":up") {
    await page.keyboard.press("ArrowUp");
  } else {
    await page.keyboard.press(key);
  }
}

export async function toggleRichTextToolbar(page) {
  const btn = page.locator("#composer .composer__rich-text-btn");
  await btn.click({ force: true });
}

export async function pickMention(page, name) {
  const item = page.locator(".lexxy-prompt-menu__item", { hasText: name });
  await item.waitFor({ state: "visible", timeout: 5000 });
  await page.keyboard.press("Tab");
}

export async function pasteInComposer(page, text, html = null) {
  const editor = composerEditor(page);
  await editor.click();
  await page.evaluate(
    ({ text, html }) => {
      const content = document.querySelector("#composer lexxy-editor .lexxy-editor__content");
      const dt = new DataTransfer();
      dt.setData("text/plain", text);
      if (html) dt.setData("text/html", html);
      const event = new ClipboardEvent("paste", { bubbles: true, cancelable: true, clipboardData: dt });
      content.dispatchEvent(event);
    },
    { text, html }
  );
}

export async function clickSendButton(page) {
  const btn = page.locator("#composer [data-action='composer#submit'], #composer button[type='submit']");
  await btn.click();
}

export async function fillInRichTextArea(page, id, text) {
  await page.locator(`lexxy-editor#${id}`).evaluate((el, val) => (el.value = val), text);
}

export async function sendMessage(page, text) {
  await fillInRichTextArea(page, "message_body", text);
  await clickSendButton(page);
}

export function withinMessage(page, messageId) {
  return page.locator(`#message_${messageId}`);
}

export async function revealMessageActions(page, messageId) {
  const msg = withinMessage(page, messageId);
  await msg.hover();
  const details = msg.locator("details.position-relative");
  const isOpen = await details.evaluate((el) => el.open).catch(() => false);
  if (!isOpen) {
    const optionsBtn = msg.locator(".message__options-btn");
    await optionsBtn.click({ force: true });
  }
  await msg.locator(".message__boost-btn").waitFor({ state: "visible", timeout: 5000 });
}

export async function fillInBoostInput(page, messageId, text) {
  const msg = withinMessage(page, messageId);
  const newBoostBtn = msg.locator(".message__boost-btn, button:has-text('New boost'), a:has-text('New boost')");
  await newBoostBtn.click({ force: true });
  const input = msg.locator("input.input--boost, input[type='text'][name='boost[content]']");
  await input.waitFor({ state: "visible", timeout: 5000 });
  await input.fill(text);
}

export async function assertBoostText(page, text, parentLocator = null) {
  const scope = parentLocator || page;
  const escaped = text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  await scope.locator(".boost").filter({ hasText: new RegExp(`^\\s*${escaped}\\b`) }).first().waitFor({ state: "visible", timeout: 10000 });
}

export async function assertMessageText(page, textOrRegex) {
  if (typeof textOrRegex === "string") {
    await page.locator(".message__body", { hasText: textOrRegex }).first().waitFor({ state: "visible", timeout: 10000 });
  } else {
    await page.waitForFunction(
      (pattern) => {
        const bodies = Array.from(document.querySelectorAll(".message__body"));
        return bodies.some((b) => new RegExp(pattern).test(b.textContent || ""));
      },
      textOrRegex instanceof RegExp ? textOrRegex.source : textOrRegex,
      { timeout: 10000 }
    );
  }
}

export async function assertRoomRead(page, roomName) {
  await page.waitForSelector(`.rooms a:not(.unread):has-text("${roomName}")`, { state: "visible", timeout: 10000 });
}

export async function assertRoomUnread(page, roomName) {
  await page.waitForSelector(`.rooms a.unread:has-text("${roomName}")`, { state: "visible", timeout: 10000 });
}

export async function assertComposerText(page, text) {
  await page.waitForFunction(
    (expected) => {
      const el = document.querySelector("#composer lexxy-editor .lexxy-editor__content");
      return el && (el.textContent?.includes(expected) || el.innerText?.includes(expected));
    },
    text,
    { timeout: 10000 }
  );
}

export async function assertComposerEmpty(page) {
  await page.waitForFunction(() => {
    const el = document.querySelector("#composer lexxy-editor .lexxy-editor__content");
    if (!el) return false;
    return !el.textContent || el.textContent.trim() === "";
  }, { timeout: 10000 });
}

export async function assertEditEditorText(page, text) {
  await page.waitForFunction(
    (expected) => {
      const el = document.querySelector(".message__body-content--editing lexxy-editor");
      return el && (el.value?.includes(expected) || el.textContent?.includes(expected) || el.innerText?.includes(expected));
    },
    text,
    { timeout: 10000 }
  );
}
