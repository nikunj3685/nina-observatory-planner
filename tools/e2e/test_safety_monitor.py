"""
Test-only ASCOM Alpaca safety monitor that a test script can switch between safe and unsafe.

NINA finds it by Alpaca discovery (UDP, port 32228 in the test profile so it does not clash with OmniSim on 32227)
and talks to it over HTTP like any Alpaca device.

Control (for the test script):
  GET http://127.0.0.1:32850/control/safe?value=1   -> safe
  GET http://127.0.0.1:32850/control/safe?value=0   -> unsafe
  GET http://127.0.0.1:32850/control/state          -> {"safe": ..., "connected": ..., "issafe_reads": n}
"""
import json
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

DISCOVERY_PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 32228
HTTP_PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 32850
LOG = sys.argv[3] if len(sys.argv) > 3 else None
UNIQUE_ID = "observatory-planner-test-safety-0001"

state = {"safe": False, "connected": False, "issafe_reads": 0, "server_tx": 0}
lock = threading.RLock()  # reentrant: handlers call reply() which also locks


def log(msg):
    line = f"{time.strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    if LOG:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")


def discovery():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("0.0.0.0", DISCOVERY_PORT))
    log(f"discovery listening on udp {DISCOVERY_PORT}")
    while True:
        data, addr = s.recvfrom(1024)
        if data.startswith(b"alpacadiscovery"):
            s.sendto(json.dumps({"AlpacaPort": HTTP_PORT}).encode(), addr)
            log(f"discovery reply to {addr[0]}:{addr[1]}")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, value=None, error=0, message="", raw=None, code=200):
        with lock:
            state["server_tx"] += 1
            tx = state["server_tx"]
        q = self.query()  # cached: a PUT body can only be read once
        body = raw if raw is not None else {
            "Value": value, "ClientTransactionID": int(q.get("clienttransactionid", ["0"])[0] or 0),
            "ServerTransactionID": tx, "ErrorNumber": error, "ErrorMessage": message}
        if value is None and raw is None:
            body.pop("Value")
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def query(self):
        if getattr(self, "_query", None) is not None:
            return self._query
        q = parse_qs(urlparse(self.path).query)
        if self.command == "PUT":
            length = int(self.headers.get("Content-Length", 0))
            q.update(parse_qs(self.rfile.read(length).decode()) if length else {})
        self._query = {k.lower(): v for k, v in q.items()}
        return self._query

    def do_GET(self):
        path = urlparse(self.path).path.lower().rstrip("/")
        q = self.query()
        if path == "/control/safe":
            safe = q.get("value", ["1"])[0] in ("1", "true", "True")
            with lock:
                state["safe"] = safe
            log(f"CONTROL safe={safe}")
            return self.reply(raw={"safe": safe})
        if path == "/control/state":
            with lock:
                snapshot = dict(state)
            return self.reply(raw=snapshot)
        if path == "/management/apiversions":
            return self.reply([1])
        if path == "/management/v1/description":
            return self.reply({"ServerName": "Planner test safety", "Manufacturer": "Observatory Planner tests", "ManufacturerVersion": "1.0", "Location": "local"})
        if path == "/management/v1/configureddevices":
            return self.reply([{"DeviceName": "Planner Test Safety", "DeviceType": "SafetyMonitor", "DeviceNumber": 0, "UniqueID": UNIQUE_ID}])
        prefix = "/api/v1/safetymonitor/0/"
        if not path.startswith(prefix):
            return self.reply(raw={"error": "not found"}, code=404)
        prop = path[len(prefix):]
        with lock:
            connected = state["connected"]
            safe = state["safe"]
        if prop == "connected":
            return self.reply(connected)
        if prop == "connecting":
            return self.reply(False)
        if prop == "issafe":
            with lock:
                state["issafe_reads"] += 1
            if not connected:
                return self.reply(False, 0x407, "Not connected")
            return self.reply(safe)
        if prop == "name":
            return self.reply("Planner Test Safety")
        if prop == "description":
            return self.reply("Test safety monitor for the Observatory Planner")
        if prop == "driverinfo":
            return self.reply("Observatory Planner test driver")
        if prop == "driverversion":
            return self.reply("1.0")
        if prop == "interfaceversion":
            return self.reply(1)
        if prop == "supportedactions":
            return self.reply([])
        return self.reply(None, 0x400, f"{prop} is not implemented")

    def do_PUT(self):
        path = urlparse(self.path).path.lower().rstrip("/")
        q = self.query()
        prefix = "/api/v1/safetymonitor/0/"
        prop = path[len(prefix):] if path.startswith(prefix) else ""
        if prop == "connected":
            value = q.get("connected", ["false"])[0].lower() == "true"
            with lock:
                state["connected"] = value
            log(f"NINA set connected={value}")
            return self.reply()
        if prop in ("connect", "disconnect"):
            with lock:
                state["connected"] = prop == "connect"
            log(f"NINA {prop}")
            return self.reply()
        return self.reply(None, 0x400, f"{prop} is not implemented")


if __name__ == "__main__":
    threading.Thread(target=discovery, daemon=True).start()
    log(f"http listening on {HTTP_PORT} (UniqueID {UNIQUE_ID})")
    ThreadingHTTPServer(("0.0.0.0", HTTP_PORT), Handler).serve_forever()
