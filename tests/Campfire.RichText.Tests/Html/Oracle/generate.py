#!/usr/bin/env python3
"""Regenerates gumbo-trees.jsonl: the trees Gumbo builds, as Nokogiri 1.19.4 builds Gumbo.

Gumbo is compiled from the nokogiri 1.19.4 gem's own gumbo-parser sources with the flags
ext/nokogiri/extconf.rb uses (-O2 -g and no NDEBUG, so Gumbo's debug-only branches run, as they
do in Rails). gumbo_dump.c parses each input the way ext/nokogiri/gumbo.c does and prints the tree.
The inputs are html5lib-tests' tree-construction cases (in their fragment context, in body, and
as documents) and seeded random markup aimed at Gumbo's corners.

Needs python3, curl, git, tar and a C compiler. Run from anywhere:
    tests/Campfire.RichText.Tests/Html/Oracle/generate.py
"""
import hashlib, json, os, random, struct, subprocess, tempfile, glob

HERE = os.path.dirname(os.path.abspath(__file__))
GEM_URL = "https://rubygems.org/downloads/nokogiri-1.19.4.gem"
GEM_SHA256 = "50c951611c92bca05c51411aef45f1cbc50f2821c4802758c5c6d34696533ab5"
HTML5LIB_TESTS = "https://github.com/html5lib/html5lib-tests"
# The last commit before the tree-construction tests moved to WPT
HTML5LIB_COMMIT = "9329e64694e7835d0dcff9811e22856ef6ad16f9"
FUZZ_SEED = 1
FUZZ_CASES = 1500


def run(*args, **kwargs):
    subprocess.run(args, check=True, **kwargs)


def build_gumbo_dump(work):
    gem = os.path.join(work, "nokogiri.gem")
    run("curl", "-sSfL", "-o", gem, GEM_URL)
    with open(gem, "rb") as f:
        assert hashlib.sha256(f.read()).hexdigest() == GEM_SHA256, "unexpected nokogiri gem"
    run("tar", "xf", gem, "-C", work)
    run("tar", "xzf", os.path.join(work, "data.tar.gz"), "-C", work, "gumbo-parser")
    src = os.path.join(work, "gumbo-parser", "src")
    binary = os.path.join(work, "gumbo_dump")
    run("cc", "-std=c99", "-O2", "-g", "-I", src, os.path.join(HERE, "gumbo_dump.c"),
        *sorted(glob.glob(os.path.join(src, "*.c"))), "-o", binary)
    return binary


def html5lib_cases(work):
    repo = os.path.join(work, "html5lib-tests")
    run("git", "init", "-q", repo)
    run("git", "-C", repo, "fetch", "-q", "--depth", "1", HTML5LIB_TESTS, HTML5LIB_COMMIT)
    run("git", "-C", repo, "checkout", "-q", "FETCH_HEAD")
    cases = []
    for path in sorted(glob.glob(os.path.join(repo, "tree-construction", "*.dat"))):
        text = open(path, encoding="utf-8").read()
        for test in text.split("\n\n#data\n"):
            test = test[len("#data\n"):] if test.startswith("#data\n") else test
            data = test.split("\n#errors")[0]
            if "#document-fragment\n" in test:
                context = test.split("#document-fragment\n")[1].split("\n")[0].replace(" ", ":")
                cases += [(context, data), ("body", data)]
            else:
                cases += [("", data), ("body", data)]
    return cases


TAGS = """a b i p div span table tr td th tbody thead caption col colgroup select option optgroup svg
math mi mtext foreignObject desc title textarea script style xmp iframe noscript noembed plaintext
template li ul ol dl dt dd h1 h2 pre listing form input button br img image hr nobr font em strong
code u s rb rt rp rtc ruby frameset frame head body html meta link base annotation-xml malignmark
mglyph clippath circle path action-text-attachment figure figcaption marquee object applet sarcasm
keygen wbr area embed search dialog""".split()
ATTRS = ["id", "class", "href", "xlink:href", "xml:lang", "xmlns", "xmlns:xlink", "viewbox",
         "definitionurl", "encoding", "type", "color", "face", "size", "title", "content", "TITLE",
         "a b", "=x", '"q']
VALUES = ["x", "text/html", "hidden", "application/xhtml+xml", "&amp;", "&notin", "&notit;",
          "&#x80;", "&#0;", "&#xD800;", "&lt", "a&b=c", "&amp=", "\r\n", "\n", "", "'", '"',
          " ", "\U0001F600"]
TEXTS = ["x", " ", "\n", "\r\n", "\r", "&", "&amp;", "&nbsp", "&#65;", "&#x110000;", "&#128;", "<",
         ">", "\0", " ", "\U0001F600", "﻿", "]]>", "<![CDATA[y]]>", "<!-- c -->", "<!--",
         "-->", "<!DOCTYPE html>", "<?pi?>", "</>", "</ >", "<a/b>", "&notin;", "&NotEqualTilde;",
         "&#x", "&#;"]
CONTEXTS = ["body", "body", "body", "div", "table", "tr", "td", "select", "svg:svg", "math:math",
            "textarea", "script", "title", "template", "html", "svg:foreignObject",
            "math:annotation-xml", "p", "pre", "style", "plaintext", "frameset", "head",
            "colgroup", "tbody", "caption", "form"]


def fuzz_markup(rng):
    out = []
    for _ in range(rng.randint(1, 40)):
        r = rng.random()
        if r < 0.45:
            tag = rng.choice(TAGS)
            if rng.random() < 0.3:
                tag = tag.upper()
            attributes = ""
            for _ in range(rng.randint(0, 3)):
                name, value = rng.choice(ATTRS), rng.choice(VALUES)
                quote = rng.choice(['"', "'", ""])
                if quote == "" and (" " in value or value == "" or ">" in value):
                    quote = '"'
                attributes += " " + name + ("=" + quote + value.replace(quote, "") + quote if rng.random() < 0.85 else "")
            out.append("<" + tag + attributes + ("/" if rng.random() < 0.1 else "") + ">")
        elif r < 0.7:
            out.append("</" + rng.choice(TAGS) + ">")
        else:
            out.append(rng.choice(TEXTS))
    return "".join(out)


def fuzz_cases():
    rng = random.Random(FUZZ_SEED)
    cases = [(rng.choice(CONTEXTS) if rng.random() < 0.9 else "", fuzz_markup(rng)) for _ in range(FUZZ_CASES)]
    # Gumbo's limits (deep trees that stay within them make for large dumps, so HtmlParserTests has those)
    cases += [("body", "<b>" * 401), ("body", "<b>" * 398 + "<table><td>"), ("", "<b>" * 401)]
    cases += [("body", "<p " + " ".join(f"a{i}=1" for i in range(n)) + ">") for n in (400, 401)]
    return cases


def main():
    with tempfile.TemporaryDirectory() as work:
        gumbo_dump = build_gumbo_dump(work)
        cases = html5lib_cases(work) + fuzz_cases()
        records = os.path.join(work, "records.bin")
        with open(records, "wb") as f:
            for context, data in cases:
                for part in (context.encode(), data.encode()):
                    f.write(struct.pack("<I", len(part)) + part)
        trees = subprocess.run([gumbo_dump, records], check=True, capture_output=True).stdout.decode()
    trees = trees.split("#end\n")[:-1]
    assert len(trees) == len(cases)
    with open(os.path.join(HERE, "gumbo-trees.jsonl"), "w", encoding="utf-8") as out:
        for (context, data), tree in zip(cases, trees):
            out.write(json.dumps({"context": context, "input": data, "tree": tree}, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
