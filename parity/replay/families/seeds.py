"""Families for the other seeds: first run on an empty database, and the crowd seed's pagination
(FirstRunsController, Accounts::UsersController#index, Autocompletable::UsersController)."""

from . import family
from .account import PNG


@family("first_run", seed="first_run", mutates=True)
def first_run(run):
    """First run: the redirects on an empty database, creating the owner, then the redirects again."""
    browsers = run.pair("203.0.113.120")
    for path in ["/session/new", "/", "/first_run", "/webmanifest.json", "/account/logo", "/up"]:
        run.compare(f"first run GET {path}", browsers.get(path))
    tokens = browsers.tokens("/first_run", lambda r: r.form_token("/first_run"))
    fields = [("user[name]", "Owner"), ("user[email_address]", "owner@example.com"), ("user[password]", run.password)]
    run.compare("POST /first_run", browsers.multipart("post", "/first_run", tokens, fields, [("user[avatar]", "me.png", "image/png", PNG)]))
    for path in ["/", "/first_run", "/users/me/profile", "/account/edit"]:
        run.compare(f"after first run GET {path}", browsers.get(path))
    run.compare("POST /first_run again", browsers.form("post", "/first_run", tokens, fields), body=False)


@family("crowd", seed="crowd")
def crowd(run):
    """520 extra members: the account user list's pages, autocompletion pages and the sidebar's
    direct placeholders."""
    admins = run.signed_in("david", "203.0.113.130")
    members = run.signed_in("kevin", "203.0.113.131")
    for path in ["/account/edit", "/users/me/sidebar", "/autocompletable/users", "/autocompletable/users.json",
                 "/autocompletable/users.json?page=2", "/autocompletable/users.json?page=27", "/autocompletable/users.json?page=99",
                 "/autocompletable/users.json?query=a&page=3&z=1", "/autocompletable/users?filter=Ada",
                 f"/rooms/{run.label('rooms.hq')}", f"/autocompletable/users?room_id={run.label('rooms.hq')}"]:
        run.compare(f"crowd admin GET {path}", admins.get(path))
    for page in ["1", "2", "3", "x"]:
        run.compare(f"crowd admin GET /account/users?page={page} (turbo stream)",
                    admins.get(f"/account/users?page={page}", {"Accept": "text/vnd.turbo-stream.html"}))
    for path in ["/account/edit", "/users/me/sidebar"]:
        run.compare(f"crowd member GET {path}", members.get(path))
