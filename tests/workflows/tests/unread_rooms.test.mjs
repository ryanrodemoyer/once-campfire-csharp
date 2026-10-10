import {
  FIXTURES,
  createSession,
  signIn,
  joinRoom,
  sendMessage,
  assertRoomRead,
  assertRoomUnread,
} from "../helpers.mjs";

export async function runUnreadRoomsTests(browser, baseUrl, onResult = null) {
  const results = [];

  async function test(name, fn) {
    const started = Date.now();
    try {
      await fn();
      const r = { name: `unread_rooms: ${name}`, passed: true, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    } catch (err) {
      const r = { name: `unread_rooms: ${name}`, passed: false, error: err.message, stack: err.stack, duration: Date.now() - started };
      results.push(r);
      if (onResult) onResult(r);
    }
  }

  await test("unread room notifications between two users", async () => {
    const sessionJz = await createSession(browser);
    const sessionKevin = await createSession(browser);
    try {
      await signIn(sessionJz.page, baseUrl, FIXTURES.users.jz.email);
      await joinRoom(sessionJz.page, baseUrl, FIXTURES.rooms.hq);
      await assertRoomRead(sessionJz.page, "HQ");

      // Kevin joins Designers and sends messages
      await signIn(sessionKevin.page, baseUrl, FIXTURES.users.kevin.email);
      await joinRoom(sessionKevin.page, baseUrl, FIXTURES.rooms.designers);
      await sendMessage(sessionKevin.page, "Hello!!");
      await sendMessage(sessionKevin.page, "Talking to myself?");

      // JZ asserts Designers is now marked unread
      await assertRoomUnread(sessionJz.page, "Designers");

      // JZ joins Designers
      await joinRoom(sessionJz.page, baseUrl, FIXTURES.rooms.designers);
      await assertRoomRead(sessionJz.page, "Designers");
    } finally {
      await sessionJz.context.close();
      await sessionKevin.context.close();
    }
  });

  return results;
}
