"""Messages: pages of a room's messages, a message's edit form, posting, editing, deleting and
boosting, and the bot API (MessagesController, Messages::BoostsController, Messages::ByBotsController,
Messages::Boosts::ByBotsController)."""

from . import family

STREAM = {"Accept": "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"}


@family("messages:read")
def message_pages(run):
    """Message pages (last, before, after), single messages, edit forms and boosts."""
    david = run.signed_in("david", "203.0.113.91")
    kevin = run.signed_in("kevin", "203.0.113.92")
    watercooler, designers = run.label("rooms.watercooler"), run.label("rooms.designers")
    busy = run.label("messages.busy_060")
    for query in ["", f"?before={busy}", f"?after={busy}", f"?before={run.label('messages.busy_001')}",
                  f"?after={run.label('messages.thirteenth')}", "?before=999"]:
        run.compare(f"david GET /rooms/{{watercooler}}/messages{query}", david.get(f"/rooms/{watercooler}/messages{query}"))
    etags = [r.header("etag") for r in david.get(f"/rooms/{watercooler}/messages")]
    run.compare("david GET /rooms/{watercooler}/messages If-None-Match",
                [b.get(f"/rooms/{watercooler}/messages", {"If-None-Match": e or ""}) for b, e in zip(david.browsers, etags)])
    run.compare("david GET /rooms/{designers}/messages", david.get(f"/rooms/{designers}/messages"))
    run.compare("david GET /rooms/{quiet}/messages (empty)", david.get(f"/rooms/{run.label('rooms.quiet')}/messages"))
    run.compare("kevin GET /rooms/{watercooler}/messages (not a member)", kevin.get(f"/rooms/{watercooler}/messages"))
    for label in ["plain", "long", "mention", "image", "video", "file", "boosted_many", "edited", "bot"]:
        mid = run.label(f"messages.{label}")
        run.compare(f"david GET /rooms/{{designers}}/messages/{{{label}}}", david.get(f"/rooms/{designers}/messages/{mid}"))
    for label in ["plain", "boosted_by_david"]:
        mid = run.label(f"messages.{label}")
        run.compare(f"david GET /rooms/{{designers}}/messages/{{{label}}}/edit", david.get(f"/rooms/{designers}/messages/{mid}/edit"))
        run.compare(f"david GET /messages/{{{label}}}/boosts", david.get(f"/messages/{mid}/boosts"))
        run.compare(f"david GET /messages/{{{label}}}/boosts/new", david.get(f"/messages/{mid}/boosts/new"))
    run.compare("kevin GET edit of someone else's message", kevin.get(f"/rooms/{designers}/messages/{run.label('messages.plain')}/edit"))


@family("messages", mutates=True)
def message_writes(run):
    """Posting, editing, deleting and boosting messages."""
    david = run.signed_in("david", "203.0.113.93")
    kevin = run.signed_in("kevin", "203.0.113.94")
    hq, designers = run.label("rooms.hq"), run.label("rooms.designers")
    room = f"/rooms/{hq}"
    fields = [("message[body]", "<div>Replayed <strong>hello</strong> @here</div>"), ("message[client_message_id]", "6f1c2c56-0d34-4a52-9b6b-0d6a1f3f4b10")]
    run.compare("POST /rooms/<id>/messages", david.form("post", f"{room}/messages", david.tokens(room, lambda r: r.form_token(f"{room}/messages")), fields, STREAM))
    run.compare("  then GET /rooms/<id>/messages", david.get(f"{room}/messages"))
    run.compare("  then GET /rooms/<id>", david.get(room))
    run.compare("POST /rooms/<id>/messages with an empty body", david.form("post", f"{room}/messages", david.tokens(room, lambda r: r.form_token(f"{room}/messages")),
                                                                          [("message[body]", ""), ("message[client_message_id]", "c0c2b4f2-6a0e-4b8e-8f9f-1e2d3c4b5a60")], STREAM))
    run.compare("POST /rooms/999/messages", david.form("post", "/rooms/999/messages", david.tokens(room, lambda r: r.meta_token()), fields, STREAM))
    plain = run.label("messages.plain")
    edit = f"/rooms/{designers}/messages/{plain}"
    run.compare("PATCH /rooms/<id>/messages/<id>", david.submit(f"{edit}/edit", edit, "patch", [("message[body]", "<div>Edited by replay</div>")]))
    run.compare("  then GET the message", david.get(edit))
    run.compare("kevin PATCH someone else's message", kevin.form("patch", edit, kevin.tokens(f"/rooms/{designers}", lambda r: r.meta_token()), [("message[body]", "x")]))
    run.compare("DELETE /rooms/<id>/messages/<id>", david.form("delete", f"/rooms/{designers}/messages/{run.label('messages.unboosted')}",
                                                               david.tokens(f"/rooms/{designers}", lambda r: r.meta_token()), headers=STREAM))
    run.compare("  then GET /rooms/{designers}/messages", david.get(f"/rooms/{designers}/messages"))

    boosted = run.label("messages.boosted_one")
    run.compare("POST /messages/<id>/boosts", david.submit(f"/messages/{boosted}/boosts/new", f"/messages/{boosted}/boosts", "post", [("boost[content]", "🎉")]))
    run.compare("  then GET /messages/<id>/boosts", david.get(f"/messages/{boosted}/boosts"))
    mine = run.labels.get("boosts.david_on_boosted_by_david")
    if mine:
        target = run.label("messages.boosted_by_david")
        run.compare("DELETE /messages/<id>/boosts/<id>", david.form("delete", f"/messages/{target}/boosts/{mine}",
                                                                    david.tokens(f"/rooms/{designers}", lambda r: r.meta_token()), headers=STREAM))
    run.compare("kevin POST boost on an unreachable message", kevin.form("post", f"/messages/{run.label('messages.bot_in_watercooler')}/boosts",
                                                                          kevin.tokens(f"/rooms/{hq}", lambda r: r.meta_token()), [("boost[content]", "x")]))


@family("bot_api", mutates=True)
def bot_api(run):
    """The bot API: messages by key (index, create, update, destroy) and boosts."""
    anon = run.pair("203.0.113.95")
    key = run.label("bot_keys.bender")
    watercooler = run.label("rooms.watercooler")
    base = f"/rooms/{watercooler}/{key}/messages"
    for query in ["", f"?before={run.label('messages.busy_060')}", f"?after={run.label('messages.busy_060')}"]:
        run.compare(f"GET {{bot}}/messages{query}", anon.get(base + query))
    run.compare("GET with a wrong bot key", anon.get(f"/rooms/{watercooler}/{run.label('users.bender')}-wrongwrong12/messages"))
    run.compare("GET a room the bot isn't in", anon.get(f"/rooms/{run.label('rooms.designers')}/{key}/messages"))
    created = anon.request("POST", base, "Hello from the replay", {"Content-Type": "text/plain"})
    run.compare("POST {bot}/messages", created)
    run.compare("POST {bot}/messages with no body", anon.request("POST", base, "", {"Content-Type": "text/plain"}))
    locations = [r.header("location") or "" for r in created]
    ids = [loc.rstrip("/").split("/")[-1] for loc in locations]
    if all(i.isdigit() for i in ids):
        run.compare("PUT {bot}/messages/<id>", [b.request("PUT", f"{base}/{i}", "Edited", {"Content-Type": "text/plain"}) for b, i in zip(anon.browsers, ids)])
        run.compare("POST {bot}/messages/<id>/boosts", [b.request("POST", f"{base}/{i}/boosts", "👍", {"Content-Type": "text/plain"}) for b, i in zip(anon.browsers, ids)])
        run.compare("DELETE {bot}/messages/<id>", [b.request("DELETE", f"{base}/{i}") for b, i in zip(anon.browsers, ids)])
    run.compare("  then GET {bot}/messages", anon.get(base))
