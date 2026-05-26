# Dashboard Performance Guidance

Keep the UI responsive even when large research packets or ledger history are loaded.

- Limit unnecessary redraws during run updates.
- Batch refreshes when multiple status values change together.
- Prefer incremental updates for charts, tables, and run summaries.
