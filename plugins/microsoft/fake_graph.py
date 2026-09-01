"""A stand-in for Microsoft Graph and the identity platform, on loopback.

The same principle as the Discord stand-in: the fake is at the far end of a real socket, so what is
under test is the real client building real requests and reading real responses. What is replaced
is whose server answers them.

It is deliberately capable of behaving badly — throttling, malformed bodies, truncated JSON,
redirects to somewhere else, an error envelope that is not the documented shape. A provider fake
that only ever behaves well tests the happy path and nothing that matters.

It records what it was asked, because "the plugin refused" and "the plugin asked and was told no"
are the two outcomes a boundary must keep apart, and they look identical from the caller's side.
"""

import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer


class FakeGraph:
    """An HTTP server on 127.0.0.1 that answers like Graph, or like Graph on a bad day."""

    def __init__(self):
        self.replies = {}
        self.default = None
        self.seen = []
        self._lock = threading.Lock()

        service = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def _handle(self, method):
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length).decode("utf-8") if length else ""

                with service._lock:
                    service.seen.append({
                        "method": method,
                        "path": self.path,
                        "headers": {k.lower(): v for k, v in self.headers.items()},
                        "body": body,
                    })

                    key = (method, self.path.split("?")[0])
                    queue = service.replies.get(key)

                    if queue:
                        reply = queue.pop(0)
                    elif service.default is not None:
                        reply = service.default
                    else:
                        reply = (404, json.dumps({
                            "error": {"code": "itemNotFound", "message": "no such thing"}}), {})

                status, text, headers = reply

                encoded = text.encode("utf-8") if isinstance(text, str) else text

                self.send_response(status)

                for name, value in headers.items():
                    self.send_header(name, value)

                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(encoded)))
                self.end_headers()
                self.wfile.write(encoded)

            def do_GET(self):
                self._handle("GET")

            def do_POST(self):
                self._handle("POST")

            def do_PATCH(self):
                self._handle("PATCH")

            def do_DELETE(self):
                self._handle("DELETE")

        self._server = HTTPServer(("127.0.0.1", 0), Handler)
        self.port = self._server.server_address[1]
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()

    @property
    def base(self):
        return "http://127.0.0.1:%d" % self.port

    def answer(self, method, path, status=200, body=None, headers=None):
        """Queues one reply for one route. Replies are given in the order they were queued."""
        text = body if isinstance(body, str) else json.dumps(body if body is not None else {})

        with self._lock:
            self.replies.setdefault((method, path), []).append((status, text, headers or {}))

        return self

    def always(self, status=200, body=None, headers=None):
        text = body if isinstance(body, str) else json.dumps(body if body is not None else {})
        self.default = (status, text, headers or {})
        return self

    def requests_to(self, path):
        with self._lock:
            return [r for r in self.seen if r["path"].split("?")[0] == path]

    def close(self):
        self._server.shutdown()
        self._server.server_close()

    def __enter__(self):
        return self

    def __exit__(self, *unused):
        self.close()
