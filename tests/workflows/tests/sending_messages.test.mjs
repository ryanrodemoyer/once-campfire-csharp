import {
  FIXTURES,
  createSession,
  signIn,
  joinRoom,
  sendMessage,
  withinMessage,
  revealMessageActions,
  assertMessageText,
} from "../helpers.mjs";

export async function runSendingMessagesTests(browser, baseUrl, onResult = null) {
  const results = [];

  async function test(name, fn) {
    const started = Date.now();
    try {
      await fn();
      const r = { name: `sending_messages: ${name}`, passed: true, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    } catch (err) {
      const r = { name: `sending_messages: ${name}`, passed: false, error: err.message, stack: err.stack, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    }
  }

  await test("sending messages between two users", async () => {
    const sessionJz = await createSession(browser);
    const sessionKevin = await createSession(browser);
    try {
      await signIn(sessionJz.page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(sessionJz.page, baseUrl, FIXTURES.rooms.designers);

      await signIn(sessionKevin.page, baseUrl, FIXTURES.users.kevin.email);
      await joinRoom(sessionKevin.page, baseUrl, FIXTURES.rooms.designers);

      // JZ sends message
      await sendMessage(sessionJz.page, "Is this thing on?");

      // Kevin sees it over cable
      await assertMessageText(sessionKevin.page, "Is this thing on?");

      // Kevin replies
      await sendMessage(sessionKevin.page, "👍👍");

      // JZ sees reply over cable
      await assertMessageText(sessionJz.page, "👍👍");
    } finally {
      await sessionJz.context.close();
      await sessionKevin.context.close();
    }
  });

  await test("editing messages", async () => {
    const sessionJz = await createSession(browser);
    const sessionKevin = await createSession(browser);
    try {
      await signIn(sessionJz.page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(sessionJz.page, baseUrl, FIXTURES.rooms.designers);

      await signIn(sessionKevin.page, baseUrl, FIXTURES.users.kevin.email);
      await joinRoom(sessionKevin.page, baseUrl, FIXTURES.rooms.designers);

      // JZ edits messages(:third)
      const msg = withinMessage(sessionJz.page, FIXTURES.messages.third);
      await revealMessageActions(sessionJz.page, FIXTURES.messages.third);
      await msg.locator(".message__edit-btn").click();
      await msg.locator(".message__body-content--editing lexxy-editor").evaluate((el) => (el.value = "Redacted!"));
      await msg.getByRole("button", { name: "Save changes" }).click();

      // Kevin sees Redacted!
      await assertMessageText(sessionKevin.page, "Redacted!");

      // Restore original message text
      await revealMessageActions(sessionJz.page, FIXTURES.messages.third);
      await msg.locator(".message__edit-btn").click();
      await msg.locator(".message__body-content--editing lexxy-editor").evaluate((el) => (el.value = "Third time's a charm."));
      await msg.getByRole("button", { name: "Save changes" }).click();
      await assertMessageText(sessionKevin.page, "Third time's a charm.");
    } finally {
      await sessionJz.context.close();
      await sessionKevin.context.close();
    }
  });

  await test("deleting messages", async () => {
    const sessionJz = await createSession(browser);
    const sessionKevin = await createSession(browser);
    try {
      await signIn(sessionKevin.page, baseUrl, FIXTURES.users.kevin.email);
      await joinRoom(sessionKevin.page, baseUrl, FIXTURES.rooms.designers);
      await assertMessageText(sessionKevin.page, "Third time's a charm.");

      await signIn(sessionJz.page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(sessionJz.page, baseUrl, FIXTURES.rooms.designers);

      // JZ deletes messages(:third)
      const msg = withinMessage(sessionJz.page, FIXTURES.messages.third);
      await revealMessageActions(sessionJz.page, FIXTURES.messages.third);
      await msg.locator(".message__edit-btn").click();

      // Handle confirm dialog
      sessionJz.page.once("dialog", async (dialog) => {
        await dialog.accept();
      });
      await msg.getByRole("button", { name: "Delete message" }).click();

      // Kevin verifies message is gone
      await sessionKevin.page.waitForFunction(
        (id) => !document.querySelector(`#message_${id}`),
        FIXTURES.messages.third,
        { timeout: 10000 }
      );
    } finally {
      await sessionJz.context.close();
      await sessionKevin.context.close();
    }
  });

  return results;
}
