---
applyTo: "tests/**,**/*test*,**/*spec*"
---

# Testing

- Test all affected execution modes.
- Test wrong-account and wrong-endpoint rejection.
- Test timeouts, retries, duplicate submissions, and restarts.
- Test partial fills, cancellations, rejections, and out-of-order broker data.
- Test kill-switch behavior in each affected order state.
- Use deterministic fixtures, broker fakes, or an approved paper environment.
- Never place a live order from an automated test.
