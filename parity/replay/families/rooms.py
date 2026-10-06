"""Rooms: the room page, its refresh, settings and involvement, and creating, editing and deleting
open, closed and direct rooms (RoomsController, Rooms::*, WelcomeController)."""

from . import family

ROOMS = ["watercooler", "designers", "hq", "pets", "archive", "quiet", "broken", "david_and_jason", "david_and_kevin",
         "group_direct"]


@family("rooms:read")
def room_pages(run):
    """The room page for every kind of seeded room, at a message, refreshed, and its settings."""
    david = run.signed_in("david", "203.0.113.81")
    kevin = run.signed_in("kevin", "203.0.113.82")
    loner = run.signed_in("loner", "203.0.113.83")
    run.compare("david GET /", david.get("/"))
    run.compare("loner GET / (no rooms)", loner.get("/"))
    run.compare("david GET /rooms", david.get("/rooms"))
    for room in ROOMS:
        rid = run.label(f"rooms.{room}")
        run.compare(f"david GET /rooms/{{{room}}}", david.get(f"/rooms/{rid}"))
    hq, watercooler, designers = (run.label(f"rooms.{r}") for r in ("hq", "watercooler", "designers"))
    run.compare("david GET /rooms/{watercooler}/@{busy_060}", david.get(f"/rooms/{watercooler}/@{run.label('messages.busy_060')}"))
    run.compare("david GET /rooms/{watercooler}/@999", david.get(f"/rooms/{watercooler}/@999"))
    run.compare("kevin GET /rooms/{designers} (unread)", kevin.get(f"/rooms/{designers}"))
    run.compare("kevin GET /rooms/{watercooler} (not a member)", kevin.get(f"/rooms/{watercooler}"))
    run.compare("david GET /rooms/999", david.get("/rooms/999"))
    run.compare("david GET /rooms/{designers} in a Turbo frame", david.get(f"/rooms/{designers}", {"Turbo-Frame": "frame"}))
    for since in ["0", run.labels.get("clock.now_ms", "1772467200000"), "x"]:
        run.compare(f"david GET /rooms/{{watercooler}}/refresh?since={since}", david.get(f"/rooms/{watercooler}/refresh?since={since}", {"Accept": "text/vnd.turbo-stream.html"}))
    for room in ["hq", "designers", "david_and_jason"]:
        rid = run.label(f"rooms.{room}")
        run.compare(f"david GET /rooms/{{{room}}}/settings", david.get(f"/rooms/{rid}/settings"))
        run.compare(f"david GET /rooms/{{{room}}}/involvement", david.get(f"/rooms/{rid}/involvement"))
    for path in ["/rooms/opens/new", "/rooms/closeds/new", "/rooms/directs/new", f"/rooms/opens/{hq}/edit", f"/rooms/closeds/{designers}/edit",
                 f"/rooms/opens/{hq}", f"/rooms/closeds/{designers}", f"/rooms/directs/{run.label('rooms.david_and_jason')}/edit",
                 f"/rooms/directs/{hq}/edit", f"/rooms/closeds/{run.label('rooms.david_and_jason')}/edit"]:
        run.compare(f"david GET {path}", david.get(path))
    run.compare("kevin GET /rooms/closeds/{designers}/edit", kevin.get(f"/rooms/closeds/{designers}/edit"))


@family("rooms", mutates=True)
def room_writes(run):
    """Involvement changes and creating, converting, renaming and deleting rooms."""
    david = run.signed_in("david", "203.0.113.84")
    kevin = run.signed_in("kevin", "203.0.113.85")
    hq, designers, archive = (run.label(f"rooms.{r}") for r in ("hq", "designers", "archive"))
    for room, involvement in [(hq, "everything"), (archive, "mentions"), (designers, "invisible")]:
        path = f"/rooms/{room}/involvement?involvement={involvement}"
        run.compare(f"PUT /rooms/<id>/involvement?involvement={involvement}",
                    david.form("put", path, david.tokens(f"/rooms/{room}/involvement", lambda r: r.meta_token())))
        run.compare("  then GET /users/me/sidebar", david.get("/users/me/sidebar"))

    users = [("user_ids[]", str(run.label(f"users.{u}"))) for u in ("david", "kevin", "jz")]
    run.compare("POST /rooms/opens", david.submit("/rooms/opens/new", "/rooms/opens", "post", [("room[name]", "Replay open")]))
    run.compare("  then GET /", david.get("/"))
    run.compare("POST /rooms/closeds", david.submit("/rooms/closeds/new", "/rooms/closeds", "post", [("room[name]", "Replay closed")] + users))
    run.compare("  then kevin GET /users/me/sidebar", kevin.get("/users/me/sidebar"))
    # The edit page's room type toggle points the form at the other controller; its JavaScript
    # sends the page's token.
    run.compare("PATCH /rooms/opens/<id> (closed to open)", david.form("patch", f"/rooms/opens/{designers}",
                david.tokens(f"/rooms/closeds/{designers}/edit", lambda r: r.meta_token()), [("room[name]", "Designers!")]))
    run.compare("PATCH /rooms/closeds/<id> (open to closed)", david.form("patch", f"/rooms/closeds/{hq}",
                david.tokens(f"/rooms/opens/{hq}/edit", lambda r: r.meta_token()), [("room[name]", "HQ")] + users[:2]))
    run.compare("  then GET /rooms/<hq>", david.get(f"/rooms/{hq}"))
    run.compare("kevin PATCH /rooms/opens/<id> (not the creator)", kevin.form("patch", f"/rooms/opens/{run.label('rooms.pets')}",
                                                                               kevin.tokens(f"/rooms/{hq}", lambda r: r.meta_token()), [("room[name]", "x")]))
    run.compare("POST /rooms/directs", david.submit("/rooms/directs/new", "/rooms/directs", "post", [("user_ids[]", str(run.label("users.jz")))]))
    run.compare("POST /rooms/directs (existing)", david.submit("/rooms/directs/new", "/rooms/directs", "post", [("user_ids[]", str(run.label("users.jason")))]))
    quiet = run.label("rooms.quiet")
    pets = run.label("rooms.pets")
    run.compare("kevin DELETE /rooms/{pets} (not the creator)", kevin.form("delete", f"/rooms/{pets}", kevin.tokens(f"/rooms/{quiet}", lambda r: r.meta_token())))
    run.compare("kevin DELETE /rooms/{quiet}", kevin.form("delete", f"/rooms/{quiet}", kevin.tokens(f"/rooms/{quiet}", lambda r: r.meta_token())))
    run.compare("  then GET /users/me/sidebar", kevin.get("/users/me/sidebar"))
