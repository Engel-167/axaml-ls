#!/usr/bin/env python3
"""Introduce errors in memory in a real .axaml file and print the quick fixes axaml-ls offers for each diagnostic.

usage: mise exec -- python3 scripts/lsp-codeactions.py <file.axaml> 'old=>new' ['old=>new' ...]
  Each 'old=>new' replaces the first occurrence of old in the text (in memory only; nothing is written).
  Prints every diagnostic the edited text gets, and below it the code actions (title and edits).
example:
  mise exec -- python3 scripts/lsp-codeactions.py ../Orion_POS/Orion_POS/Views/LoginView.axaml \
      'StaticResource SpaceGrotesk=>StaticResource SpaceGrotsk' '<TextBox=><TextBoks'
Server: $AXAML_LS, else ~/.local/bin/axaml-ls.
"""
import json, os, subprocess, sys, threading, queue, pathlib, time
from urllib.parse import unquote
SERVER = os.environ.get("AXAML_LS", str(pathlib.Path.home() / ".local/bin/axaml-ls"))
path = pathlib.Path(sys.argv[1]).resolve()
text = path.read_text()
for r in sys.argv[2:]:
    old, new = r.split("=>", 1)
    if old not in text: raise SystemExit(f"not found: {old!r}")
    text = text.replace(old, new, 1)
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
def wait(pred, t=180):
    end = time.time() + t
    while time.time() < end:
        m = inbox.get(timeout=end - time.time())
        if m.get("method") == "window/workDoneProgress/create": send({"id": m["id"], "result": None})
        if pred(m): return m
    raise SystemExit("timeout")
uri = path.as_uri()
send({"id": 1, "method": "initialize", "params": {"capabilities": {"window": {"workDoneProgress": True}}}})
wait(lambda m: m.get("id") == 1)
send({"method": "initialized", "params": {}})
send({"method": "textDocument/didOpen", "params": {"textDocument": {"uri": uri, "languageId": "axaml", "version": 1, "text": text}}})
wait(lambda m: m.get("method") == "$/progress" and m["params"]["value"]["kind"] == "end")
diags = wait(lambda m: m.get("method") == "textDocument/publishDiagnostics")["params"]["diagnostics"]
def show(u, r):
    p = pathlib.Path(unquote(u[7:]))
    src = (text if p == path else p.read_text()).split("\n")
    s, e = r["start"], r["end"]
    seg = src[s["line"]][s["character"]:e["character"]] if s["line"] == e["line"] else "…"
    return f"{p.name}:{s['line']+1}:{s['character']+1} {seg!r}"
for i, d in enumerate(diags):
    print(f"{d['code']} {d['range']['start']['line']+1}:{d['range']['start']['character']+1} {d['message']}")
    res = None
    send({"id": 10 + i, "method": "textDocument/codeAction", "params": {"textDocument": {"uri": uri}, "range": d["range"], "context": {"diagnostics": [d]}}})
    for a in wait(lambda m: m.get("id") == 10 + i).get("result") or []:
        print("   ->", a["title"], "(preferred)" if a.get("isPreferred") else "")
        for u, edits in a["edit"]["changes"].items():
            for e in edits: print("        ", show(u, e["range"]), "<-", repr(e["newText"]))
proc.kill()
