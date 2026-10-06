"""The account: settings, custom styles, logo, join code, users and bots
(AccountsController and Accounts::*)."""

import os

from . import family
from replay import ROOT

PNG = open(os.path.join(ROOT, "reference", "app", "assets", "images", "campfire-icon.png"), "rb").read()


@family("account:read")
def account_pages(run):
    """Account pages for an administrator and a member, in and out of Turbo frames."""
    admins = run.signed_in("david", "203.0.113.61")
    members = run.signed_in("kevin", "203.0.113.62")
    bender = run.label("users.bender")
    pages = ["/account/edit", "/account/bots", "/account/bots/new", f"/account/bots/{bender}/edit", f"/account/bots/{bender}",
             "/account/custom_styles/edit", "/account/users", "/account/users?page=2", "/account/users.turbo_stream?page=2",
             f"/account/users/{run.label('users.kevin')}", "/account/join_code", "/account/logo", "/account/logo?size=small",
             "/account/logo.png", "/account/bots/999/edit"]
    for path in pages:
        run.compare(f"admin GET {path}", admins.get(path))
    for path in ["/account/edit", "/account/bots", "/account/custom_styles/edit"]:
        run.compare(f"admin GET {path} in a Turbo frame", admins.get(path, {"Turbo-Frame": "frame"}))
    run.compare("admin GET /account/users as a turbo stream", admins.get("/account/users?page=2", {"Accept": "text/vnd.turbo-stream.html"}))
    run.compare("admin GET /account/logo with Accept: */*", admins.get("/account/logo", {"Accept": "*/*"}))
    etags = [r.header("etag") for r in admins.get("/account/logo?size=small")]
    run.compare("admin GET /account/logo?size=small If-None-Match", [b.get("/account/logo?size=small", {"If-None-Match": e or ""}) for b, e in zip(admins.browsers, etags)])
    for path in ["/account/edit", "/account/bots", "/account/bots/new", "/account/custom_styles/edit"]:
        run.compare(f"member GET {path}", members.get(path))


@family("account", mutates=True)
def account_writes(run):
    """Account settings, custom styles, logo and join code changes, user roles and removal."""
    admins = run.signed_in("david", "203.0.113.63")
    members = run.signed_in("kevin", "203.0.113.64")
    action = f"/account.{run.label('accounts.signal')}"
    run.compare("PATCH /account", admins.submit("/account/edit", action, "patch", [("account[name]", "Signal HQ")]))
    run.compare("  then GET /account/edit (notice)", admins.get("/account/edit"))
    run.compare("PATCH /account settings", admins.submit("/account/edit", action, "patch", [("account[settings][restrict_room_creation_to_administrators]", "true")]))
    run.compare("  then GET /users/me/sidebar", admins.get("/users/me/sidebar"))
    run.compare("  then member GET /rooms/opens/new", members.get("/rooms/opens/new"))
    run.compare("PATCH /account/custom_styles", admins.submit("/account/custom_styles/edit", "/account/custom_styles", "patch", [("account[custom_styles]", "body { --color: red; }")]))
    run.compare("  then GET /account/custom_styles/edit", admins.get("/account/custom_styles/edit"))
    run.compare("member PATCH /account (forbidden)", members.form("patch", action, members.tokens("/account/edit", lambda r: r.meta_token()), [("account[name]", "x")]))
    run.compare("PATCH /account with a logo", admins.multipart("patch", action, admins.tokens("/account/edit", lambda r: r.form_token(action)), [], [("account[logo]", "logo.png", "image/png", PNG)]))
    run.compare("  then GET /account/edit", admins.get("/account/edit"))
    run.compare("  then GET /account/logo", admins.get("/account/logo?size=small"), body=False)
    run.compare("DELETE /account/logo", admins.press("/account/edit", "/account/logo?", "delete"))
    run.compare("POST /account/join_code", admins.submit("/account/edit", "/account/join_code"))
    run.compare("  then GET /account/edit", admins.get("/account/edit"))

    jz = run.label("users.jz")
    run.compare("PATCH /account/users/<id> (role)", admins.submit("/account/edit", f"/account/users/{jz}", "patch", [("user[role]", "administrator")]))
    run.compare("member PATCH /account/users/<id> (forbidden)", members.form("patch", f"/account/users/{jz}", members.tokens("/account/edit", lambda r: r.meta_token()), [("user[role]", "member")]))
    loner = run.label("users.loner")
    run.compare("DELETE /account/users/<id>", admins.press("/account/edit", f"/account/users/{loner}", "delete"))
    run.compare("  then GET /account/edit", admins.get("/account/edit"))


@family("bots", mutates=True)
def bots(run):
    """Creating, editing, re-keying and removing bots (Accounts::BotsController, Bots::KeysController)."""
    admins = run.signed_in("david", "203.0.113.65")
    bender = run.label("users.bender")
    run.compare("POST /account/bots", admins.submit("/account/bots/new", "/account/bots", "post", [("user[name]", "Robo"), ("user[webhook_url]", "https://example.com/robo")]))
    run.compare("  then GET /account/bots", admins.get("/account/bots"))
    run.compare("POST /account/bots with no name", admins.submit("/account/bots/new", "/account/bots", "post", [("user[name]", ""), ("user[webhook_url]", "")]))
    run.compare("PATCH /account/bots/<id>", admins.submit(f"/account/bots/{bender}/edit", f"/account/bots/{bender}", "patch", [("user[name]", "Bender B"), ("user[webhook_url]", "")]))
    run.compare("  then GET /account/bots/<id>/edit", admins.get(f"/account/bots/{bender}/edit"))
    run.compare("PUT /account/bots/<id>/key", admins.press(f"/account/bots/{bender}/edit", f"/account/bots/{bender}/key", "put"))
    run.compare("  then GET /account/bots/<id>/edit", admins.get(f"/account/bots/{bender}/edit"))
    run.compare("POST /account/bots with an avatar", admins.multipart("post", "/account/bots", admins.tokens("/account/bots/new", lambda r: r.form_token("/account/bots")),
                                                                      [("user[name]", "Pixel"), ("user[webhook_url]", "")], [("user[avatar]", "bot.png", "image/png", PNG)]))
    run.compare("  then GET /account/bots", admins.get("/account/bots"))
    deploy = run.label("users.deploy_bot")
    run.compare("DELETE /account/bots/<id>", admins.press(f"/account/bots/{deploy}/edit", f"/account/bots/{deploy}", "delete"))
    run.compare("  then GET /account/bots", admins.get("/account/bots"))
