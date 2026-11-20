#!/usr/bin/env python3
"""Builds a large, deterministic copy of samples/ for benchmarks (docs/plans/PERFORMANCE.md, P0.1).

usage: make-big-project.py <out-dir> [--no-restore]

The copy is samples/SampleApp plus:
  - 30,000 files under obj/BenchJunk/ (the file index must prune them)
  - 3,000 SVG and PNG assets in nested folders (Assets/Generated/set_NN/ico_NN_MM.*), and one large PNG
  - 500 views (Views/Generated/ViewNNN.axaml): Second.axaml without x:Class, each with its own x:Key, a Classes= use
    and a {StaticResource Accent} use (Accent is declared in MainView.axaml)
  - 200 C# files with Classes.Add("…") and Resources["…"]

Everything is fixed: same names and bytes on every run. The sample app is restored so the server can load it."""
import pathlib, shutil, struct, subprocess, sys, zlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
JUNK_DIRS, JUNK_PER_DIR = 300, 100
ASSET_SETS, ASSETS_PER_SET = 30, 50  # each asset is written as .svg and .png: 30 * 50 * 2 = 3,000
VIEWS, CODE_FILES, CLASS_VARIANTS = 500, 200, 10


def png(width: int, height: int) -> bytes:
    """A valid RGB PNG with a fixed gradient (no randomness, so the bytes never change)."""
    raw = bytearray()
    for y in range(height):
        raw.append(0)  # filter: none
        for x in range(width):
            raw += bytes((x * 255 // max(1, width - 1), y * 255 // max(1, height - 1), (x ^ y) & 0xFF))

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def svg(i: int, j: int) -> str:
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle cx="12" cy="12" r="{1 + (i + j) % 11}"/></svg>\n'


VIEW = """<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SampleApp.ViewModels"
             x:DataType="vm:MainViewModel">
    <UserControl.Resources>
        <SolidColorBrush x:Key="BenchBrush{i:03}" Color="#{i:06X}"/>
    </UserControl.Resources>
    <StackPanel>
        <TextBlock Text="{{Binding Title}}" Foreground="{{StaticResource Accent}}"/>
        <Button Classes="Card BenchClass{c}" Content="{{Binding Title}}" Command="{{Binding SaveCommand}}"/>
        <Border Classes.Card="{{Binding IsBusy}}" Background="{{DynamicResource BenchBrush{i:03}}}"/>
    </StackPanel>
</UserControl>
"""

CODE = """namespace SampleApp.Bench;

public static class Code{i:03}
{{
    public static void Apply(Avalonia.Controls.Control control)
    {{
        control.Classes.Add("BenchClass{c}");
        _ = control.Resources["Accent"];
    }}
}}
"""


def main() -> None:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 1:
        raise SystemExit(__doc__)
    out = pathlib.Path(args[0]).resolve()
    if out.exists() and any(out.iterdir()):
        raise SystemExit(f"{out} is not empty")
    shutil.copytree(ROOT / "samples", out, dirs_exist_ok=True, ignore=shutil.ignore_patterns("bin", "obj"))
    app = out / "SampleApp"

    for d in range(JUNK_DIRS):
        folder = app / "obj" / "BenchJunk" / f"d{d:03}"
        folder.mkdir(parents=True)
        for f in range(JUNK_PER_DIR):
            (folder / f"f{f:03}.axaml").write_text(f"<Junk N=\"{d}-{f}\"/>\n")

    small = png(16, 16)
    for i in range(ASSET_SETS):
        folder = app / "Assets" / "Generated" / f"set_{i:02}"
        folder.mkdir(parents=True)
        for j in range(ASSETS_PER_SET):
            (folder / f"ico_{i}_{j}.svg").write_text(svg(i, j))
            (folder / f"ico_{i}_{j}.png").write_bytes(small)
    (app / "Assets" / "Generated" / "large.png").write_bytes(png(1024, 1024))

    views = app / "Views" / "Generated"
    views.mkdir(parents=True)
    for i in range(VIEWS):
        (views / f"View{i:03}.axaml").write_text(VIEW.format(i=i, c=i % CLASS_VARIANTS))

    code = app / "Bench"
    code.mkdir()
    for i in range(CODE_FILES):
        (code / f"Code{i:03}.cs").write_text(CODE.format(i=i, c=i % CLASS_VARIANTS))

    if "--no-restore" not in sys.argv:
        subprocess.run(["dotnet", "restore", str(app / "SampleApp.csproj"), "-v", "q"], check=True, stdout=subprocess.DEVNULL)
    print(app)


if __name__ == "__main__":
    main()
