# Cross-Server Continuity Harness (Q06)

Validates cross-server continuity between the reference Rails app and the C# port on the same SQLite database and storage tree, in both directions:
1. **Rails -> C#**: Minted/rendered on Rails, consumed/submitted/verified on C#.
2. **C# -> Rails**: Minted/rendered on C#, consumed/submitted/verified on Rails.

## Scenarios

The harness executes 7 scenarios in both directions (14 scenario checks in total):

1. **`session_continuity`**:
   - Sign in on Server A (`POST /session`).
   - Transfer cookies (`session_token`, `_campfire_session`) to Server B.
   - Access root `/` and room pages on Server B; assert session is recognized and authenticated user is returned.
   - Sign out on Server B; verify session is invalidated across both servers.
2. **`form_login_continuity`**:
   - Render unauthenticated login form on Server A (`GET /session/new`).
   - Extract `authenticity_token` and `_campfire_session`.
   - Submit credentials to Server B (`POST /session`); assert 302 Found and `session_token` cookie issuance.
   - Verify signed-in access on Server B.
3. **`form_message_continuity`**:
   - Render room on Server A with authenticated session (`GET /rooms/<id>`).
   - Extract per-form `authenticity_token` for `/rooms/<id>/messages`.
   - Post message to Server B (`POST /rooms/<id>/messages`) with Server A's CSRF token and cookies.
   - Assert 200/302 response on Server B and verify message appears on Server A.
4. **`form_profile_update_continuity`**:
   - Render profile page on Server A with authenticated user (`GET /users/me/profile`).
   - Extract CSRF token and submit profile update to Server B (`PATCH /users/<id>/profile`).
   - Assert update succeeds on Server B and changes are reflected on Server A.
5. **`transfer_link_continuity`**:
   - Mint a 4-hour session transfer link on Server A (`/session/transfers/<token>`).
   - Access transfer link on Server B from a new unauthenticated browser session.
   - Submit transfer on Server B (`PUT /session/transfers/<token>`); assert 302 Found redirect to root.
   - Verify subsequent requests on Server B are authenticated as the transferred user.
6. **`security_csrf_tamper_continuity`**:
   - Render form on Server A and tamper with the authenticity token.
   - Submit to Server B; assert rejection with `422 Unprocessable Entity` (`InvalidAuthenticityToken`).
7. **`security_transfer_tamper_continuity`**:
   - Mint transfer link on Server A and tamper with the signed token.
   - Submit redemption to Server B (`PUT /session/transfers/<tampered_token>`); assert rejection with `400 Bad Request`.

## Running Offline Unit Tests

```bash
parity/continuity/run --selftest
# or:
python3 -m unittest discover -s parity/continuity -p 'test_*.py'
```

## Running Live Cross-Server Continuity

```bash
# 1. Start reference Rails on port 3100
parity/bin/reference up --seed default --port 3100 --time 2026-03-02T16:00:00Z

# 2. Start C# candidate on port 3200 against the same instance storage and synchronized clock
FAKETIME="@2026-03-02 16:00:00" dotnet run parity/continuity/Server.cs -- \
  3200 parity/.seed/.instances/3100/db/production.sqlite3 parity/.seed/.instances/3100/storage artifacts/assets

# 3. Run continuity checks across both servers
parity/continuity/run http://127.0.0.1:3100 http://127.0.0.1:3200 --direction both --report parity/continuity/results.json

# 4. Stop servers when finished
parity/bin/reference down --port 3100
```
