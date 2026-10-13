#!/usr/bin/env python3
"""Resign seed transfer links so they are valid at the frozen parity clock.

The default seed signs transfers.david and transfers.kevin at 2026-03-02, so they
expire at 2026-03-02T20:00:00Z. Screen capture freezes both apps after 2026-10-07
(the pin's migrations refuse to boot at the seed instant). labels.json is produced
by the seed build and is gitignored, so compare refreshes the non-expired transfer
labels before capture. Expired fixtures (transfers.david_expired) are left alone.

The signature matches ActiveRecord::SignedId: PBKDF2-HMAC-SHA256 of
SECRET_KEY_BASE with salt "active_record/signed_id", 1000 iterations, 64 bytes,
then HMAC-SHA256 over the unpadded URL-safe JSON envelope.
"""

import base64
import hashlib
import hmac
import json
import sys
from datetime import datetime, timedelta, timezone


def load_secret(env_path):
    with open(env_path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line.startswith("SECRET_KEY_BASE="):
                return line.split("=", 1)[1].encode()
    raise SystemExit(f"no SECRET_KEY_BASE in {env_path}")


def sign(secret, user_id, expires_at):
    key = hashlib.pbkdf2_hmac("sha256", secret, b"active_record/signed_id", 1000, 64)
    payload = (
        '{"_rails":{"data":%d,"exp":"%s","pur":"user/transfer"}}' % (user_id, expires_at)
    ).encode()
    encoded = base64.urlsafe_b64encode(payload).rstrip(b"=")
    mac = hmac.new(key, encoded, hashlib.sha256).hexdigest().encode()
    return (encoded + b"--" + mac).decode()


def main():
    labels_path, clock, env_path = sys.argv[1:]
    moment = datetime.strptime(clock.replace("Z", ""), "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc)
    expires = (moment + timedelta(hours=4)).strftime("%Y-%m-%dT%H:%M:%S.000Z")
    secret = load_secret(env_path)
    with open(labels_path, encoding="utf-8") as handle:
        labels = json.load(handle)
    changed = []
    for key, user_key in (
        (key, "users." + key.split(".", 1)[1])
        for key in list(labels)
        if key.startswith("transfers.") and "expired" not in key
    ):
        if user_key not in labels:
            continue
        labels[key] = sign(secret, int(labels[user_key]), expires)
        changed.append(key)
    with open(labels_path, "w", encoding="utf-8") as handle:
        json.dump(labels, handle, indent=2)
        handle.write("\n")
    print(f"refresh-transfer-labels: {', '.join(changed) or 'nothing'} exp {expires}", file=sys.stderr)


if __name__ == "__main__":
    main()
