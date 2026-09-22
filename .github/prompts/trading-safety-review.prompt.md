---
name: trading-safety-review
description: Reviews a Stockripper change for paper and live execution safety before merge.
---

# Trading safety review

Review the change against every affected execution mode.

1. Trace authorization from goal activation to broker submission.
2. Verify exact connection, account, allocation, schedule, and policy binding.
3. Verify paper/live separation and fail-closed behavior.
4. Verify idempotency before retries and after restart.
5. Verify fill reconciliation and final position state.
6. Verify audit continuity and kill-switch behavior.
7. Report findings with concrete failure scenarios and severity.
