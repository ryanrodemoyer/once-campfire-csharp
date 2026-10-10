import assert from "node:assert";
import {
  FIXTURES,
  createSession,
  signIn,
  joinRoom,
  pasteInComposer,
} from "../helpers.mjs";

export async function runUnfurlingLinksTests(browser, baseUrl, onResult = null) {
  const results = [];

  async function test(name, fn) {
    const started = Date.now();
    try {
      await fn();
      const r = { name: `unfurling_links: ${name}`, passed: true, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    } catch (err) {
      const r = { name: `unfurling_links: ${name}`, passed: false, error: err.message, stack: err.stack, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    }
  }

  await test("a quote in the opengraph image URL cannot add attributes to the preview", async () => {
    const { context, page } = await createSession(browser);
    try {
      const pageUrl = "https://example.com/page.html";
      const imageUrl = 'http://127.0.0.1:9999/image.png?from=" style="outline:9px solid red';

      // Intercept /unfurl_link so the test does not depend on an external server or SSRF bypass
      await page.route("**/unfurl_link", async (route) => {
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            title: "A normal looking link",
            url: "https://example.com/harmless",
            description: "Nothing to see here",
            image: imageUrl,
          }),
        });
      });

      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await pasteInComposer(page, pageUrl);

      // Verify title appears in preview
      const title = page.locator("#composer lexxy-editor .og-embed__title", { hasText: "A normal looking link" });
      await title.waitFor({ state: "visible", timeout: 10000 });

      // Check image attributes
      const attrs = await page.evaluate(() => {
        const img = document.querySelector("#composer lexxy-editor .og-embed__image img");
        if (!img) return null;
        return Object.fromEntries(Array.from(img.attributes, (a) => [a.name, a.value]));
      });

      assert.ok(attrs, "image element should exist");
      assert.strictEqual(attrs.src, imageUrl);
      const extraKeys = Object.keys(attrs).filter((k) => !["src", "class", "alt"].includes(k));
      assert.deepStrictEqual(extraKeys, [], `Unexpected attributes: ${extraKeys.join(", ")}`);
    } finally {
      await context.close();
    }
  });

  return results;
}
