# Upstream sync: 345e637 → edbc779

U01 moved `reference/` from `basecamp/once-campfire@345e637` to `edbc779` (main on 2026-10-09).
This file maps (1) every first-parent upstream commit in that range that touches `app/`, `config/`,
`lib/` or `db/` to the U task that ports it, and (2) every parity-gate failure against the new
reference to the task that fixes it. U01 ports no app behavior.

Produced with:

```sh
git -C reference log --first-parent --reverse 345e637..edbc779 -- app config lib db   # 59 commits
```

## What U01 rebuilt

- **Reference image**: `parity/bin/reference build` from `reference/` at edbc779: Ruby 4.0.7,
  Puma 8.0.2, Rails main `e3d5c56`, Propshaft 1.3.2, Rack 3.2.7. `PARITY_APP_IMAGE` and
  `PARITY_IMAGE` now name the two images, so two pins can sit side by side
  (`campfire-reference:app-edbc779` and `campfire-reference:edbc779` here).
- **Seed**: `parity/bin/seed build default`, recorded in `parity/.seed/SEED.sha256` and verified.
  The schema gains `rooms.messages_count`, `index_messages_on_room_id_and_created_at`,
  `index_messages_on_room_id_and_updated_at` and the three `messages_*_rooms_messages_count`
  triggers. The database dump changed, and three media files changed only their storage keys.
  Note for U02: on the seed, `messages_count` is behind the real count by the fixture messages in
  two rooms (All Talk 121 vs 131, Designers 28 vs 31), because fixtures load after the triggers
  are installed and reset `rooms`. That is Rails' state, so the C# app must read it as is.
- **Frontend**: the JS changes (`messages_controller`, `read_rooms_controller`,
  `rooms_list_controller`, `base_autocomplete_handler`, `file_uploader`) flow in from
  `reference/`. `assets/vendor/` and the asset goldens
  (`tests/Campfire.Web.Tests/Assets/Reference/`) were re-exported from the new image: Action Text
  and Active Storage JS changed with Rails main, and Action View's rails-ujs left the load path
  (312 compiled files, was 314). Two gem changes were ported so the build still matches
  byte for byte: Propshaft 1.3.2's `"app"` flag in `.manifest.json`, and
  ActionDispatch::Static's `charset=utf-8` on HTML and CSS files.
- **Erubi golden** (`tests/Campfire.Templates.Generator.Tests/Erubi/reference-views.json`)
  regenerated inside the new image. The scanner splits all 89 new views like Erubi does.
- **Benchmark reference image**: `bench/README.md` said to build it with
  `reference-rust/parity/bin/reference build`, which builds from `reference-rust/reference/`
  (once-campfire `90b3300`, older than either pin). It now says `parity/bin/reference build`,
  from our `reference/`.

## 1. Upstream commits

Commits that touch only JavaScript are done by U01: the frontend ships verbatim from `reference/`.

| Commit | Upstream change | Paths | Task |
|---|---|---|---|
| acef0c7 | #295 Index messages by room and creation time | db/migrate/20261004225000, db/schema.rb | U02 |
| f2bdfde | #298 Search operator words as words | app/models/message/searchable.rb | U05 |
| e0e3184 | #299 Boost soft-keyboard `data-action` | app/views/messages/boosts/_boosts.html.erb | U05 |
| c73ea37 | #300 Autocomplete fetch asks for JSON | app/javascript (only) | U01 |
| d1f09b4 | #297 `paged?` without counting | app/models/message/pagination.rb | U05 |
| 2393f01 | #302 Stray `</span>`s in the flash | app/views/layouts/application.html.erb | U03 (layout) |
| 77c5234 | #304 Newest search matches by rowid | searches_controller.rb, message/searchable.rb | U05 |
| 4690de5 | #301 Edge install icon path | app/views/pwa/_install_instructions.html.erb | U05 |
| d2155e8 | #306 Webhook reply attachment only on HTTP 200 | app/models/webhook.rb | **none**: closest is U04 (a webhook reply becomes a message); add it to U04 |
| 32b4144 | #314 Update Rails main | user/mentionable.rb, accounts/edit, pwa partials, time_formats, sentry, development.rb | U05 for the views and `to_editor_content_attachment_partial_path`; the Rails main bump itself: **none**, see [gem-level changes](#gem-level-changes) |
| cf63b61 | #334 Create the created_at index only where missing | db/migrate/20261004225000 | U02 |
| 1e0d353 | #312 Refresh finds replaced messages through an index | message/pagination.rb, db/migrate/20261005022000, db/schema.rb | U02 (index), U05 (query) |
| 87eafc0 | #310 Direct room with one query | app/models/rooms/direct.rb | U05 |
| 0d5998f | #318 Sidebar directs and shared rooms queried separately | users/sidebars_controller.rb, membership.rb | U05 |
| fdec7df | #322 Boosts cached with their message | messages/boosts/_boost, _boosts | U05 |
| b6d3075 | #325 Boosts rendered from the preload | messages/boosts/_boosts | U05 |
| 819b389 | #323 Push badge counts once per batch | push/subscription.rb, lib/web_push/pool.rb | U04 |
| 6ad4735 | #324 Search entry rewritten only on body/attachment change | message/searchable.rb | U04 |
| 0f2a1da | #316 One page of account members at a time | accounts_controller, accounts/users_controller, user/bannable.rb, accounts/edit | U05 |
| cbf5280 | #311 Post a message whose attachment can't be previewed | message/attachment.rb | U06 |
| a991adb | #330 Bound attachment preview work | attachment_presentation.rb, message/attachment.rb, _message, initializers/active_storage.rb, lib/rails_ext/time_limited_video_previewer.rb | U06 |
| 3005154 | #331 No previews for rich-text file embeds | active_storage/blobs/_blob, _message | U06 |
| b409b99 | #328 Bound link unfurling | unfurl_links_controller.rb, opengraph/fetch.rb | U06 |
| 72648a8 | #296 Unread room notice fanned out from a job | presence_channel.rb, broadcast_unread_room_job.rb, message/broadcasts.rb (+ JS) | U04 |
| 4ceb92e | #326 Pooled push connections pinned to the approved address | initializers/web_push.rb, lib/web_push/connections.rb, pool.rb | U06 |
| 14a2f8f | #329 A room's messages deleted in a job | rooms_controller, room/destroy_job.rb, room.rb, user.rb | U04 |
| 27065ef | Same-second unread events; bounded idle push connections | presence_channel.rb, message/broadcasts.rb, lib/web_push/connections.rb | U04 (unread), U06 (push) |
| 7df98ac | Merge Rails main during performance review | boosts_controller, messages_helper, message.rb (`to_key` removed), _message, _template (+ JS) | U05 (message-id DOM ids, template); U04 (boost broadcast target) |
| dbc7620 | Bound accessible search probes | searches_controller.rb, message/searchable.rb | U05 |
| 2b9685c | Wait for sidebar frames before reloading | app/javascript (only) | U01 |
| 3c022a1 | Timestamp ordering of unread notices | app/javascript (only) | U01 (U04 keeps the server's ordering) |
| 765711f | Request ports kept in copy links | concerns/set_current_request.rb | U05 |
| 8bbe129 | Active ping editors kept during sidebar refreshes | app/javascript (only) | U01 |
| 27f5461 | Complete auto-submit transfer form | sessions/transfers/show.html.erb | U05 |
| ac73267 | Reuse authorized read pages (response cache) | application_controller, concerns/cached_responses.rb, messages/rooms/searches/sidebars controllers, response_cache.rb | U07 |
| b220486 | Paginated HTML admitted; CSRF hydrated outside keys | cached_responses.rb, messages_controller | U07 |
| 8d02540 | Bounded fragment cache; collapsed concurrent renders | cached_responses.rb, fragment_cache.rb, response_cache.rb, initializers/fragment_cache.rb | U07 |
| a95361d | Recheck snapshot before fragment lookup | cached_responses.rb | U07 |
| 105f4b8 | Digest session credentials in fragment keys | cached_responses.rb | U07 |
| d9c3207 | Banned-user push notification fix | room/message_pusher.rb | U04 |
| fd49099 | #337 SQLite-maintained bot pagination counts | by_bots_controller, room/messages_count.rb, initializers/room_messages_count.rb, db/migrate/20261007190000, schema.rb, tasks/room_messages_count.rake | U02 |
| f792e16 | Repair counters within database task pools | room/messages_count.rb, tasks/room_messages_count.rake | U02 |
| 8de3e22 | Clarify repair hook scope | tasks/room_messages_count.rake | U02 |
| 0f5d0b2 | Header-only forgery protection; complete-response cache | authentication.rb, application_helper.rb, layout, session_store.rb, active_storage_authentication.rb, cached_responses.rb, messages_controller (+ file_uploader.js) | U03 (forgery, tokens, layout); U07 (cache) |
| f36b5a2 | #336 Already-unread memberships not rewritten | app/models/room.rb | U04 |
| e55b6dc | #339 WAL checkpoint outside request threads | database.yml, initializers/sqlite_wal_checkpoint.rb, puma.rb, lib/rails_ext/sqlite_wal_checkpoint.rb, tasks/resque.rake | U08 |
| 2c53c46 | Token-free message controls; posting deliveries rendered once | session_lookup.rb, cached_responses.rb, messages_controller, messages_helper, message/broadcasts.rb, _actions, create.turbo_stream | U05 (controls), U04 (deliveries, broadcasts), U07 (cache parts) |
| 59e1c6d | #335 Invalid QR code requests rejected | qr_code_controller.rb | U05 |
| 74381e8 | Content-validated message fragments kept | session_lookup.rb, cached_responses.rb, messages_helper, fragment_cache.rb, _message, messages/index, refreshes/show, rooms/show, searches/index | U07 (views keep their output; U05 checks them) |
| bbee420 | Epoch-verified record snapshots; no empty push payloads | session_lookup.rb, cached_responses.rb, room_scoped.rb, rooms_controller, record_cache.rb, message_pusher.rb, _actions | U07 (snapshots), U04 (push payloads), U05 (_actions) |
| a900701 | Cache transactions checked without a connection | cached_responses.rb, record_cache.rb | U07 |
| a0d7293 | Temporary transaction guards; simpler quick boost forms | cached_responses.rb, fragment_cache.rb, record_cache.rb, _actions | U07 (guards), U05 (quick boost forms) |
| 9b8a168 | Native plain text for attachment-free content | app/models/message.rb | U04 (same output, the write path's plain text) |
| 7331d3a | Messages, search entries and unread state created atomically | message.rb, message/searchable.rb, room.rb | U04 |
| 4bcc745 | Interruptible WAL checkpoint shutdown; exclusive lock kept | lib/rails_ext/sqlite_wal_checkpoint.rb | U08 |
| 19c9e6b | No room queries for messages without mentions | message/mentionee.rb | U04 |
| a7e9ea5 | `http_proxy` can't bypass the pinned unfurl address | opengraph/fetch.rb | U06 |
| 2daa01e | Pooled push connections as a Net::HTTP subclass with Stages | initializers/web_push.rb, lib/web_push/connections.rb | U06 |
| edbc779 | Pooled-connection class named and tested on its own | lib/web_push/connections.rb | U06 |

### Gem-level changes

The Rails main bump (32b4144, then 7df98ac) and the new Ruby, Puma, Propshaft and Rack versions
change responses without any app code changing. **None of U02–U08 covers these as written**:

| Change | Suggested task |
|---|---|
| `X-XSS-Protection: 0` is gone from the default headers (the C# app still sends it) | U03 (it owns the security-header and forgery defaults) |
| Action Text's editor tag no longer renders `input="message_body_trix_input_message"` on the composer's `lexxy-editor` | U05 (room page markup) |
| Propshaft 1.3.2's manifest `app` flag; ActionDispatch::Static's `charset=utf-8` on HTML and CSS | done in U01 (the frontend rebuild needed them) |

Checked against the old image (`campfire-reference:latest`, 345e637) and **not** caused by the
bump, so not mapped here: the `Link: …; rel=preload` stylesheet header, Thruster's `X-Cache`
header and gzip on assets, `charset=UTF-8` in upper case on some responses, and turbo-rails'
minimal layout for Turbo Frame requests. Rails sent all of them at 345e637 too; the candidate
doesn't. They are existing gaps for Q11 (and P03 for the front server).

## 2. Gate failures

Run against the edbc779 reference on 2026-10-10, with both servers on fresh copies of the default
seed. Two harness problems block every gate before any comparison, and are noted first.

### Harness

- **The parity clock predates the new migrations.** Every harness starts the reference at the
  seed's instant, `2026-03-02T16:00:00Z`. Rails now refuses to boot:
  `ActiveRecord::InvalidMigrationTimestampError: Invalid timestamp 20261004225000 … must be less
  than 20260303160001` (migration timestamps must not be in the future). The runs below used
  `--time 2026-10-09T16:00:00Z`; Ruby vs Ruby on that clock replays 282 matched, 0 differed.
  Every place that starts Rails at the seed instant (`parity/screens/compare`, the vector
  generators' documented `--time`, `tests/Campfire.Web.Tests/Pipeline/Vectors`) needs a clock
  after 2026-10-07. **Task: Q11** (owns the gate runs on the new pin).
- **The replay and continuity harnesses need an authenticity token.** `parity/replay/replay.py`
  (`token_in`, `meta_token`) and `parity/continuity/scenarios.py` raise when a form or the
  layout carries no token, which is every Rails page now. The replay runs below used a local,
  uncommitted change that sends a token only when the page has one. **Task: U03** (its card asks
  for the harness change).

### Replay (`parity/replay/run`)

`parity/replay/run http://127.0.0.1:3150 http://127.0.0.1:3250` (all 16 default-seed families,
282 responses): **0 matched, 282 differed**. Every difference, grouped:

| Difference | Responses | Task |
|---|---|---|
| Candidate's pages carry `csrf-param`/`csrf-token` meta tags and an `authenticity_token` input in every form | 128 | U03 |
| `Vary: Sec-Fetch-Site` missing on the candidate | 211 | U03 |
| Cookies differ: the candidate sets `_campfire_session` on pages that no longer need one, and sign-ins differ with the token checks | 24 | U03 |
| `POST /session` with a bad or missing token: Rails 302, candidate 422; `POST /join/<wrong code>`: Rails 404, candidate 422 | 3 | U03 |
| `X-XSS-Protection` extra on the candidate | 240 | none as written; U03 suggested, [above](#gem-level-changes) |
| Composer's `lexxy-editor` has an extra `input` attribute on the candidate (rooms with a composer) | 7 | none as written; U05 suggested, [above](#gem-level-changes) |
| Not from the bump (Rails sent them at 345e637 too): `X-Cache` and `Link` headers missing, `charset=UTF-8` vs `utf-8`, Turbo Frame requests get the whole layout | 282 / 110 / 22 / 9 | Q11 (existing gaps) |
| Message markup: `message_<id>`, `edit_message_<id>`, `boosting_message_<id>`, `boosts_message_<id>` (Message#to_key removed), `data-client-message-id` on the message | 37 | U05 |
| The message template gains `data-pending-message` and `data-client-message-id` | 14 | U05 |
| Boost link `data-action="soft-keyboard#open"` (was `action=`), boosts frame markup | 3 | U05 |
| Account settings checkbox renders `checked="checked"` (`tag.input`) | 3 | U05 |
| Transfer page: the footer sits outside the auto-submit form | 4 | U05 |
| `GET /searches?q="quoted" OR`: Rails 200, candidate 500 | 3 | U05 |
| `GET /qr_code/!!`: Rails 400, candidate 500 | 2 | U05 |
| `GET /rooms/{broken}`: Rails 500 (`undefined method 'attributes' for nil` in `ResponseCache#synchronize_render`), candidate 200 | 1 | U07 (port the response cache, including this Rails behavior, or record it as a known difference) |
| `GET /account/logo` and the avatar after an upload: Rails 200, candidate 500 | 2 | not the bump: this run's candidate image predates P07 (media libraries, merged since) |

### Route coverage

`config/routes.rb` is unchanged between the pins, so the route table and
`parity/route-coverage.json` stand. `RouteCoverageTests` passes in `bin/check`. Green.

### Screens (`parity/screens/compare`)

At the seed instant the reference can't boot (harness, above). With
`--time 2026-10-09T16:00:00Z`:

- The full lean matrix (874 cells) was stopped after the first 70, all failing; it runs at about
  8 cells a minute here.
- `parity/screens/compare --time 2026-10-09T16:00:00Z --only "auth/sign_in,rooms/show/designers,search/submitted,account/edit" --isolated 1`:
  **32 cells: 4 pass, 28 fail, 0 error**. Pixels and the accessibility tree are equal in every
  cell. `search/submitted` passes. `auth/sign_in` differs only in the network layer: Thruster's
  gzip, `Vary` and `X-Cache` on assets (Q11/P03, not the bump) and the session cookie the candidate
  still sets (U03). `rooms/show/designers` also differs in the server and live DOM: the message
  markup (U05) and tokens (U03).
- In the stopped full run, `realtime/attachment_sent` timed out on Firefox and WebKit waiting for
  `.message__attachment`; not yet explained. **Task: U06** (attachment presentation changed in
  a991adb/3005154), then Q11.

### Cable (`parity/cable/run`)

`parity/cable/run http://127.0.0.1:3150 http://127.0.0.1:3250`: 19/19 passed. The runner's live
mode replays its recorded golden sequences rather than the two servers, so this doesn't yet test
edbc779. Its goldens need re-recording against the new reference for the unread-room fan-out
(U04, 72648a8/27065ef) and the boost broadcast target (U04, 7df98ac). **Task: U04**, then Q11.

### State (`parity/state/run --replay`)

`parity/state/run --replay http://127.0.0.1:3150 http://127.0.0.1:3250 --ref-storage … --cand-storage …`:
differences after every family. Most come from reading each database with `immutable=1`, which
ignores the WAL: Rails now checkpoints from a background thread (e55b6dc), so its file is current
and the candidate's isn't. Diffing the final states after a WAL checkpoint leaves:

| Difference | Task |
|---|---|
| `sessions`: Rails signs in on `POST /session` with a bad or missing token; the candidate refuses (4 rows only in Rails, so later ids shift) | U03 |
| `memberships` created by the join family differ in `created_at` (8 rows); not yet explained, re-check once U03 lands | U03, then Q11 |
| `active_storage_blobs`/`attachments`: the candidate keeps an unattached logo and bot avatar blob | P07 (merged since this run: the candidate image had no media libraries) |
| The per-family diff reads stale databases (`immutable=1` ignores the WAL) | Q11 (checkpoint before dumping) and U08 |

`messages`, `rooms` (including `messages_count`), `boosts` and the search index were equal at
the end. `StateDifferentialTests` (Rails edbc779 boots on C#-written data, then reads, edits,
deletes and searches) passes against the new image.

### Continuity (`parity/continuity/run`)

Both servers on one database (`parity/continuity/Server.cs` on the reference instance's storage):
**7/14**.

| Failure | Task |
|---|---|
| Rails → C#: all but the tamper check stop at "Could not find authenticity token in /session/new" | U03 (harness and port) |
| C# → Rails: `security_csrf_tamper_continuity` expects 422 for a tampered token; Rails now ignores tokens and answers 302 | U03 |

C# → Rails sessions, sign-in forms, messages, profile updates and transfer links all pass: a
C#-issued cookie still signs in on edbc779.

### Workflows (`tests/workflows/run`)

- Against the C# app: 19/19.
- Against Rails edbc779: 13/19. Every failure waits for `#message_0003`, the client-id DOM id
  that Message#to_key no longer produces (7df98ac). The workflows and the C# message markup
  move to message-id DOM ids together. **Task: U05.**

## bin/check skips

Tests that fail only because Rails changed are skipped with the task that removes the skip:

| Test | Task |
|---|---|
| `SchemaPreparerTests.Migration_versions_are_the_reference_migrations`, `.Schema_sha1_is_the_reference_schema_rb` | U02 |
| `LayoutGoldenTests` (both) | U03 |
| `SessionsControllerTests`, `AccountBotsReplayTests`, `PushSubscriptionsControllerTests`, `RoomSettingsReplayTests` replay tests | U03 |
| `WriteControllerTests` replay test | U04 |
| `RoomsControllerTests`, `BoostsReplayTests`, `SidebarsReplayTests`, `SearchesControllerTests` (both), `AccountsControllerTests`, `ProfilesReplayTests`, `PwaControllerTests` replay tests | U05 |

The controller vectors were recorded at 345e637. The task that removes a skip regenerates that
family's vectors at edbc779 (each `Vectors/*.rb` says how; use a `--time` after 2026-10-07).
