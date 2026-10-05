# once-campfire-csharp

Campfire in C#, ported from the Rails app in `reference/` (a submodule pinned to
`basecamp/once-campfire@345e637`). It must behave the same as Rails on the same SQLite database,
storage directory, cookies, URLs and frontend, so it can be benchmarked fairly next to the other
Campfire ports. `reference-rust/` (pinned to `basecamp/once-campfire-rust@ccece30`) supplies golden
vectors, the parity seed, the parity harness and the benchmark harness. The Go port,
<https://github.com/basecamp/once-campfire-go>, is a second worked example; read it on GitHub.

The plan is [`plans/csharp-port.md`](plans/csharp-port.md). The work is the task graph in
[`plans/tasks.yaml`](plans/tasks.yaml), one GitHub issue per task (mapped in
[`plans/issues.json`](plans/issues.json)).

## Rules

- **Never edit `reference/` or `reference-rust/`.** Port-owned frontend changes go in
  `assets/overrides/`.
- **The Rails app is the oracle.** Expected output comes from the reference: golden vectors,
  replay, golden helper output. When behavior depends on Rails or gem internals, read the gem source
  inside the reference image, not docs or memory. Hand-written expectations are only for
  independent security assertions (no `<script>`, no `on*` attributes, and so on).
- **Stay compatible with existing installs**: the SQLite schema, the storage layout, and signed and
  encrypted cookies, so people stay signed in across a switch from Rails.
- **Any difference from Rails is a bug** until it's listed under "Known differences" in `README.md`
  with the failing gate linked.
- **Never benchmark an incomplete response.** Every measured response must first be shown to be
  replay-equal to the reference. Never publish numbers before task B05.
- **Tests live beside the code.** Port the relevant `reference/test` cases. Run controller tests
  with CSRF protection on, since Rails' test environment turns it off.
- Write code that reads like the surrounding code: small, clearly named functions, and comments
  only where the behavior isn't obvious. Cite the reference file (`reference/app/...`) when
  matching Rails.

## Working a task

1. `plans/bin/plan ready` lists the claimable tasks, highest priority first, with their issue
   numbers. `plans/bin/plan show <ID>` prints the full card.
2. Claim the task by assigning its issue to yourself, or commenting that you've started.
   Don't take a task someone else has claimed.
3. Branch `port/<ID>-<slug>` from `main`. Edit only the paths listed under the task's `owns`. If you
   need a change in a path another task owns, comment on that task's issue instead of editing it.
4. Run `bin/check` until it's green. It runs format, build, tests and `plans/bin/plan validate`.
5. Add `plans/status/<ID>.md` with evidence for every acceptance criterion
   (template in `plans/status/README.md`).
6. Open a PR titled `[<ID>] <title>` whose body says `Closes #<issue>`, and turn on auto-merge
   (squash). A green PR merges itself. If CI goes red, fix it; if `main` moves and the PR
   conflicts, merge `main` into the branch.
7. If a task turns out bigger than its size, split it in `plans/tasks.yaml` (new ids, same lane),
   then run `plans/bin/plan dag` and `plans/bin/plan validate`.

Issues labelled `human` (B05 and X02) are for the repository owner. Don't start them.

## Setup

`git submodule update --init --recursive` checks out both references. Once task F03 lands,
`bin/setup` installs the pinned .NET SDK and `bin/check` runs every local check.
