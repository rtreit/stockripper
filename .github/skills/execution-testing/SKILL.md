---
name: execution-testing
description: >-
  Design or implement Stockripper tests for execution modes, broker behavior,
  authorization, idempotency, reconciliation, or restart recovery. Use for
  execution test plans, regression tests, integration tests, and safety test
  matrices. Do not use to run tests against a live account.
---

# Execution testing

Read `AGENTS.md` before you design or implement tests.

Build the smallest matrix that covers the changed behavior across applicable
modes. Include negative cases for wrong account, wrong endpoint, stale goal,
stale policy, allocation excess, and schedule violations when relevant.

Exercise retries and restart points around each external action. Verify that
one intent produces no more than one broker order. Exercise partial fills,
cancellations, rejections, timeouts, and out-of-order broker events.

Assert durable audit continuity from goal version to final position. Verify
kill-switch behavior in each affected order state.

Use deterministic fixtures, broker fakes, or an approved paper environment.
Never place a live order from an automated test.
