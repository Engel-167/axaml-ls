#!/usr/bin/env python3
"""Drive the installed axaml-ls over stdio against a real .axaml file, inserting probe snippets.

usage: mise exec -- python3 scripts/lsp-probe.py <file.axaml> '<anchor>@@<snippet with |>' ...
  Each probe inserts <snippet> right before the first occurrence of <anchor> in the file (in memory only),
  puts the cursor at '|', and prints the completion items (label, detail), sorted like the editor would.
  A snippet starting with FMT sends textDocument/onTypeFormatting for '/' instead of completion.
  Also prints the initial diagnostics, the semantic tokens, and resolves the first file item.
  Server: $AXAML_LS, else ~/.local/bin/axaml-ls (run `mise run install` first).
example:
  mise exec -- python3 scripts/lsp-probe.py ../Orion_POS/Orion_POS/Views/LoginView.axaml \
      '    </UserControl.Styles>@@<Style Selector="Button:|"/>'
"""
import json, os, subprocess, sys, threading, queue, pathlib, time
SERVER = os.environ.get("AXAML_LS", str(pathlib.Path.home() / ".local/bin/axaml-ls"))
path = pathlib.Path(sys.argv[1]).resolve()
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
def wait(pred, t=120):
    end = time.time() + t
    while time.time() < end:
        m = inbox.get(timeout=end - time.time())
        if m.get("method") == "window/workDoneProgress/create": send({"id": m["id"], "result": None})
        if pred(m): return m
    raise SystemExit("timeout")
uri = path.as_uri(); text = path.read_text()
probes = sys.argv[2:]  # each probe: snippet containing | ; inserted before </UserControl.Styles> or end of root
send({"id": 1, "method": "initialize", "params": {"capabilities": {"window": {"workDoneProgress": True}}}})
init = wait(lambda m: m.get("id") == 1)["result"]
print("legend:", init["capabilities"]["semanticTokensProvider"]["legend"]["tokenTypes"])
send({"method": "initialized", "params": {}})
send({"method": "textDocument/didOpen", "params": {"textDocument": {"uri": uri, "languageId": "axaml", "version": 1, "text": text}}})
wait(lambda m: m.get("method") == "$/progress" and m["params"]["value"]["kind"] == "end")
d = wait(lambda m: m.get("method") == "textDocument/publishDiagnostics")
for x in d["params"]["diagnostics"]:
    print(f"  diag {x['range']['start']['line']+1}:{x['range']['start']['character']+1} {x['code']} {x.get('tags')} {x['message']}")
send({"id": 2, "method": "textDocument/semanticTokens/full", "params": {"textDocument": {"uri": uri}}})
data = wait(lambda m: m.get("id") == 2)["result"]["data"]
legend = init["capabilities"]["semanticTokensProvider"]["legend"]["tokenTypes"]
lines = text.split("\n"); ln = ch = 0; out = []
for i in range(0, len(data), 5):
    dl, dc, n, t, _ = data[i:i+5]
    ln += dl; ch = ch + dc if dl == 0 else dc
    out.append(f"{lines[ln][ch:ch+n]}={legend[t]}")
print("tokens:", out[:30])
v = 2; rid = 10
for probe in probes:
    anchor, snippet = probe.split("@@")
    i = text.index(anchor)
    caret = snippet.index("|")
    new = text[:i] + snippet.replace("|", "") + text[i:]
    off = i + caret
    v += 1
    send({"method": "textDocument/didChange", "params": {"textDocument": {"uri": uri, "version": v}, "contentChanges": [{"text": new}]}})
    pos = {"line": new.count("\n", 0, off), "character": off - (new.rfind("\n", 0, off) + 1)}
    rid += 1
    if snippet.startswith("FMT"):
        send({"id": rid, "method": "textDocument/onTypeFormatting", "params": {"textDocument": {"uri": uri}, "position": pos, "ch": "/", "options": {"tabSize": 4, "insertSpaces": True}}})
        print(f"\n{snippet!r}: edits ->", wait(lambda m: m.get("id") == rid)["result"]); continue
    send({"id": rid, "method": "textDocument/completion", "params": {"textDocument": {"uri": uri}, "position": pos}})
    r = wait(lambda m: m.get("id") == rid)["result"]
    items = sorted(r["items"], key=lambda x: x.get("sortText") or x["label"])
    files = [x for x in items if x.get("data")]
    if files:
        rid += 1
        send({"id": rid, "method": "completionItem/resolve", "params": files[0]})
        doc = wait(lambda m: m.get("id") == rid)["result"].get("documentation", {}).get("value", "")
        print("  resolve", files[0]["label"], "->", doc[:60], "...", doc[-45:].replace("\n", " "), f"({len(doc)} chars)")
    print("  isIncomplete:", r["isIncomplete"], " first insert:", items[0]["textEdit"]["newText"] if items else None)
    print(f"\n{snippet!r}: {len(items)} ->", [(x["label"], x.get("detail")) for x in items][:12])
send({"id": 99, "method": "shutdown"}); wait(lambda m: m.get("id") == 99); send({"method": "exit"})
