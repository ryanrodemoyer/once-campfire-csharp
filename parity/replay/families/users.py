"""People: profiles, user pages, avatars, bans, the sidebar, push subscriptions and autocompletion
(UsersController#show, Users::*, Autocompletable::UsersController)."""

import re

from . import family
from .account import PNG


@family("users:read")
def user_pages(run):
    """Profiles, user pages, avatars, the sidebar and autocompletion."""
    admins = run.signed_in("david", "203.0.113.71")
    members = run.signed_in("kevin", "203.0.113.72")
    ids = run.labels
    pages = ["/users/me/profile", "/users/me/profile/edit", "/users/me/sidebar", "/users/me/push_subscriptions",
             f"/users/{ids['users.kevin']}", f"/users/{ids['users.bender']}", f"/users/{ids['users.rita']}",
             f"/users/{ids['users.mallory']}", f"/users/{ids['users.david']}", "/users/999",
             f"/users/{ids['avatar_tokens.david']}/avatar", f"/users/{ids['avatar_tokens.jason']}/avatar",
             f"/users/{ids.get('avatar_tokens.deploy_bot', 'x')}/avatar", "/users/bogus/avatar",
             "/autocompletable/users", "/autocompletable/users?filter=j", "/autocompletable/users.json",
             "/autocompletable/users.json?query=e&page=1", "/autocompletable/users.json?page=2",
             f"/autocompletable/users?room_id={ids['rooms.watercooler']}", f"/autocompletable/users?room_id={ids['rooms.designers']}&filter=k"]
    for path in pages:
        run.compare(f"admin GET {path}", admins.get(path))
    for path in ["/users/me/profile", "/users/me/sidebar", f"/users/{ids['users.kevin']}"]:
        run.compare(f"admin GET {path} in a Turbo frame", admins.get(path, {"Turbo-Frame": "frame"}))
    run.compare("admin GET /users/me/sidebar in its Turbo frame", admins.get("/users/me/sidebar", {"Turbo-Frame": "user_sidebar"}))
    run.compare("admin GET /users/me/profile with a referrer", admins.get("/users/me/profile", {"Referer": f"http://campfire.test/rooms/{ids['rooms.hq']}"}))
    for path in ["/autocompletable/users", "/users/me/profile"]:
        run.compare(f"admin GET {path} with Accept: */*", admins.get(path, {"Accept": "*/*"}))
    run.compare("admin GET /autocompletable/users with Accept: application/json", admins.get("/autocompletable/users", {"Accept": "application/json"}))
    avatar = f"/users/{ids['avatar_tokens.jason']}/avatar"
    run.compare("admin GET avatar with image accepts", admins.get(avatar, {"Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8"}))
    etags = [r.header("etag") for r in admins.get(avatar)]
    run.compare("admin GET avatar If-None-Match", [b.get(avatar, {"If-None-Match": e or ""}) for b, e in zip(admins.browsers, etags)])
    for path in ["/users/me/profile", "/users/me/sidebar", f"/users/{ids['users.jz']}", f"/users/{ids['users.mallory']}"]:
        run.compare(f"member GET {path}", members.get(path))


@family("users", mutates=True)
def user_writes(run):
    """Profile and avatar changes, bans, push subscriptions."""
    admins = run.signed_in("david", "203.0.113.73")
    members = run.signed_in("kevin", "203.0.113.74")
    jz = run.label("users.jz")
    run.compare("PATCH /users/me/profile", admins.submit("/users/me/profile", "/users/me/profile", "patch", [("user[name]", "David H"), ("user[bio]", "Hi")]))
    run.compare("  then GET /users/me/profile", admins.get("/users/me/profile"))
    run.compare("PATCH /users/me/profile with a taken email", admins.submit("/users/me/profile", "/users/me/profile", "patch", [("user[email_address]", run.label("emails.kevin"))]))
    run.compare("POST /users/<id>/ban", admins.submit(f"/users/{jz}", f"/users/{jz}/ban"))
    run.compare("  then GET /users/<id>", admins.get(f"/users/{jz}"))
    run.compare("DELETE /users/<id>/ban", admins.press(f"/users/{jz}", f"/users/{jz}/ban", "delete"))
    run.compare("member POST /users/<id>/ban (forbidden)", members.form("post", f"/users/{jz}/ban", members.tokens("/users/me/profile", lambda r: r.meta_token())))

    run.compare("PATCH /users/me/profile with an avatar", members.multipart("patch", "/users/me/profile", members.tokens("/users/me/profile", lambda r: r.form_token("/users/me/profile")),
                                                                         [("user[name]", "Kevin")], [("user[avatar]", "me.png", "image/png", PNG)]))
    run.compare("  then GET /users/me/profile", members.get("/users/me/profile"))
    avatars = [re.search(r'src="(/users/[^"]+/avatar\?v=\d+)"', r.text()).group(1) for r in members.get("/users/me/profile")]
    run.compare("  then GET the avatar", [b.get(a) for b, a in zip(members.browsers, avatars)], body=False)
    paths = [a.split("?")[0] for a in avatars]
    run.compare("DELETE /users/<token>/avatar", [b.form("delete", p, b.get("/users/me/profile").button_token(p, "delete")) for b, p in zip(members.browsers, paths)])
    run.compare("  then GET /users/me/profile", members.get("/users/me/profile"))

    tokens = admins.tokens("/users/me/push_subscriptions", lambda r: r.meta_token())
    invalid = {"push_subscription": {"endpoint": "http://example.com/push", "p256dh_key": "a", "auth_key": "b"}}
    run.compare("POST /users/me/push_subscriptions (invalid endpoint)", admins.json("POST", "/users/me/push_subscriptions", invalid, tokens))
    pushers = run.signed_in("jason", "203.0.113.75")
    endpoint = "https://fcm.googleapis.com/fcm/send/replay-abc"
    valid = {"push_subscription": {"endpoint": endpoint, "p256dh_key": "BK", "auth_key": "au"}}
    for attempt in ["create", "existing"]:
        tokens = pushers.tokens("/users/me/push_subscriptions", lambda r: r.meta_token())
        run.compare(f"POST /users/me/push_subscriptions ({attempt})", pushers.json("POST", "/users/me/push_subscriptions", valid, tokens))
    run.compare("  then GET /users/me/push_subscriptions", pushers.get("/users/me/push_subscriptions"))
    run.compare("DELETE /session with push_subscription_endpoint", pushers.submit("/users/me/profile", "/session", "delete", [("push_subscription_endpoint", endpoint)]))
    pushers.sign_in(run.label("emails.jason"), run.password)
    run.compare("  then GET /users/me/push_subscriptions", pushers.get("/users/me/push_subscriptions"))
    david_chrome = run.labels.get("push_subscriptions.david_chrome")
    if david_chrome:
        path = f"/users/me/push_subscriptions/{david_chrome}"
        run.compare("DELETE /users/me/push_subscriptions/<id>", admins.press("/users/me/push_subscriptions", path, "delete"))
        run.compare("  then GET /users/me/push_subscriptions", admins.get("/users/me/push_subscriptions"))
