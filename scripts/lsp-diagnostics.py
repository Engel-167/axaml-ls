#!/usr/bin/env python3
"""Open every .axaml file under a folder in one axaml-ls session and print the diagnostics it publishes.

usage: mise exec -- python3 scripts/lsp-diagnostics.py <project folder>
  Prints AXN0401/AXN0501/AXN0205/AXN0402-0406 lines in full and a count per code. Set SHOW=AXN0101,AXN0201 to print other codes too.
  Use it to look for false positives on a real project after changing a check.
"""
import json, os, subprocess, sys, threading, queue, pathlib, time
SERVER = os.environ.get("AXAML_LS", str(pathlib.Path.home() / ".local/bin/axaml-ls"))
files = [p for p in pathlib.Path(sys.argv[1]).resolve().rglob("*.axaml") if "/bin/" not in str(p) and "/obj/" not in str(p)]
proc = subprocess.Popen([SERVER], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
inbox = queue.Queue()
def reader():
    while True:
        h = {}
        while True:
            l = proc.stdout.readline()
            if not l: return
            l = l.decode().strip()
            if not l: break
            k, v = l.split(":", 1); h[k.lower()] = v.strip()
        inbox.put(json.loads(proc.stdout.read(int(h["content-length"]))))
threading.Thread(target=reader, daemon=True).start()
def send(m):
    b = json.dumps({"jsonrpc": "2.0", **m}).encode()
    proc.stdin.write(f"Content-Length: {len(b)}\r\n\r\n".encode() + b); proc.stdin.flush()
send({"id": 1, "method": "initialize", "params": {"capabilities": {"window": {"workDoneProgress": True}}}})
send({"method": "initialized", "params": {}})
latest = {}
for f in files:
    send({"method": "textDocument/didOpen", "params": {"textDocument": {"uri": f.as_uri(), "languageId": "axaml", "version": 1, "text": f.read_text()}}})
end = time.time() + 90; loaded = False
while time.time() < end:
    try: m = inbox.get(timeout=5)
    except queue.Empty:
        if loaded: break
        continue
    if m.get("method") == "window/workDoneProgress/create": send({"id": m["id"], "result": None})
    if m.get("method") == "$/progress" and m["params"]["value"]["kind"] == "end": loaded = True
    if m.get("method") == "textDocument/publishDiagnostics": latest[m["params"]["uri"]] = m["params"]["diagnostics"]
from collections import Counter
SHOW = {"AXN0501", "AXN0401", "AXN0205", "AXN0402", "AXN0403", "AXN0404", "AXN0405", "AXN0406"} | set(filter(None, os.environ.get("SHOW", "").split(",")))
codes = Counter()
for uri, ds in latest.items():
    for d in ds:
        codes[d["code"]] += 1
        if d["code"] in SHOW: print(uri.rsplit("/", 1)[-1], d["range"]["start"]["line"] + 1, d["code"], d["message"])
print(len(latest), "files;", dict(codes))
proc.kill()
