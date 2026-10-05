# Reference tools

Scripts that generate the golden vectors in `vectors/` by running the reference Rails app,
copied from the Rust port's `reference-tools/` (and its `crates/campfire/src/integrations/testdata/oracle/`
scripts, now in `oracle/`) at `basecamp/once-campfire-rust@ccece30`.

The generators for `vectors/` run against this repository's reference through
`parity/bin/reference` (the `campfire-reference` Docker image, or its native runtime):

    reference-tools/regenerate              # every family, into tmp/vectors/, checked against vectors/
    reference-tools/regenerate rails_compat routes

prints `same`, `DIFFERS` or `missing` for each file in `vectors/MANIFEST` and exits non-zero on a
difference. `campfire_sessions.json` needs the default seed (`parity/bin/seed build default`).
`views/`, `http_shape/`, `db/` and `rails_compat_verify_rust.rb` still assume the Rust repository's
layout (`crates/...`).

`import.sh` copies the vectors from `reference-rust/` and rewrites `vectors/MANIFEST`. With
`IMPORT_TOOLS=1` it also re-copies these scripts, which overwrites the adaptations above (`run.sh`,
`regenerate`, `campfire/user_agents.rb`, `oracle/`, `storage/run.sh`, `richtext/run.sh`).
