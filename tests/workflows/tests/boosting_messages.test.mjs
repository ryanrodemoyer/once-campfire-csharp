import assert from "node:assert";
import {
  FIXTURES,
  createSession,
  signIn,
  joinRoom,
  withinMessage,
  revealMessageActions,
  fillInBoostInput,
  assertBoostText,
} from "../helpers.mjs";

export async function runBoostingMessagesTests(browser, baseUrl, onResult = null) {
  const results = [];

  async function test(name, fn) {
    const started = Date.now();
    try {
      await fn();
      const r = { name: `boosting_messages: ${name}`, passed: true, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    } catch (err) {
      const r = { name: `boosting_messages: ${name}`, passed: false, error: err.message, stack: err.stack, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    }
  }

  await test("boosting a message", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.kevin.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      const msg = withinMessage(page, FIXTURES.messages.third);
      await revealMessageActions(page, FIXTURES.messages.third);
      await fillInBoostInput(page, FIXTURES.messages.third, "Good morning");
      await msg.getByRole("button", { name: "Submit" }).click();

      await assertBoostText(page, "Good morning", msg);
    } finally {
      await context.close();
    }
  });

  await test("deleting a boost", async () => {
    const { context, page } = await createSession(browser);
    try {
      await signIn(page, baseUrl, FIXTURES.users.david.email);
      await joinRoom(page, baseUrl, FIXTURES.rooms.designers);

      const boostContainer = page.locator(`#boost_${FIXTURES.boosts.first}`);
      await boostContainer.locator("span", { hasText: "Hello" }).click();
      const deleteBtn = boostContainer.getByRole("button", { name: "Delete this boost" });
      await deleteBtn.waitFor({ state: "visible", timeout: 5000 });
      await deleteBtn.click();

      await page.waitForFunction(
        (id) => !document.querySelector(`#boost_${id}`),
        FIXTURES.boosts.first,
        { timeout: 5000 }
      );
    } finally {
      await context.close();
    }
  });

  await test("message update preserves the input state", async () => {
    const sessionKevin = await createSession(browser);
    const sessionJz = await createSession(browser);
    try {
      // Kevin session
      await signIn(sessionKevin.page, baseUrl, FIXTURES.users.kevin.email);
      await joinRoom(sessionKevin.page, baseUrl, FIXTURES.rooms.designers);

      const kevinMsg = withinMessage(sessionKevin.page, FIXTURES.messages.third);
      await revealMessageActions(sessionKevin.page, FIXTURES.messages.third);
      await fillInBoostInput(sessionKevin.page, FIXTURES.messages.third, "Hey!");

      // JZ session: edit the message
      await signIn(sessionJz.page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(sessionJz.page, baseUrl, FIXTURES.rooms.designers);

      const jzMsg = withinMessage(sessionJz.page, FIXTURES.messages.third);
      await revealMessageActions(sessionJz.page, FIXTURES.messages.third);
      await jzMsg.locator(".message__edit-btn").click();
      await jzMsg.locator(".message__body-content--editing lexxy-editor").evaluate((el) => (el.value = "Redacted!"));
      await jzMsg.getByRole("button", { name: "Save changes" }).click();

      // Kevin session verifies: message body updated to Redacted! and boost input still has "Hey!"
      await kevinMsg.locator(".message__body", { hasText: "Redacted!" }).waitFor({ state: "visible", timeout: 10000 });
      const boostVal = await kevinMsg.locator("input.input--boost, input[type='text'][name='boost[content]']").inputValue();
      assert.strictEqual(boostVal, "Hey!");

      // Restore original message text
      await revealMessageActions(sessionJz.page, FIXTURES.messages.third);
      await jzMsg.locator(".message__edit-btn").click();
      await jzMsg.locator(".message__body-content--editing lexxy-editor").evaluate((el) => (el.value = "Third time's a charm."));
      await jzMsg.getByRole("button", { name: "Save changes" }).click();
      await kevinMsg.locator(".message__body", { hasText: "Third time's a charm." }).waitFor({ state: "visible", timeout: 10000 });
    } finally {
      await sessionKevin.context.close();
      await sessionJz.context.close();
    }
  });

  await test("boost by another user preserves the input state", async () => {
    const sessionKevin = await createSession(browser);
    const sessionDavid = await createSession(browser);
    try {
      // Kevin session
      await signIn(sessionKevin.page, baseUrl, FIXTURES.users.kevin.email);
      await joinRoom(sessionKevin.page, baseUrl, FIXTURES.rooms.designers);

      const kevinMsg = withinMessage(sessionKevin.page, FIXTURES.messages.third);
      await revealMessageActions(sessionKevin.page, FIXTURES.messages.third);
      await fillInBoostInput(sessionKevin.page, FIXTURES.messages.third, "Hey!");

      // David session: adds boost "Morning"
      await signIn(sessionDavid.page, baseUrl, FIXTURES.users.david.email);
      await joinRoom(sessionDavid.page, baseUrl, FIXTURES.rooms.designers);

      const davidMsg = withinMessage(sessionDavid.page, FIXTURES.messages.third);
      await revealMessageActions(sessionDavid.page, FIXTURES.messages.third);
      await fillInBoostInput(sessionDavid.page, FIXTURES.messages.third, "Morning");
      await davidMsg.getByRole("button", { name: "Submit" }).click();
      await assertBoostText(sessionDavid.page, "Morning", davidMsg);

      // In Kevin session: boost "Morning" appears over cable, and input still has "Hey!"
      await assertBoostText(sessionKevin.page, "Morning", kevinMsg);
      const boostVal = await kevinMsg.locator("input.input--boost, input[type='text'][name='boost[content]']").inputValue();
      assert.strictEqual(boostVal, "Hey!");
    } finally {
      await sessionKevin.context.close();
      await sessionDavid.context.close();
    }
  });

  return results;
}
