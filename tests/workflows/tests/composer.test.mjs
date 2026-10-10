import {
  FIXTURES,
  createSession,
  signIn,
  joinRoom,
  typeInComposer,
  pressInComposer,
  toggleRichTextToolbar,
  pickMention,
  pasteInComposer,
  clickSendButton,
  withinMessage,
  revealMessageActions,
  assertMessageText,
  assertComposerText,
  assertComposerEmpty,
  assertEditEditorText,
} from "../helpers.mjs";

export async function runComposerTests(browser, baseUrl, onResult = null) {
  const results = [];

  async function test(name, fn) {
    const started = Date.now();
    try {
      await fn();
      const r = { name: `composer: ${name}`, passed: true, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    } catch (err) {
      const r = { name: `composer: ${name}`, passed: false, error: err.message, stack: err.stack, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    }
  }

  await test("arrow up edits my last message when the composer is empty", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await page.locator("#composer lexxy-editor .lexxy-editor__content").click();
      await pressInComposer(page, "up");

      await page.locator(".message__body-content--editing").waitFor({ state: "visible", timeout: 5000 });
      await assertEditEditorText(page, "Third time's a charm.");
    } finally {
      await context.close();
    }
  });

  await test("replying quotes the original message with attribution", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      const msg = withinMessage(page, FIXTURES.messages.third);
      await revealMessageActions(page, FIXTURES.messages.third);
      await msg.locator("button[title='Reply'], button[aria-label='Reply'], button[data-action~='reply#reply']").click();

      await assertComposerText(page, "Third time's a charm.");

      await clickSendButton(page);

      const quote = page.locator(".message:last-of-type .message__body blockquote", { hasText: "Third time's a charm." });
      const cite = page.locator(".message:last-of-type .message__body cite", { hasText: "JZ" });
      await quote.waitFor({ state: "visible", timeout: 10000 });
      await cite.waitFor({ state: "visible", timeout: 10000 });
    } finally {
      await context.close();
    }
  });

  await test("enter sends the message when the toolbar is collapsed", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await typeInComposer(page, "A quick reply");
      await pressInComposer(page, "enter");

      await assertMessageText(page, "A quick reply");
      await assertComposerEmpty(page);
    } finally {
      await context.close();
    }
  });

  await test("enter adds a newline in rich text mode and meta+enter sends", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await toggleRichTextToolbar(page);

      await typeInComposer(page, "line one");
      await pressInComposer(page, "enter");
      await typeInComposer(page, "line two");

      // Verify not yet sent
      const hasLineOne = await page.locator(".message__body", { hasText: "line one" }).isVisible().catch(() => false);
      if (hasLineOne) {
        throw new Error("line one was sent before Ctrl+Enter!");
      }

      await pressInComposer(page, ["control", "enter"]);

      await assertMessageText(page, /line one\s*line two/);
      await assertComposerEmpty(page);
    } finally {
      await context.close();
    }
  });

  await test("an unsent message is kept as a draft while hopping between rooms", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await typeInComposer(page, "Still writing this");

      // Switch to HQ
      await joinRoom(page, baseUrl, FIXTURES.rooms.hq);
      await assertComposerEmpty(page);

      await typeInComposer(page, "And this one too");

      // Switch back to Designers
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);
      await assertComposerText(page, "Still writing this");

      await pressInComposer(page, "enter");
      await assertMessageText(page, "Still writing this");

      // Switch to HQ again
      await joinRoom(page, baseUrl, FIXTURES.rooms.hq);
      await assertComposerText(page, "And this one too");

      // Switch to Designers again
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);
      await assertComposerEmpty(page);
    } finally {
      await context.close();
    }
  });

  await test("markdown strikethrough survives sanitization", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await typeInComposer(page, "Hello ~~Claude~~ World");
      await pressInComposer(page, "enter");

      const sTag = page.locator(".message:last-of-type .message__body s, .message:last-of-type .message__body del", { hasText: "Claude" });
      await sTag.waitFor({ state: "visible", timeout: 10000 });
      await assertMessageText(page, "Hello Claude World");
    } finally {
      await context.close();
    }
  });

  await test("mentioning a user with @ inserts a mention attachment", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await typeInComposer(page, "Hey @Jas");
      await pickMention(page, "Jason");
      await clickSendButton(page);

      const mention = page.locator(".message:last-of-type .message__body .mention", { hasText: "Jason" });
      await mention.waitFor({ state: "visible", timeout: 10000 });
    } finally {
      await context.close();
    }
  });

  await test("editing a message with a mention keeps the mention", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await typeInComposer(page, "Hey @Jas");
      await pickMention(page, "Jason");
      await clickSendButton(page);

      const lastMsg = page.locator(".message:last-of-type");
      await lastMsg.locator(".message__body .mention", { hasText: "Jason" }).waitFor({ state: "visible", timeout: 10000 });

      // Edit message
      const idAttr = await lastMsg.getAttribute("id");
      const messageId = idAttr.replace("message_", "");
      await revealMessageActions(page, messageId);
      await lastMsg.locator(".message__edit-btn").click();
      await assertEditEditorText(page, "Jason");
      await lastMsg.getByRole("button", { name: "Save changes" }).click();

      // Mention remains
      await lastMsg.locator(".message__body .mention", { hasText: "Jason" }).waitFor({ state: "visible", timeout: 10000 });
    } finally {
      await context.close();
    }
  });

  await test("pasting a table sends it as a table", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await pasteInComposer(page, "Name Points\nJason 10", "<table><tr><th>Name</th><th>Points</th></tr><tr><td>Jason</td><td>10</td></tr></table>");
      await page.locator("#composer lexxy-editor table").waitFor({ state: "visible", timeout: 5000 });

      await clickSendButton(page);

      const tableTh = page.locator(".message:last-of-type .message__body table th", { hasText: "Name" });
      const tableTd = page.locator(".message:last-of-type .message__body table td", { hasText: "10" });
      await tableTh.waitFor({ state: "visible", timeout: 10000 });
      await tableTd.waitFor({ state: "visible", timeout: 10000 });
    } finally {
      await context.close();
    }
  });

  await test("pasting a URL unfurls an opengraph preview", async () => {
    const { context, page } = await createSession(browser);
    try {
      await page.route("**/unfurl_link", async (route) => {
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            title: "Example Site",
            url: "https://example.com/article",
            description: "An example article",
            image: "",
          }),
        });
      });

      await signIn(page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      await pasteInComposer(page, "https://example.com/article");

      const previewTitle = page.locator("#composer .og-embed__title", { hasText: "Example Site" });
      await previewTitle.waitFor({ state: "visible", timeout: 10000 });

      await clickSendButton(page);

      const msgTitle = page.locator(".message:last-of-type .og-embed__title", { hasText: "Example Site" });
      await msgTitle.waitFor({ state: "visible", timeout: 10000 });
    } finally {
      await context.close();
    }
  });

  return results;
}
