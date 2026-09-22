# AI coding environment

This repository provides one shared rule set and small provider adapters.

## Canonical guidance

`AGENTS.md` contains the durable project rules. Update it when a rule must
apply to all coding agents.

`CLAUDE.md` imports `AGENTS.md`. The Copilot adapter points to the same file.
Do not maintain three independent copies of project rules.

## Directory layout

- `AGENTS.md`: Shared project context and safety rules
- `CLAUDE.md`: Claude Code import and provider notes
- `.codex/`: Codex project settings and specialized agents
- `.claude/agents/`: Claude Code project subagents
- `.agents/skills/`: Canonical reusable project skills
- `.github/copilot-instructions.md`: Copilot repository adapter
- `.github/agents/`: Copilot custom agents
- `.github/instructions/`: Copilot path rules
- `.github/prompts/`: Copilot reusable prompts
- `.github/skills/`: Copilot-compatible skill copies
- `docs/documentation-style.md`: Project writing standard

## Maintenance rules

- Keep `AGENTS.md` concise enough to load in every task.
- Put a multi-step workflow in a skill or prompt.
- Put a specialized review role in an agent file.
- Put file-specific Copilot rules in `.github/instructions/`.
- Keep `.agents/skills/` and `.github/skills/` copies identical.
- Keep credentials and personal defaults outside the repository.
- Update build and test commands after the implementation stack is selected.

## Current limitation

The repository has no implementation stack or build system. Agent instructions
must not assume one until the project adds it.
