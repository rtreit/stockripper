---
name: langgraph-workflow
description: Design and review StockRipper LangGraph workflow changes, checkpoints, and state transitions.
---

# LangGraph Workflow Skill

## When to Use

- Adding or refactoring a workflow node, graph state, or checkpoint flow
- Designing agent coordination, judge handoff, or replay behavior
- Debugging workflow stalls, retries, or recovery logic

## Key Rules

1. Keep graph state explicit and serializable.
2. Use checkpoints and run IDs to support replay, debugging, and post-hoc analysis. Checkpointing is for reproducibility, not for inserting human approval into the live flow.
3. Make each state transition testable and deterministic.
4. Keep external provider calls and LLM tool calls isolated from the graph core where practical.
