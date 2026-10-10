# Frontend assets

The frontend ships byte for byte as the reference's `bin/rails assets:precompile` leaves it.
`bin/build-assets` rebuilds it from pinned sources with no Ruby or Node:

```sh
bin/build-assets                  # writes artifacts/assets
SOURCE_DATE_EPOCH=... bin/build-assets out/   # reproducible last-modified, other directory
```

The output directory holds:

| Path | What it is |
|---|---|
| `public/` | Everything ActionDispatch::Static serves: `reference/public` plus `public/assets/` with every Propshaft-digested file and `.manifest.json` |
| `importmap.json` | `Rails.application.importmap.to_json`, the JSON inside the import map script tag |
| `importmap_tags.html` | `javascript_importmap_tags`, as the application layout renders it |

The server loads it with `AssetBundle.Load` and serves it with `StaticFiles`
(`src/Campfire.Web/Assets/`). `AssetBundle.Build` does the same build in memory.

## Sources

Propshaft's load path, in its order:

1. `assets/overrides/`: port-owned changes, which shadow a file of the same logical path. Empty
   today; the C# port serves the reference frontend unchanged.
2. The reference app's own directories, read straight from the `reference/` submodule:
   `app/assets/{images,sounds,stylesheets}`, `app/javascript` and `vendor/javascript` (request.js
   and highlight.js live there).
3. `assets/vendor/`: the gem-provided directories (Turbo, Stimulus, Lexxy, Action Cable, Action
   Text, Trix, Active Storage), byte for byte at the versions `reference/Gemfile.lock` resolves.
   `vendor/LOAD_PATH` records the load path and `vendor/MANIFEST.md` each file's gem, version,
   source and SHA-256. (Rails main no longer puts Action View's rails-ujs on the load path.)

`assets/vendor/` and the golden outputs in `tests/Campfire.Web.Tests/Assets/Reference/` were
exported from the reference image at our pin, `edbc779`, by the Rust port's
`crates/assets/script/export_reference.rb` (unmodified). After a reference bump, rebuild the image
and export again into a scratch directory, then copy `vendor/` here and `tests/reference/*` into
`tests/Campfire.Web.Tests/Assets/Reference/`:

```sh
parity/bin/reference build
mkdir -p tmp/assets-export/script
cp reference-rust/crates/assets/script/export_reference.rb tmp/assets-export/script/
docker run --rm --user "$(id -u):$(id -g)" -e RAILS_ENV=production -e SECRET_KEY_BASE_DUMMY=1 \
  -e DISABLE_SSL=1 -e ASSETS_DIR=/work/assets -e REFERENCE_SHA="$(git -C reference rev-parse HEAD)" \
  -v "$PWD/tmp/assets-export:/work/assets" campfire-reference:app \
  bin/rails runner /work/assets/script/export_reference.rb
```

`VendorTests` checks every file against `MANIFEST.md` and every gem version against `Gemfile.lock`.

## What is ported

- Propshaft 1.3.2 (`PropshaftLoadPath`): load path dedup and shadowing, SHA1 digests over an asset
  and everything it references plus the assets version (`1.0`), the CSS and JS asset-URL compilers,
  the source-mapping-URL compiler, and `.manifest.json` with each entry's `app` flag (whether it
  comes from the Rails root's `app/assets`).
- importmap-rails 2.2.2 (`Importmap`): the `pin` / `pin_all_from` subset of `config/importmap.rb`
  the reference uses (anything else fails the build), `to_json` and `javascript_importmap_tags`.
- ActionDispatch::Static over Rack::Files 3.2.7 (`StaticFiles`): path cleaning, `.html` and
  `/index.html` fallbacks, `charset=utf-8` on HTML and CSS, precompressed `.br`/`.gz`
  negotiation, ranges and multipart ranges, `If-Modified-Since`, and Rack::Mime's full table.

Headers are the reference's: `config/environments/production.rb` assigns
`public_file_server.headers` twice, and the second assignment wins, so every static file, digested
assets included, gets `Cache-Control: public, max-age=2592000` rather than the immutable one-year
policy the first assignment describes.

## Golden tests

`tests/Campfire.Web.Tests/Assets/` checks the build against what the reference itself produced
(`tests/Campfire.Web.Tests/Assets/Reference/`, exported from `assets:precompile` and the real
helpers): the manifest, the SHA-256 of all 312 compiled files, the import map tags, the
`stylesheet_link_tag :all` order, and ActionDispatch::Static's responses.

One thing can't match byte for byte: `public/assets/.manifest.json` lists its entries in the order
Propshaft found them, which is the build machine's `readdir` order and differs between
filesystems even for Rails. We walk each directory in ordinal order, so the file has the same
entries and length as the reference's but its own order.
