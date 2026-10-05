# Reference tools

Scripts that generate the golden vectors in `vectors/` by running the reference Rails app,
copied from the Rust port's `reference-tools/` (and its `crates/campfire/src/integrations/testdata/oracle/`
scripts, now in `oracle/`) at `basecamp/once-campfire-rust@ccece30`.

They still assume the Rust repository's layout (`parity/.env.reference`, `crates/...` output
paths); task V01 adapts them to run against this repository's reference and reproduce `vectors/`.
Until then, regenerate in `reference-rust/` and re-import.

`import.sh` copies the vectors from `reference-rust/` and rewrites `vectors/MANIFEST`. With
`IMPORT_TOOLS=1` it also re-copies these scripts, which overwrites local adaptations.
