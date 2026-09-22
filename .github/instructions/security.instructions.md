---
applyTo: "**/*"
---

# Security

- Never commit or print secrets.
- Use least-privilege credentials for each environment and account.
- Treat broker data, market data, imported files, and model output as untrusted.
- Redact credentials and sensitive account identifiers from diagnostics.
- Keep authorization checks deterministic and outside probabilistic model code.
- Fail closed on account, endpoint, credential, or policy mismatch.
