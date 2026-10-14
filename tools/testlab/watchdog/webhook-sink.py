#!/usr/bin/env python3
"""Webhook and Teams Workflows sink for the AdMng Watchdog lab (Python 3 standard library only).

Listens on 127.0.0.1:${SINK_PORT:-8091} and accepts the POSTs of the watchdog's webhook and Teams channels:

* Webhook mode (any path not starting with /teams): verifies the Standard Webhooks v1 signature
  (``webhook-id``, ``webhook-timestamp``, ``webhook-signature``) with the secret from ``SINK_SECRET``
  (``whsec_<base64>``; the public lab value of common.sh when unset), rejects timestamps more than 5 minutes
  off and prints type / id / object of every delivery (``duplicate`` for a retried webhook-id).
* Teams mode (paths starting with /teams, or every path with ``SINK_MODE=teams``): checks the Power Automate
  Workflows envelope (``type: message``, exactly one Adaptive Card attachment) and prints the card title.

Responses are scriptable: ``SINK_STATUS`` (default 202, e.g. 429 or 500) and ``SINK_RETRY_AFTER`` (seconds) at start,
or at runtime with ``PUT /_sink/response`` and a JSON body ``{"status": 429, "retryAfter": 5}``.
``GET /_sink/requests`` returns the summaries of everything received (for scripts and tests),
``DELETE /_sink/requests`` clears them.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOLERANCE_SECONDS = 5 * 60
MAX_BODY_BYTES = 1024 * 1024
DEFAULT_SECRET = "whsec_QWRNbmctV2F0Y2hkb2ctbGFiLXdlYmhvb2stMjAyNiE="


def parse_secret(secret: str) -> bytes:
    """Key bytes of a whsec_ secret (base64 after the prefix, padding optional)."""
    if not secret.startswith("whsec_"):
        raise ValueError("SINK_SECRET must start with whsec_")
    encoded = secret[len("whsec_"):]
    encoded += "=" * (-len(encoded) % 4)
    key = base64.b64decode(encoded, validate=True)
    if not 24 <= len(key) <= 64:
        raise ValueError("the SINK_SECRET key must be 24-64 bytes")
    return key


def verify_signature(key: bytes, message_id: str, timestamp: str, body: bytes, header: str) -> bool:
    """True when any ``v1,<base64>`` entry of the header is the HMAC-SHA256 of id.timestamp.body."""
    signed = message_id.encode("utf-8") + b"." + timestamp.encode("ascii") + b"." + body
    expected = base64.b64encode(hmac.new(key, signed, hashlib.sha256).digest()).decode("ascii")
    valid = False
    for entry in header.split(" "):
        version, _, signature = entry.partition(",")
        if version == "v1" and hmac.compare_digest(signature, expected):
            valid = True
    return valid


class SinkState:
    """Received requests and the scripted response, shared by the handler threads."""

    def __init__(self, key: bytes, status: int, retry_after: str | None, mode: str) -> None:
        self.key = key
        self.status = status
        self.retry_after = retry_after
        self.mode = mode
        self.lock = threading.Lock()
        self.requests: list[dict] = []
        self.seen_ids: set[str] = set()
        self.output = threading.Lock()

    def say(self, line: str) -> None:
        """One whole line on stdout (handler threads must not interleave)."""
        with self.output:
            print(line, flush=True)

    def record(self, summary: dict) -> None:
        with self.lock:
            self.requests.append(summary)

    def first_delivery(self, message_id: str) -> bool:
        with self.lock:
            if message_id in self.seen_ids:
                return False
            self.seen_ids.add(message_id)
            return True


def card_title(card: dict) -> str:
    """The first TextBlock of the card (the alert or digest title)."""
    stack = list(card.get("body", []))
    while stack:
        element = stack.pop(0)
        if not isinstance(element, dict):
            continue
        if element.get("type") == "TextBlock" and element.get("text"):
            return str(element["text"])
        stack[0:0] = element.get("items", []) + [c for col in element.get("columns", []) for c in col.get("items", [])]
    return card.get("fallbackText", "")


class SinkHandler(BaseHTTPRequestHandler):
    server_version = "AdMngWatchdogLabSink/1"
    state: SinkState

    def log_message(self, format: str, *args) -> None:  # noqa: A002 - BaseHTTPRequestHandler signature
        pass  # one line per delivery is printed by the handlers instead of the access log

    # --- control endpoints ---------------------------------------------------------------------------------------
    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/_sink/requests":
            with self.state.lock:
                payload = json.dumps(self.state.requests, ensure_ascii=False).encode("utf-8")
            self.reply(200, payload, "application/json; charset=utf-8")
        elif self.path == "/_sink/health":
            self.reply(200, b"ok", "text/plain")
        else:
            self.reply(404, b"not found", "text/plain")

    def do_DELETE(self) -> None:  # noqa: N802
        if self.path == "/_sink/requests":
            with self.state.lock:
                self.state.requests.clear()
                self.state.seen_ids.clear()
            self.reply(204)
        else:
            self.reply(404, b"not found", "text/plain")

    def do_PUT(self) -> None:  # noqa: N802
        if self.path != "/_sink/response":
            self.reply(404, b"not found", "text/plain")
            return
        try:
            script = json.loads(self.read_body() or b"{}")
            self.state.status = int(script.get("status", 202))
            retry_after = script.get("retryAfter")
            self.state.retry_after = None if retry_after is None else str(retry_after)
        except (ValueError, TypeError):
            self.reply(400, b"expected {\"status\": 202, \"retryAfter\": null}", "text/plain")
            return
        self.state.say(f"[sink] responses now {self.state.status} (Retry-After {self.state.retry_after})")
        self.reply(204)

    # --- deliveries ----------------------------------------------------------------------------------------------
    def do_POST(self) -> None:  # noqa: N802
        body = self.read_body()
        if body is None:
            self.reply(413, b"payload too large", "text/plain")
            return
        teams = self.state.mode == "teams" or (self.state.mode == "auto" and self.path.startswith("/teams"))
        summary = self.teams_summary(body) if teams else self.webhook_summary(body)
        self.state.record(summary)
        self.state.say("[sink] " + json.dumps(summary, ensure_ascii=False))
        if summary.get("rejected"):
            self.reply(400 if teams else 401, summary["rejected"].encode("utf-8"), "text/plain")
            return
        headers = {"Retry-After": self.state.retry_after} if self.state.retry_after else {}
        self.reply(self.state.status, b"", None, headers)

    def webhook_summary(self, body: bytes) -> dict:
        message_id = self.headers.get("webhook-id", "")
        timestamp = self.headers.get("webhook-timestamp", "")
        signature = self.headers.get("webhook-signature", "")
        summary: dict = {"channel": "webhook", "id": message_id, "path": self.path.split("?", 1)[0]}
        try:
            payload = json.loads(body)
            data = payload.get("data") or {}
            obj = data.get("object") or {}
            summary.update(type=payload.get("type"), kind=data.get("kind"), severity=data.get("severity"),
                           object=obj.get("dn") or obj.get("kind"))
        except ValueError:
            summary["rejected"] = "body is not JSON"
            return summary
        if not message_id or not timestamp or not signature:
            summary["rejected"] = "missing webhook-id / webhook-timestamp / webhook-signature"
        elif not timestamp.isdigit() or abs(time.time() - int(timestamp)) > TOLERANCE_SECONDS:
            summary["rejected"] = "timestamp outside the 5 minute tolerance"
        elif not verify_signature(self.state.key, message_id, timestamp, body, signature):
            summary["rejected"] = "invalid signature"
        summary["signatureValid"] = "rejected" not in summary
        summary["duplicate"] = not self.state.first_delivery(message_id) if message_id else False
        return summary

    def teams_summary(self, body: bytes) -> dict:
        summary: dict = {"channel": "teams", "path": self.path.split("?", 1)[0]}
        try:
            envelope = json.loads(body)
        except ValueError:
            summary["rejected"] = "body is not JSON"
            return summary
        attachments = envelope.get("attachments") if isinstance(envelope, dict) else None
        if envelope.get("type") != "message" or not isinstance(attachments, list) or len(attachments) != 1:
            summary["rejected"] = "expected {\"type\":\"message\",\"attachments\":[one card]}"
            return summary
        attachment = attachments[0]
        card = attachment.get("content") or {}
        if attachment.get("contentType") != "application/vnd.microsoft.card.adaptive" or card.get("type") != "AdaptiveCard":
            summary["rejected"] = "the attachment is not an Adaptive Card"
            return summary
        summary.update(title=card_title(card), version=card.get("version"), bytes=len(body))
        return summary

    # --- helpers -------------------------------------------------------------------------------------------------
    def read_body(self) -> bytes | None:
        length = int(self.headers.get("Content-Length") or 0)
        if length > MAX_BODY_BYTES:
            return None
        return self.rfile.read(length) if length else b""

    def reply(self, status: int, body: bytes = b"", content_type: str | None = None, headers: dict | None = None) -> None:
        self.send_response(status)
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        if content_type:
            self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)


def main() -> int:
    port = int(os.environ.get("SINK_PORT", "8091"))
    mode = os.environ.get("SINK_MODE", "auto").lower()
    if mode not in ("auto", "webhook", "teams"):
        print("SINK_MODE must be auto, webhook or teams", file=sys.stderr)
        return 2
    try:
        key = parse_secret(os.environ.get("SINK_SECRET") or DEFAULT_SECRET)
    except ValueError as error:
        print(f"[sink] {error}", file=sys.stderr)
        return 2
    SinkHandler.state = SinkState(key, int(os.environ.get("SINK_STATUS", "202")), os.environ.get("SINK_RETRY_AFTER") or None, mode)
    server = ThreadingHTTPServer(("127.0.0.1", port), SinkHandler)
    print(f"[sink] listening on http://127.0.0.1:{port} (mode {mode}, responses {SinkHandler.state.status})", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
