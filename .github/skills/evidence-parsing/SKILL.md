---
name: evidence-parsing
description: Parse and normalize source evidence and structured external data into StockRipper's internal models.
---

# Source Evidence Parsing Skill

## When to Use

- Normalizing SEC EDGAR, market, or news payloads into internal models
- Reviewing source extraction and citation handling
- Fixing failures in evidence parsing, timestamps, or confidence fields

## Key Rules

1. Preserve the original source reference, retrieval timestamp, and confidence score.
2. Parse into typed models as early as possible.
3. Surface data-quality warnings for incomplete or stale content.
4. Keep parser logic separate from workflow orchestration.
