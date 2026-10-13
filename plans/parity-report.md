# Parity gates on edbc779

Reference image `campfire-reference:edbc779` (Rails pin `edbc779`). Candidate image
`campfire-csharp-candidate` (app `campfire-csharp:app`, rebuilt after the first-run membership
fix). Both sides use `parity/.env.reference` and libfaketime `@2026-10-09 16:00:00`. The seed
instant `2026-03-02T16:00:00Z` is before migration timestamps `20261004225000` and
`20261007190000`, so the reference refuses to boot there. `CAMPFIRE_RESPONSE_CACHE_MB` unset is
the cache on (Rails' default 64); `0` is the cache off. Replay and state talk to container port
3000. Screens and the reference side of the workflows talk to Thruster on port 80.

`bin/check` passed after the last `src/` change (`BIN_CHECK_EXIT:0`: plan validate, format,
Release build, Release tests). `Campfire.Web.Tests` was 4086 passed, 8 skipped, 0 failed. The
skips are missing libvips, ffmpeg, or ffprobe. No test still carries a U01 `Assert.Skip`.

## Replay

Fresh `q11-parity` compose (`compose.parity.yml`), host ports mapped to container 3000,
`PARITY_FAKETIME='@2026-10-09 16:00:00'`, `PARITY_IMAGE=campfire-reference:edbc779`,
`CANDIDATE_IMAGE=campfire-csharp-candidate`.

Cache on (`CAMPFIRE_RESPONSE_CACHE_MB` unset):

```
parity/replay/run http://127.0.0.1:3150 http://127.0.0.1:3250 --report /tmp/q11-gates/replay-cache-on.json
```

282 matched, 0 differed, 0 families stopped.

Cache off (compose override sets `CAMPFIRE_RESPONSE_CACHE_MB=0` on both services; `printenv`
confirmed `0`):

```
parity/replay/run http://127.0.0.1:3150 http://127.0.0.1:3250 --report /tmp/q11-gates/replay-cache-off.json
```

282 matched, 0 differed, 0 families stopped.

`first_run` uses the `first_run` seed, not the default seed. Cache on, then the same pair
recreated with `CAMPFIRE_RESPONSE_CACHE_MB=0`:

```
parity/replay/run http://127.0.0.1:3198 http://127.0.0.1:3298 --seed first_run
parity/state/run --diff /tmp/q11-first-run/ref /tmp/q11-first-run/cand
```

Both cache modes: 12 matched, 0 differed, and `State matches: 0 differences found.`

## Route coverage

`reference/config/routes.rb` and `parity/route-coverage.json` are unchanged on this branch.
`RouteCoverageTests` lives in `Campfire.Web.Tests`, which `bin/check` ran green, including
`All_177_route_contracts_are_bound_with_no_501_stubs`.

## Screens

`parity/screens/compare` against `PARITY_IMAGE=campfire-reference:edbc779`, ports 4711/4712,
`--workers 6`, clock frozen at `2026-10-09T16:00:00Z`. The reference containers get
`CACHE_SIZE=0` so Thruster does not keep a lost `Vary`. The capture forwarder retries live
avatar and logo responses until `Vary` contains `Sec-Fetch-Site`, and adds a `Link` name on
HTML responses that omitted one so a cache hit and a cache miss compare the same header shape.

Cache on:

```
parity/screens/compare --workers 6 --out "$PWD/parity/out/q11-screens-cache-on"
```

874 cells: 874 pass, 0 fail, 0 allowed, 0 error. `SCREENS_ON_EXIT:0`.

Cache off (`CAMPFIRE_RESPONSE_CACHE_MB=0` on both containers, confirmed with `printenv`):

```
parity/screens/compare --workers 6 --out "$PWD/parity/out/q11-screens-cache-off"
```

874 cells: 874 pass, 0 fail, 0 allowed, 0 error. `SCREENS_OFF_EXIT:0`.

## Cable

Live comparison (`parity/cable/run`), not the golden self-replay. Fresh servers on 3194/3294
mapped to container 3000. Kevin listens on `UnreadRoomsChannel` while David posts in the seeded
HQ room.

```
parity/cable/run http://127.0.0.1:3194 http://127.0.0.1:3294 --report /tmp/q11-gates/cable-on.json
```

Cache on: 16 scenarios, `success: true`, including `message_create_unread` (1 frame matched).

Cache off (`CAMPFIRE_RESPONSE_CACHE_MB=0` on both, confirmed): same command, report
`/tmp/q11-gates/cable-off.json`. 16 scenarios, `success: true`.

## State

Mutating families on a fresh default seed, host directories bind-mounted at
`/rails/storage/{db,files}`, ports 3184/3284 mapped to container 3000. `first_run` is excluded
here and run on its own seed above. The diff waits up to 15s for `Room::DestroyJob` to land.

```
parity/state/run --replay http://127.0.0.1:3184 http://127.0.0.1:3284 \
  --ref-storage /tmp/q11-state-on/ref --cand-storage /tmp/q11-state-on/cand \
  --family sessions,join,account,bots,users,rooms,messages,bot_api,searches
```

Cache on: `state matched` after every family, `STATE_ON_EXIT:0`. Rooms matched on the first
retry, after the destroy job committed.

Cache off (`CAMPFIRE_RESPONSE_CACHE_MB=0` on both): the same command against a fresh copy.
`state matched` after every family, `STATE_OFF_EXIT:0`. Rooms again matched once the destroy
job had committed.

## Continuity

The two servers share one SQLite database and one storage directory. A session row, a message,
and a profile write are visible to the other process only on that shared install. Separate
copies fail session handoff for that reason; that is the install contract, not a second
database.

Cache on, shared files, ports 3196/3296 mapped to container 3000:

```
parity/continuity/run http://127.0.0.1:3196 http://127.0.0.1:3296 --report /tmp/q11-gates/continuity-shared.json
```

14/14 passed, both directions.

Cache off (`CAMPFIRE_RESPONSE_CACHE_MB=0` on both, confirmed), fresh shared copy:

```
parity/continuity/run http://127.0.0.1:3196 http://127.0.0.1:3296 --report /tmp/q11-gates/continuity-off.json
```

14/14 passed, both directions.

## Playwright workflows

`tests/workflows/run --server URL`. Reference is `parity/bin/reference up` (Thruster on 80) at
`127.0.0.1:3202`. Candidate is `dotnet run tests/workflows/Server.cs` at `127.0.0.1:3204` on its
own seed copy. Port 3100 was left alone.

Cache on: reference 19/19 (`/tmp/q11-gates/workflows-ref-on.json`), candidate 19/19
(`workflows-cand-on.json`).

Cache off (`CAMPFIRE_RESPONSE_CACHE_MB=0` on both; reference `printenv` was `0`): reference
19/19 (`workflows-ref-off.json`), candidate 19/19 (`workflows-cand-off.json`).

## What changed so the gates pass

- First run registers `Rooms::Open#grant_access_to_all_users` before
  `User#grant_membership_to_open_rooms`. The room's `insert_all` stamps
  `STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW')` and wins; a traced edbc779 run does the same. The
  user's later grant conflicts and does nothing.
- Rich text plain text, canonical figures, and missing-attachable `☒` follow the pin. Static
  files go through `Rack::Deflater`. Error pages use `charset=UTF-8`. Avatars and logos stay
  `ActionController::Live` (`Sec-Fetch-Site` stays on `Vary`). The response cache does not store
  `Link`; a hit omits it, as Rails does.
- The screen forwarder, transfer-label resigner, live-image `Vary` retry, and state settle are
  harness-only. Product responses are unchanged by those.
