#!/usr/bin/env python3
"""Drives axaml-ls over stdio like an editor would: open a file, wait for diagnostics, hover, complete, go to definition.
usage: lsp-smoke.py <server command...> -- <file.axaml>"""
import json, subprocess, sys, threading, queue, pathlib, time

argv = sys.argv[1:]
sep = argv.index("--")
cmd, path = argv[:sep], pathlib.Path(argv[sep + 1]).resolve()
proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
inbox: "queue.Queue[dict]" = queue.Queue()

def reader():
    while True:
        headers = {}
        while True:
            line = proc.stdout.readline()
            if not line:
                return
            line = line.decode().strip()
            if not line:
                break
            k, v = line.split(":", 1)
            headers[k.lower()] = v.strip()
        inbox.put(json.loads(proc.stdout.read(int(headers["content-length"]))))

threading.Thread(target=reader, daemon=True).start()

def send(msg):
    body = json.dumps({"jsonrpc": "2.0", **msg}).encode()
    proc.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode() + body)
    proc.stdin.flush()

def wait(pred, timeout=60):
    end = time.time() + timeout
    while time.time() < end:
        try:
            m = inbox.get(timeout=end - time.time())
        except queue.Empty:
            break
        if m.get("method") == "window/workDoneProgress/create":
            send({"id": m["id"], "result": None})
        if pred(m):
            return m
    raise SystemExit("timed out")

uri = path.as_uri()
text = path.read_text()
send({"id": 1, "method": "initialize", "params": {"processId": None, "rootUri": None, "capabilities": {"window": {"workDoneProgress": True}}}})
print("initialize:", wait(lambda m: m.get("id") == 1)["result"]["serverInfo"])
send({"method": "initialized", "params": {}})
send({"method": "textDocument/didOpen", "params": {"textDocument": {"uri": uri, "languageId": "axaml", "version": 1, "text": text}}})

# The first publish may come before the project is loaded; wait for the progress "end", then the next publish.
wait(lambda m: m.get("method") == "$/progress" and m["params"]["value"]["kind"] == "end", 120)
diag = wait(lambda m: m.get("method") == "textDocument/publishDiagnostics")
print(f"diagnostics ({len(diag['params']['diagnostics'])}):")
for d in diag["params"]["diagnostics"]:
    print(f"  {d['range']['start']['line'] + 1}:{d['range']['start']['character'] + 1} {d['code']} {d['message']}")

def pos_of(needle, delta=0):
    i = text.index(needle) + delta
    return {"line": text.count("\n", 0, i), "character": i - (text.rfind("\n", 0, i) + 1)}

needle = "{Binding Title}"
if needle in text:
    p = pos_of(needle, 10)
    send({"id": 2, "method": "textDocument/hover", "params": {"textDocument": {"uri": uri}, "position": p}})
    h = wait(lambda m: m.get("id") == 2)["result"]
    print("hover:", h and h["contents"]["value"].replace("\n", " | "))
    send({"id": 3, "method": "textDocument/definition", "params": {"textDocument": {"uri": uri}, "position": p}})
    print("definition:", [(d["uri"].rsplit("/", 1)[-1], d["range"]["start"]["line"] + 1) for d in wait(lambda m: m.get("id") == 3)["result"]])
    p = pos_of(needle, 9)
    send({"id": 4, "method": "textDocument/completion", "params": {"textDocument": {"uri": uri}, "position": p}})
    items = wait(lambda m: m.get("id") == 4)["result"]["items"]
    print(f"completion ({len(items)}):", sorted(i["label"] for i in items)[:12])

send({"id": 9, "method": "shutdown"})
wait(lambda m: m.get("id") == 9)
send({"method": "exit"})
proc.wait(timeout=10)
print("exit code:", proc.returncode)
