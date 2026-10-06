"""Route families: each is a scripted session over one group of routes, named after the
controllers it exercises, so a feature task replays just the routes it owns.

A family is a function taking a replay.Run. It makes its own users (Run.pair / Run.signed_in, each
on its own client IP, because sign-in is rate limited per IP) and calls Run.compare on every
response pair. Families that write run after the read-only ones; every write goes to both servers,
so a family sees the same state on both whatever ran before it. Start from a fresh copy of the seed
(compose.parity.yml recopies it on every `up`).
"""

FAMILIES = {}


class Family:
    def __init__(self, name, fn, seed, mutates, doc):
        self.name, self.fn, self.seed, self.mutates, self.doc = name, fn, seed, mutates, doc


def family(name, seed="default", mutates=False):
    def register(fn):
        FAMILIES[name] = Family(name, fn, seed, mutates, (fn.__doc__ or "").strip().splitlines()[0])
        return fn
    return register


def for_seed(seed):
    """The seed's families, read-only first, in registration order otherwise."""
    chosen = [f for f in FAMILIES.values() if f.seed == seed]
    return sorted(chosen, key=lambda f: f.mutates)


from . import sessions, account, users, rooms, messages, searches, pwa, seeds  # noqa: E402,F401
