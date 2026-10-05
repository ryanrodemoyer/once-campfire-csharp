# Rewrites askama templates to reproduce Erubi's statement-line trimming (ActionView's ERB):
# a line holding only statement tags (<% if %>, <% end %>, <% content_for do %>, ...) is removed
# entirely -- its indentation and its newline -- so in the askama source such a line's tags are
# moved to the start of the following line. Expression-like tags ({% filter %} = `<%= ... do %>`,
# {% include %} = `<%= render %>`) are left alone. Idempotent.
import re, sys

STATEMENTS = {"if", "elif", "else", "endif", "for", "endfor", "let", "endfilter", "match", "when",
              "endmatch", "block", "endblock"}
TAG = r"\{%[-~+]?\s*(\w+)[^%]*?[-~+]?%\}"

def statement_only(line):
    stripped = line.strip()
    if not stripped:
        return False
    tags = re.findall(r"\{%[-~+]?\s*(\w+)", stripped)
    rest = re.sub(TAG, "", stripped).strip()
    return rest == "" and tags and all(tag in STATEMENTS for tag in tags)

for path in sys.argv[1:]:
    source = open(path).read()
    source = re.sub(r"\s*-%\}", " %}", source)  # undo earlier trimming attempts
    lines = source.split("\n")
    out, pending = [], ""
    for index, line in enumerate(lines):
        if statement_only(line) and index < len(lines) - 1:
            pending += line.strip()
        else:
            out.append(pending + line)
            pending = ""
    if pending:
        out.append(pending)
    open(path, "w").write("\n".join(out))
