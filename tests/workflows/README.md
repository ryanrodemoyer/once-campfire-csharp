# Playwright System Test Workflows

Ported from the reference Capybara system tests in `reference/test/system/`.

These workflows test full end-to-end user behavior in headless Chrome, exercising HTTP requests, Action Cable WebSockets, Turbo Streams, DOM updates, and client state across multiple simultaneous browser sessions.

## Workflows Covered

The test suites port all browser workflows from `reference/test/system/`:

1. **`boosting_messages`** (`tests/boosting_messages.test.mjs`, refs `reference/test/system/boosting_messages_test.rb`):
   - Boosting a message
   - Deleting a boost
   - Message update preserves the boost input state
   - Boost by another user preserves the boost input state

2. **`composer`** (`tests/composer.test.mjs`, refs `reference/test/system/composer_test.rb`):
   - Arrow up edits my last message when the composer is empty
   - Replying quotes the original message with attribution
   - Enter sends the message when the toolbar is collapsed
   - Enter adds a newline in rich text mode and Ctrl+Enter sends
   - An unsent message is kept as a draft while hopping between rooms
   - Markdown strikethrough survives sanitization
   - Mentioning a user with `@` inserts a mention attachment
   - Editing a message with a mention keeps the mention
   - Pasting a table sends it as a table
   - Pasting a URL unfurls an OpenGraph preview

3. **`sending_messages`** (`tests/sending_messages.test.mjs`, refs `reference/test/system/sending_messages_test.rb`):
   - Sending messages between two users in real-time over Action Cable
   - Editing messages broadcasts changes in real-time
   - Deleting messages broadcasts deletions in real-time

4. **`unfurling_links`** (`tests/unfurling_links.test.mjs`, refs `reference/test/system/unfurling_links_test.rb`):
   - A quote in the OpenGraph image URL cannot inject attributes into the preview

5. **`unread_rooms`** (`tests/unread_rooms.test.mjs`, refs `reference/test/system/unread_rooms_test.rb`):
   - Unread room notifications between two users across rooms

## Running the Workflows

Run against both reference and candidate servers:
```bash
tests/workflows/run
```

Run against a specific server:
```bash
tests/workflows/run --server http://127.0.0.1:3100
tests/workflows/run --server http://127.0.0.1:3200
```

Run a specific test suite:
```bash
tests/workflows/run --suite composer
```

Write a JSON results report:
```bash
tests/workflows/run --report results.json
```
