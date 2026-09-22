# GitHub Copilot instructions

Use the root `AGENTS.md` as the canonical Stockripper guidance. Read the
functional baseline before changing product behavior.

Copilot-specific rules:

- Apply path rules from `.github/instructions/`.
- Use custom agents in `.github/agents/` for specialized work.
- Use reusable prompts in `.github/prompts/`.
- Use project skills in `.github/skills/` when their descriptions match.
- Treat `.codex/` and `.claude/` as provider-specific adapters.
- Do not duplicate durable repository rules here. Update `AGENTS.md`.
