"""Signing in and out, session transfers, joining with the join code, first run redirects
(SessionsController, Sessions::TransfersController, UsersController#new/create)."""

import re

from . import family


@family("sessions:anon")
def signed_out(run):
    """Signed-out pages and redirects."""
    anon = run.pair("203.0.113.50")
    join = f"/join/{run.label('join_codes.signal')}"
    for path in ["/session/new", "/session/new?email_address=x%40y.com", "/", "/first_run", join, "/join/nope",
                 "/account/edit", "/session/transfers/bogus", "/users/me/profile", "/session/new.json",
                 f"/rooms/{run.label('rooms.watercooler')}", "/searches", "/up"]:
        run.compare(f"anon GET {path}", anon.get(path))
    for path in ["/session/new", join, "/first_run"]:
        run.compare(f"anon GET {path} in a Turbo frame", anon.get(path, {"Turbo-Frame": "frame"}))
    run.compare(f"anon GET /session/transfers/{{david}}", anon.get(f"/session/transfers/{run.label('transfers.david')}"))
    run.compare(f"anon GET /session/transfers/{{david_expired}}", anon.get(f"/session/transfers/{run.label('transfers.david_expired')}"))


@family("sessions", mutates=True)
def sessions(run):
    """Signing in (wrong password, bad token, rate limit, return_to), transfers and signing out."""
    david = run.label("emails.david")
    anon = run.pair("203.0.113.50")
    tokens = anon.tokens("/session/new", lambda r: r.form_token("/session"))
    run.compare("POST /session with a wrong password", anon.form("post", "/session", tokens, [("email_address", david), ("password", "nope")]))
    run.compare("POST /session with no password", anon.form("post", "/session", tokens, [("email_address", david)]))
    run.compare("POST /session with a bad token", anon.form("post", "/session", ["bogus", "bogus"], [("email_address", david), ("password", run.password)]), body=False)
    run.compare("POST /session with no token", anon.form("post", "/session", [None, None], [("email_address", david), ("password", run.password)]), body=False)

    # rate_limit to: 10, within: 3.minutes: the 11th sign-in attempt from one IP is 429.
    limited = run.pair("203.0.113.98")
    tokens = limited.tokens("/session/new", lambda r: r.form_token("/session"))
    for _ in range(10):
        limited.form("post", "/session", tokens, [("email_address", david), ("password", "nope")])
    run.compare("POST /session, 11th attempt (rate limited)", limited.form("post", "/session", tokens, [("email_address", david), ("password", run.password)]))

    # A banned IP (bans.mallory) can't sign in.
    banned = run.pair(run.labels.get("ips.banned", "203.0.113.9"))
    run.compare("banned IP GET /session/new", banned.get("/session/new"))

    admins = run.pair("203.0.113.51")
    admins.get("/account/edit")
    tokens = admins.tokens("/session/new", lambda r: r.form_token("/session"))
    run.compare("sign in (returns to the page asked for)", admins.form("post", "/session", tokens, [("email_address", david), ("password", run.password)]))
    run.compare("  then GET /session/new (signed in)", admins.get("/session/new"))

    # Transfers: the link on a user's own page signs another device in.
    kevin = run.signed_in("kevin", "203.0.113.52")
    transfers = [re.search(r"/session/transfers/([^\"]+)\"", r.text()).group(1) for r in kevin.get("/users/me/profile")]
    phones = run.pair("203.0.113.55")
    paths = [f"/session/transfers/{t}" for t in transfers]
    run.compare("GET /session/transfers/<id>", [b.get(p) for b, p in zip(phones.browsers, paths)])
    run.compare("PUT /session/transfers/<id>", [b.form("put", p, b.get(p).form_token(p), []) for b, p in zip(phones.browsers, paths)])
    run.compare("  then GET /users/me/profile", phones.get("/users/me/profile"))
    run.compare("PUT /session/transfers/{david_expired}", phones.form("put", f"/session/transfers/{run.label('transfers.david_expired')}",
                                                                     phones.tokens("/users/me/profile", lambda r: r.meta_token())), body=False)

    run.compare("DELETE /session", admins.submit("/users/me/profile", "/session", "delete"))
    run.compare("  then GET /users/me/profile", admins.get("/users/me/profile"))


@family("join", mutates=True)
def join(run):
    """Joining the account with its join code (UsersController#new/create)."""
    join = f"/join/{run.label('join_codes.signal')}"
    joiners = run.pair("203.0.113.53")
    run.compare("GET /join/<code>", joiners.get(join))
    run.compare("POST /join/<code>", joiners.submit(join, join, "post", [("user[name]", "New Person"), ("user[email_address]", "new@example.com"), ("user[password]", run.password)]))
    run.compare("  then GET /", joiners.get("/"))
    others = run.pair("203.0.113.54")
    run.compare("POST /join/<code> with a taken email", others.submit(join, join, "post", [("user[name]", "Dup"), ("user[email_address]", "new@example.com"), ("user[password]", run.password)]))
    run.compare("POST /join/<code> with no name", others.submit(join, join, "post", [("user[email_address]", "noname@example.com"), ("user[password]", run.password)]))
    run.compare("POST /join/<wrong code>", others.form("post", "/join/nope", others.tokens(join, lambda r: r.form_token(join)), []), body=False)
