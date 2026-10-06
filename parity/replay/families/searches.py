"""Search (SearchesController)."""

from . import family


@family("searches:read")
def search_pages(run):
    """Search results and recent searches."""
    david = run.signed_in("david", "203.0.113.101")
    kevin = run.signed_in("kevin", "203.0.113.102")
    for query in ["", "?q=pizza", "?q=Pizza%21", "?q=busy", "?q=nothingmatches", "?q=%22quoted%22+OR", "?q=caf%C3%A9"]:
        run.compare(f"david GET /searches{query}", david.get(f"/searches{query}"))
    run.compare("kevin GET /searches?q=busy (unreachable rooms)", kevin.get("/searches?q=busy"))


@family("searches", mutates=True)
def search_writes(run):
    """Recording and clearing searches."""
    david = run.signed_in("david", "203.0.113.103")
    run.compare("POST /searches", david.submit("/searches", "/searches", "post", [("q", "replay pizza")]))
    run.compare("  then GET /searches?q=replay+pizza", david.get("/searches?q=replay+pizza"))
    run.compare("DELETE /searches/clear", david.press("/searches", "/searches/clear", "delete"))
    run.compare("  then GET /searches", david.get("/searches"))
