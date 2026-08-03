# AGENTS.md — see ./CLAUDE.md

This repo's working notes, stack, build commands, architecture, and gotchas
all live in `./CLAUDE.md`. Read it at the start of every session — it is the
source of truth for this project.

Global rules and shared memory: see `~/.codex/AGENTS.md`, which references
`~/.claude/CLAUDE.md`, `~/CLAUDE.md`, and the shared memory dir at
`~/.claude/projects/c--Users----repos/memory/`.

## Documentation hygiene (MANDATORY — all agents)

These repos use a strict documentation structure. Violating these rules creates
sprawl that requires manual cleanup across 275+ repos.

### Allowed root-level .md files
- `README.md` — project overview (the ONLY .md tracked in git; all others are gitignored)
- `CLAUDE.md` / `AGENTS.md` — AI working notes
- `CHANGELOG.md` — chronological release history
- `ROADMAP.md` — **remaining/incomplete work only** (never completed items)
- `RESEARCH.md` — consolidated research conclusions (one file, updated in place)

### Never create
- `AUTONOMOUS-LOOP-STATE.md`, `COMPLETED.md`, `ClaudeReadMe.md`, `PROJECT_CONTEXT.md`
- `HANDOFF.md`, `TODO.md`, `SESSION_SUMMARY_*.md`, `prompt*.md`
- Dated research files (`RESEARCH_FEATURE_PLAN_2026-*.md`) — update RESEARCH.md in place

### ROADMAP.md rules
- Only incomplete work. When you finish something, **delete it** (git history is the record).
- Never append `[x]` completed checkmarks, cycle logs, or continuation state.

### RESEARCH.md rules
- One file per repo. Update in place — never create dated variants or scatter across files.
