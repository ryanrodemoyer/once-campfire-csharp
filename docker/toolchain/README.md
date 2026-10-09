# The pinned media toolchain

The image in this directory pins the media tools the port's variants and video previews must be
byte-identical for: **libvips 8.16.1** (`libvips42t64` 8.16.1-1+deb13u1) and **ffmpeg 7.1.5**
(`ffmpeg` 7:7.1.5-0+deb13u1), the exact Debian trixie packages the reference image installs
(`apt-get install libvips ffmpeg` on `ruby:3.4-slim`, which is trixie; `libvips` is a virtual
package that resolves to `libvips42t64`). Being the same `.deb`s as the reference image's is the
strongest form of parity: whatever it produces, we produce byte for byte, on both amd64 and
arm64.

The Go and Rust ports instead build libvips and ffmpeg from the Debian source packages, trimmed
to what Campfire reaches, pinned by `.dsc` checksums. The binary packages here are the same bytes
the reference image runs, so nothing is trimmed and no build is needed; the exact-version install
plays the `.dsc` checksum's role: apt's signed indexes verify the `.deb`s, and Debian trixie
moving on to a newer version fails this build rather than silently drifting.

## Running the media golden tests inside the image

```sh
docker build -t campfire-csharp-toolchain docker/toolchain   # optional; media-tests builds it
docker/toolchain/media-tests amd64
docker/toolchain/media-tests arm64                            # under emulation on an amd64 host
docker/toolchain/media-tests both
```

`media-tests` copies the repo into the container (the host's `bin/` and `obj/` are never
touched) and runs the Storage tests filtered to `Media`: the golden set
(`MediaGoldenTests`: every variant, the preview image and every analysis), `MarcelTests` and
`MediaToolTests`. The image sets `CAMPFIRE_REQUIRE_MEDIA_VECTORS=1`, so the byte comparisons run
(they compare only when the local libvips and ffmpeg are the versions that made the vectors,
`vectors/storage.json` `versions`) and any other version **fails** instead of skipping.

`arm64` needs QEMU binfmt on an amd64 host first:

```sh
docker run --privileged --rm tonistiigi/binfmt --install arm64
```

## Architecture and video bytes

ffmpeg's video encoding output is not byte-identical across architectures (amd64 vs arm64) even
with the same version, input and flags — different SIMD code paths and floating-point
implementations produce different bytes. The image variants (libvips output) are byte-identical
on both architectures. This is a fundamental trade-off of using binary packages: you get the
exact same `.deb`s as the reference image, but video preview bytes differ between architectures.
If cross-architecture video byte identity matters, build from source with pinned compiler flags
as the Go and Rust ports do.

The `media-tests` script skips only the video preview byte comparison on arm64, while still
running every other test (including ffmpeg/ffprobe usage through media analysis and error
handling) to verify the toolchain is correctly installed on both architectures.

## When Debian trixie moves on

If `apt-get install libvips42t64=8.16.1-1+deb13u1 ffmpeg=7:7.1.5-0+deb13u1` fails to find those
versions, trixie has superseded them. That also means a rebuilt reference image no longer
produces the vectors' bytes, so regenerate the vectors (the reference app,
`reference-tools/storage/generate.rb`) and bump the versions here — or pin the older archive via
`snapshot.debian.org`. The tests inside the image are the gate either way: they fail on any other
toolchain version while `CAMPFIRE_REQUIRE_MEDIA_VECTORS` is set.

## The production image

The root `Dockerfile`'s runtime stage (P01's) installs the same two pinned packages; that is all
the wiring it takes. The `media` stage here is that runtime set on its own, for reference:

```dockerfile
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y \
      libvips42t64=8.16.1-1+deb13u1 ffmpeg=7:7.1.5-0+deb13u1 && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives
```

NetVips loads `libvips.so.42` from the system (`src/Campfire.Storage/Media/LibVips.cs`), so
there is no `NetVips.Native`; ffmpeg and ffprobe are found on `PATH`.
