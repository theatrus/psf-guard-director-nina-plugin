"""Loopback Open-Meteo fixture for the disposable native cloud-hold test."""
import http.server
import json
from pathlib import Path
import sys
import time
import urllib.parse

root = Path(sys.argv[1])
requests = 0
payload = None


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        global requests, payload
        if urllib.parse.urlsplit(self.path).path != "/v1/forecast":
            self.send_error(404)
            return
        requests += 1
        (root / "forecast-requests.txt").write_text(str(requests))
        if payload is None:
            # These intervals meet exactly. A refresh is unnecessary when the
            # initial cloudy interval ends 45 seconds after the first request.
            boundary = int(time.time()) + 45
            payload = json.dumps({"hourly": {
                "time": [boundary - 3600, boundary],
                "cloud_cover": [80, 10],
                "temperature_2m": [10, 10],
                "dew_point_2m": [4, 4],
                "relative_humidity_2m": [65, 65],
            }}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_):
        pass


server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
(root / "forecast-port.txt").write_text(str(server.server_port))
server.serve_forever()
