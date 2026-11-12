#!/usr/bin/env python3
"""Print the inlay hints axaml-ls returns for a file, inlined as ‹label› (first 14 lines that have hints).

usage: mise exec -- python3 scripts/lsp-hints.py <file.axaml>
"""
import json, os, subprocess, sys, threading, queue, pathlib, time
SERVER = os.environ.get("AXAML_LS", str(pathlib.Path.home() / ".local/bin/axaml-ls"))
path = pathlib.Path(sys.argv[1]).resolve(); text = path.read_text()
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
    b = json.dumps({"jsonrpc": "2.0", **m}).encode(); proc.stdin.write(f"Content-Length: {len(b)}\r\n\r\n".encode() + b); proc.stdin.flush()
def wait(pred, t=120):
    end = time.time() + t
    while time.time() < end:
        m = inbox.get(timeout=end - time.time())
        if m.get("method") == "window/workDoneProgress/create": send({"id": m["id"], "result": None})
        if pred(m): return m
send({"id": 1, "method": "initialize", "params": {"capabilities": {"window": {"workDoneProgress": True}}}}); wait(lambda m: m.get("id") == 1)
send({"method": "initialized", "params": {}})
send({"method": "textDocument/didOpen", "params": {"textDocument": {"uri": path.as_uri(), "languageId": "axaml", "version": 1, "text": text}}})
wait(lambda m: m.get("method") == "$/progress" and m["params"]["value"]["kind"] == "end")
lines = text.split("\n")
send({"id": 2, "method": "textDocument/inlayHint", "params": {"textDocument": {"uri": path.as_uri()}, "range": {"start": {"line": 0, "character": 0}, "end": {"line": len(lines), "character": 0}}}})
hints = wait(lambda m: m.get("id") == 2)["result"]
by_line = {}
for h in hints: by_line.setdefault(h["position"]["line"], []).append(h)
for ln in sorted(by_line)[:14]:
    s = lines[ln]
    for h in sorted(by_line[ln], key=lambda h: -h["position"]["character"]):
        c = h["position"]["character"]; s = s[:c] + "‹" + h["label"] + "›" + s[c:]
    print(f"{ln+1:4}: {s.strip()[:150]}")
print(len(hints), "hints")
proc.kill()
