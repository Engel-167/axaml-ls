#!/usr/bin/env python3
"""Ask the installed axaml-ls for references, or compute (never apply) a rename, at a position of a real .axaml file.

usage: mise exec -- python3 scripts/lsp-references.py <file.axaml> <line> <col> [rename <NewName>]
  line and col are 1-based, as an editor shows them.
  Without "rename": prints every reference as file:line:col and the text there.
  With "rename": prints prepareRename's placeholder and the WorkspaceEdit's changes (file, range, newText).
  Nothing is written to disk. Server: $AXAML_LS, else ~/.local/bin/axaml-ls.
"""
import json, os, subprocess, sys, threading, queue, pathlib, time
SERVER = os.environ.get("AXAML_LS", str(pathlib.Path.home() / ".local/bin/axaml-ls"))
path = pathlib.Path(sys.argv[1]).resolve()
line, col = int(sys.argv[2]) - 1, int(sys.argv[3]) - 1
rename = sys.argv[5] if len(sys.argv) > 5 and sys.argv[4] == "rename" else None
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
def request(i, method, params):
    send({"id": i, "method": method, "params": params})
    return wait(lambda m: m.get("id") == i)
uri = path.as_uri()
send({"id": 1, "method": "initialize", "params": {"capabilities": {"window": {"workDoneProgress": True}}}})
wait(lambda m: m.get("id") == 1)
send({"method": "initialized", "params": {}})
send({"method": "textDocument/didOpen", "params": {"textDocument": {"uri": uri, "languageId": "axaml", "version": 1, "text": path.read_text()}}})
wait(lambda m: m.get("method") == "$/progress" and m["params"]["value"]["kind"] == "end")
pos = {"textDocument": {"uri": uri}, "position": {"line": line, "character": col}}
def show(u, r):
    p = pathlib.Path(u[7:]) if u.startswith("file://") else pathlib.Path(u)
    from urllib.parse import unquote
    p = pathlib.Path(unquote(u[7:]))
    lines = p.read_text().split("\n")
    s, e = r["start"], r["end"]
    text = lines[s["line"]][s["character"]:e["character"]] if s["line"] == e["line"] else "…"
    return f"{p.name}:{s['line']+1}:{s['character']+1}  {text!r}"
if rename is None:
    res = request(2, "textDocument/references", {**pos, "context": {"includeDeclaration": True}})
    for loc in res.get("result") or []: print(show(loc["uri"], loc["range"]))
    print(len(res.get("result") or []), "references")
else:
    prep = request(2, "textDocument/prepareRename", pos).get("result")
    print("prepareRename:", prep)
    res = request(3, "textDocument/rename", {**pos, "newName": rename})
    if "error" in res: print("error:", res["error"])
    else:
        n = 0
        for u, edits in (res["result"] or {}).get("changes", {}).items():
            for e in edits:
                print(show(u, e["range"]), "->", repr(e["newText"])); n += 1
        print(n, "edits in", len((res["result"] or {}).get("changes", {})), "files")
proc.kill()
