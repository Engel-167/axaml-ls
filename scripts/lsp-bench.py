#!/usr/bin/env python3
"""Times axaml-ls over LSP stdio on a big project (docs/plans/PERFORMANCE.md, P0.1).

usage: lsp-bench.py <server dll> <project dir> [--base OLD.dll] [--scenario NAME]... [--out FILE] [--compare FILE]

<project dir> is the SampleApp folder made by scripts/make-big-project.py. Scenarios (all by default):
  typing          40 incremental edits, textDocument/codeAction after each (the full diagnostics pass)
  complete-asset  types Source="/ico_5_4" one character at a time, completion after each (4 times)
  references      references on the x:Key "Accent", used in every generated view
  rename          prepareRename + rename of the same key (edits are computed, never applied)
  resolve         completionItem/resolve of the largest image's preview
  cancel          a references request cancelled after 5 ms: time until the answer, and whether it was cancelled

Each scenario runs once to warm up (JIT, first-use caches), then ROUNDS times; the round with the lowest median is
reported (best of N: background load only ever adds time). One JSON line per scenario: median/p95/max milliseconds,
median response bytes, diagnostics publishes.

Regression gate, preferred: --base OLD.dll starts the old server next to the new one and alternates rounds between
them, so both see the same machine load (CPU frequency scaling, a browser, a build). Exits 1 when the new server is
slower than the old one by more than 15% on a median (30% on a p95) and by more than the noise floor (2 ms median,
5 ms p95), and still is when the flagged scenarios are measured again.

--compare FILE applies the same rule against numbers stored by --out (docs/plans/bench-baseline.json). Those were
measured at another time, so on a busy desktop this gate is noisy; use it on a quiet machine, or use --base."""
import argparse, json, pathlib, queue, statistics, subprocess, sys, threading, time

# A regression is slower by more than the threshold AND by more than the noise floor (sub-millisecond timings jitter
# by 100%). The p95 threshold is looser: GC pauses land in the tail and move it 15-25% between runs of the same build.
REGRESSION = {"median_ms": 0.15, "p95_ms": 0.30}
NOISE_MS = {"median_ms": 2.0, "p95_ms": 5.0}
ROUNDS = 3


class Server:
    def __init__(self, dll: str):
        self.proc = subprocess.Popen(["dotnet", dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        self.inbox: "queue.Queue[tuple[float, int, dict]]" = queue.Queue()
        self.publishes = 0
        self.next_id = 1
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        out = self.proc.stdout
        while True:
            headers = {}
            while True:
                line = out.readline()
                if not line:
                    return
                line = line.decode().strip()
                if not line:
                    break
                k, v = line.split(":", 1)
                headers[k.lower()] = v.strip()
            body = out.read(int(headers["content-length"]))
            arrived = time.perf_counter()
            msg = json.loads(body)
            if msg.get("method") == "textDocument/publishDiagnostics":
                self.publishes += 1
            self.inbox.put((arrived, len(body), msg))

    def send(self, msg: dict):
        body = json.dumps({"jsonrpc": "2.0", **msg}).encode()
        self.proc.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode() + body)
        self.proc.stdin.flush()

    def wait(self, pred, timeout=300.0):
        end = time.time() + timeout
        while time.time() < end:
            try:
                arrived, size, msg = self.inbox.get(timeout=max(0.01, end - time.time()))
            except queue.Empty:
                break
            if msg.get("method") == "window/workDoneProgress/create":
                self.send({"id": msg["id"], "result": None})
            if pred(msg):
                return arrived, size, msg
        raise SystemExit("timed out waiting for the server")

    def start(self, method: str, params: dict) -> tuple[int, float]:
        rid = self.next_id
        self.next_id += 1
        sent = time.perf_counter()
        self.send({"id": rid, "method": method, "params": params})
        return rid, sent

    def request(self, method: str, params: dict) -> tuple[float, int, dict]:
        """(milliseconds, response bytes, message)."""
        rid, sent = self.start(method, params)
        arrived, size, msg = self.wait(lambda m: m.get("id") == rid)
        return (arrived - sent) * 1000, size, msg

    def notify(self, method: str, params: dict):
        self.send({"method": method, "params": params})

    def settle(self, seconds=1.0):
        """Lets debounced work (diagnostics) finish, answering server requests meanwhile."""
        end = time.time() + seconds
        while time.time() < end:
            try:
                _, _, msg = self.inbox.get(timeout=max(0.01, end - time.time()))
            except queue.Empty:
                break
            if msg.get("method") == "window/workDoneProgress/create":
                self.send({"id": msg["id"], "result": None})

    def stop(self):
        try:
            self.request("shutdown", {})
            self.notify("exit", {})
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


class Document:
    def __init__(self, server: Server, path: pathlib.Path):
        self.server, self.uri, self.text, self.version = server, path.as_uri(), path.read_text(), 1
        self.original = self.text
        server.notify("textDocument/didOpen", {"textDocument": {"uri": self.uri, "languageId": "axaml", "version": 1, "text": self.text}})

    def position(self, offset: int) -> dict:
        return {"line": self.text.count("\n", 0, offset), "character": offset - (self.text.rfind("\n", 0, offset) + 1)}

    def insert(self, offset: int, s: str):
        p = self.position(offset)
        self.version += 1
        self.server.notify("textDocument/didChange", {"textDocument": {"uri": self.uri, "version": self.version},
                                                      "contentChanges": [{"range": {"start": p, "end": p}, "text": s}]})
        self.text = self.text[:offset] + s + self.text[offset:]

    def restore(self):
        self.version += 1
        self.server.notify("textDocument/didChange", {"textDocument": {"uri": self.uri, "version": self.version},
                                                      "contentChanges": [{"text": self.original}]})
        self.text = self.original

    def at(self, offset: int) -> dict:
        return {"textDocument": {"uri": self.uri}, "position": self.position(offset)}


def stats(name: str, times: list[float], sizes: list[int], publishes: int, **extra) -> dict:
    t = sorted(times)
    return {"scenario": name, "n": len(t), "median_ms": round(statistics.median(t), 2),
            "p95_ms": round(t[max(0, int(round(len(t) * 0.95)) - 1)], 2), "max_ms": round(t[-1], 2),
            "bytes": int(statistics.median(sizes)) if sizes else 0, "publishes": publishes, **extra}


def typing(server: Server, doc: Document, project: pathlib.Path) -> dict:
    anchor = doc.text.index("</StackPanel>")
    typed = '<TextBlock Text="{Binding Title}" Margin="4 2"/>'[:40]
    times, sizes = [], []
    for i, ch in enumerate(typed):
        doc.insert(anchor + i, ch)
        end = doc.position(len(doc.text))
        ms, size, _ = server.request("textDocument/codeAction", {"textDocument": {"uri": doc.uri},
                                                                  "range": {"start": {"line": 0, "character": 0}, "end": end},
                                                                  "context": {"diagnostics": []}})
        times.append(ms)
        sizes.append(size)
    doc.restore()
    return stats("typing", times, sizes, 0)


def complete_asset(server: Server, doc: Document, project: pathlib.Path) -> dict:
    anchor = doc.text.index("</StackPanel>")
    doc.insert(anchor, '<Image Source=""/>')
    cursor = anchor + len('<Image Source="')
    times, sizes, found = [], [], False
    for _ in range(4):
        cursor = anchor + len('<Image Source="')
        for ch in "/ico_5_4":
            doc.insert(cursor, ch)
            cursor += 1
            ms, size, msg = server.request("textDocument/completion", doc.at(cursor))
            times.append(ms)
            sizes.append(size)
            items = (msg.get("result") or {}).get("items", [])
            found = any("ico_5_4" in i["label"] for i in items)
        doc.restore()
        doc.insert(anchor, '<Image Source=""/>')
    doc.restore()
    return stats("complete-asset", times, sizes, 0, found=found)


def key_offset(doc: Document) -> int:
    return doc.text.index('x:Key="Accent"') + len('x:Key="') + 2


def references(server: Server, doc: Document, project: pathlib.Path) -> dict:
    params = {**doc.at(key_offset(doc)), "context": {"includeDeclaration": True}}
    times, sizes, count = [], [], 0
    for _ in range(40):
        ms, size, msg = server.request("textDocument/references", params)
        times.append(ms)
        sizes.append(size)
        count = len(msg.get("result") or [])
    return stats("references", times, sizes, 0, results=count)


def rename(server: Server, doc: Document, project: pathlib.Path) -> dict:
    times, sizes, edits = [], [], 0
    for _ in range(40):
        ms1, s1, _ = server.request("textDocument/prepareRename", doc.at(key_offset(doc)))
        ms2, s2, msg = server.request("textDocument/rename", {**doc.at(key_offset(doc)), "newName": "AccentBench"})
        times.append(ms1 + ms2)
        sizes.append(s1 + s2)
        changes = ((msg.get("result") or {}).get("changes") or {})
        edits = sum(len(v) for v in changes.values())
    return stats("rename", times, sizes, 0, edits=edits)


def resolve(server: Server, doc: Document, project: pathlib.Path) -> dict:
    large = project / "Assets" / "Generated" / "large.png"
    item = {"label": "large.png", "kind": 17, "data": {"preview": str(large), "root": str(project)}}
    times, sizes = [], []
    for _ in range(40):
        ms, size, _ = server.request("completionItem/resolve", item)
        times.append(ms)
        sizes.append(size)
    return stats("resolve", times, sizes, 0, first_ms=round(times[0], 2))


def cancel(server: Server, doc: Document, project: pathlib.Path) -> dict:
    params = {**doc.at(key_offset(doc)), "context": {"includeDeclaration": True}}
    times, sizes, cancelled = [], [], 0
    for _ in range(40):
        rid, sent = server.start("textDocument/references", params)
        time.sleep(0.005)
        server.notify("$/cancelRequest", {"id": rid})
        arrived, size, msg = server.wait(lambda m: m.get("id") == rid)
        times.append((arrived - sent) * 1000)
        sizes.append(size)
        cancelled += 1 if (msg.get("error") or {}).get("code") == -32800 else 0
    return stats("cancel", times, sizes, 0, cancelled=cancelled)


SCENARIOS = {"typing": typing, "complete-asset": complete_asset, "references": references, "rename": rename,
             "resolve": resolve, "cancel": cancel}


def compare(results: dict, baseline: dict) -> list[str]:
    problems = []
    for name, now in results.items():
        before = baseline.get(name)
        if not before:
            continue
        for key in ("median_ms", "p95_ms"):
            old, new = before[key], now[key]
            if new > old * (1 + REGRESSION[key]) and new - old > NOISE_MS[key]:
                problems.append(f"{name}.{key}: {old} -> {new} ms (+{(new / old - 1) * 100:.0f}%)")
    return problems


class Session:
    """One server with MainView.axaml open on the project, ready for scenarios."""

    def __init__(self, dll: str, project: pathlib.Path):
        self.project = project
        self.server = Server(dll)
        self.server.request("initialize", {"processId": None, "rootUri": None, "capabilities": {"window": {"workDoneProgress": True}}})
        self.server.notify("initialized", {})
        self.doc = Document(self.server, project / "Views" / "MainView.axaml")
        self.server.wait(lambda m: m.get("method") == "$/progress" and m["params"]["value"]["kind"] == "end")
        self.server.settle(2.0)

    def run(self, name: str) -> dict:
        before = self.server.publishes
        result = SCENARIOS[name](self.server, self.doc, self.project)
        self.server.settle(1.0)
        result["publishes"] = self.server.publishes - before
        return result


def measure(sessions: list[Session], name: str) -> list[dict]:
    """Best-of-ROUNDS result for each session, rounds alternating between sessions."""
    rounds: list[list[dict]] = [[] for _ in sessions]
    for _ in range(ROUNDS):
        for i, session in enumerate(sessions):
            rounds[i].append(session.run(name))
    return [min(r, key=lambda x: x["median_ms"]) for r in rounds]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dll")
    ap.add_argument("project")
    ap.add_argument("--base", help="the old server's dll, measured side by side as the reference")
    ap.add_argument("--scenario", action="append", choices=list(SCENARIOS))
    ap.add_argument("--out")
    ap.add_argument("--compare")
    args = ap.parse_args()
    project = pathlib.Path(args.project).resolve()
    sessions: list[Session] = []
    results, base = {}, {}
    problems: list[str] = []
    try:
        sessions.append(Session(args.dll, project))
        if args.base:
            sessions.append(Session(args.base, project))
        for name in args.scenario or list(SCENARIOS):
            for s in sessions:
                s.run(name)  # warm-up: JIT and first-use caches; not reported
            measured = measure(sessions, name)
            results[name] = measured[0]
            line = dict(measured[0])
            if args.base:
                base[name] = measured[1]
                line["base"] = {k: measured[1][k] for k in ("median_ms", "p95_ms", "max_ms")}
            print(json.dumps(line), flush=True)
        reference = base if args.base else json.loads(pathlib.Path(args.compare).read_text()) if args.compare else None
        if reference is not None:
            problems = compare(results, reference)
            if problems:
                # A regression must reproduce: measure the flagged scenarios again and keep the better results.
                print("re-measuring:", "; ".join(problems), file=sys.stderr)
                for name in sorted({p.split(".")[0] for p in problems}):
                    again = measure(sessions, name)
                    if again[0]["median_ms"] < results[name]["median_ms"]:
                        results[name] = again[0]
                    if args.base and again[1]["median_ms"] < base[name]["median_ms"]:
                        base[name] = again[1]
                problems = compare(results, reference)
    finally:
        for s in sessions:
            s.server.stop()
    if args.out:
        pathlib.Path(args.out).write_text(json.dumps(results, indent=2) + "\n")
    for p in problems:
        print("REGRESSION", p, file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
