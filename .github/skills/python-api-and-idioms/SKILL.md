---
name: python-api-and-idioms
description: Apply current Python API and idiom guidance to StockRipper code so modules stay clear, typed, and testable.
---

# Python API and Idioms Skill

## When to Use

- Designing new Python modules or public functions
- Refactoring Python APIs for clarity or maintainability
- Checking whether an older pattern still makes sense in the current StockRipper codebase

## Key Rules

1. Prefer explicit types, small functions, and clear boundaries over clever abstractions.
2. Keep provider and workflow code separate so tests and replay remain straightforward.
3. Use dataclasses or Pydantic models where they improve clarity and validation.
4. Favor current, documented Python patterns over older workarounds.
