"""The web manifest, service worker, QR codes, health check and link unfurling
(PwaController, QrCodeController, Rails::HealthController, UnfurlLinksController)."""

from . import family


@family("pwa")
def pwa(run):
    """Manifest, service worker, QR codes, /up, and unfurling a URL that has no metadata."""
    anon = run.pair("203.0.113.111")
    david = run.signed_in("david", "203.0.113.112")
    for path in ["/webmanifest.json", "/webmanifest", "/service-worker.js", "/service-worker", "/up",
                 "/qr_code/aHR0cDovL2NhbXBmaXJlLnRlc3Qvam9pbi9DUk11LWw4R2UtS0I5Qg", "/qr_code/aHR0cDovL2NhbXBmaXJlLnRlc3Q", "/qr_code/!!"]:
        run.compare(f"anon GET {path}", anon.get(path))
        run.compare(f"david GET {path} with Accept: */*", david.get(path, {"Accept": "*/*"}))
    run.compare("POST /unfurl_link (a private address)", david.xhr("/", "post", "/unfurl_link", [("url", "http://127.0.0.1/")]))
