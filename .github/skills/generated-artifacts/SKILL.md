---
name: generated-artifacts
description: Manage generated artifacts in StockRipper so reports, caches, leaderboard exports, and reusable outputs stay discoverable and reviewable.
---

# Generated Artifacts Skill

## When to Use

- Adding generated reports, caches, or replay outputs
- Verifying what should be committed versus what should stay local
- Reviewing artifact naming and cleanup behavior

## Key Rules

1. Keep generated files out of the main code path unless they are intentionally part of the workflow.
2. Use consistent naming and clear location for generated reports, snapshots, and replay data.
3. Do not commit secrets, credentials, or user-specific data.
4. If a generated artifact is important for debugging, document how to regenerate it.
