#!/usr/bin/env python3
"""Send one LSP request for a real .axaml file to the installed axaml-ls and print the (shortened) result.

usage: mise exec -- python3 scripts/lsp-request.py <file.axaml> <method> [line col]
  line and col are 1-based. Methods: documentSymbol, foldingRange, codeLens, typeDefinition, implementation, hover, definition.
  (documentSymbol is printed as an indented tree.)
Server: $AXAML_LS, else ~/.local/bin/axaml-ls.
"""
import json, os, subprocess, sys, threading, queue, pathlib, time
SERVER = os.environ.get("AXAML_LS", str(pathlib.Path.home() / ".local/bin/axaml-ls"))
path = pathlib.Path(sys.argv[1]).resolve(); method = sys.argv[2]
pos = {"line": int(sys.argv[3]) - 1, "character": int(sys.argv[4]) - 1} if len(sys.argv) > 4 else {"line": 0, "character": 0}
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
send({"method": "textDocument/didOpen", "params": {"textDocument": {"uri": uri, "languageId": "axaml", "version": 1, "text": path.read_text()}}})
wait(lambda m: m.get("method") == "$/progress" and m["params"]["value"]["kind"] == "end")
send({"id": 2, "method": "textDocument/" + method, "params": {"textDocument": {"uri": uri}, "position": pos}})
res = wait(lambda m: m.get("id") == 2)
if "error" in res: print("error:", res["error"])
elif method == "documentSymbol":
    def tree(nodes, d=0):
        for n in nodes:
            print("  " * d + f"{n['name']}  [{n['kind']}] {n.get('detail','')}  {n['range']['start']['line']+1}-{n['range']['end']['line']+1}")
            tree(n["children"], d + 1)
    tree(res["result"])
else:
    r = res["result"]
    if isinstance(r, list) and len(r) > 25: print(json.dumps(r[:25])[:3000], f"… ({len(r)} total)")
    else: print(json.dumps(r, indent=1)[:3000])
proc.kill()
