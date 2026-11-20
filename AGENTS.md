# Notes for coding agents

## Commits and pull requests

- **Do not credit yourself.** No `Co-Authored-By:` trailer naming an AI model or tool, no "Generated with …" line,
  and no other AI attribution in commit messages, pull request titles or descriptions, or code comments. Commits
  are authored by the person who owns the repository.
- Commit only when asked. Never commit to `main` directly; work on a branch.
- Don't rewrite commits that are already pushed unless asked to.

## How the code is written

Follow [`docs/ENGINEERING.md`](docs/ENGINEERING.md): CUPID rules, the definition of done and the known deviations.
The plans in progress are in [`docs/plans/`](docs/plans/).

## Checks before you say a change is done

| Command | What it checks |
|---|---|
| `mise run ci` | Build with warnings as errors, then the tests (what CI runs) |
| `mise run smoke` | The server answers over LSP stdio like an editor would; output must not change unless intended |
