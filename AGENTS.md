# AGENTS.md

## Project context

Stockripper is a goal-driven market research and trade execution system. It
supports historical analysis, paper trading, and live trading.

The approved functional baseline is `docs/functional-specification.md`. Read
it before you plan or change product behavior. Do not infer missing
requirements as approved behavior.

Phase 0 implementation has started. The accepted technology decisions are in
`docs/architecture/`. Do not replace them without a new decision record.

## Canonical execution modes

Use only these mode values:

- `REPLAY`
- `RESEARCH`
- `PAPER_REVIEW`
- `PAPER_AUTO`
- `LIVE_REVIEW`
- `LIVE_AUTO`

Do not add aliases or silently map an unknown value to another mode. Reject an
unknown or missing executable mode.

## Safety invariants

These rules apply to design, implementation, tests, reviews, and operations.

- Treat paper trading and live trading as separate, first-class paths.
- Keep paper and live credentials, endpoints, accounts, state, and audit data
  visibly distinct.
- Require an immutable goal version for each executable goal.
- Bind that version to the exact broker connection, broker account, capital
  allocation, schedule, and risk-policy version.
- Treat deliberate `LIVE_AUTO` goal activation as the authorization boundary.
- Do not add routine per-session confirmation after valid activation.
- Fail closed when mode, account, connection, allocation, schedule, or policy
  binding is absent, stale, or inconsistent.
- Prevent duplicate orders with durable idempotency and restart-safe state.
- Reconcile requested orders with broker acknowledgments, fills, partial
  fills, cancellations, rejections, and final positions.
- Keep a complete audit trail from goal version through decision, order, fill,
  reconciliation, and resulting position.
- Make the kill switch deterministic, observable, and safe after restart.
- Never let automated tests place a live order.
- Never use a live account to validate ordinary development changes.

A valid losing trade is accepted market risk. The following outcomes are
software failures:

- The system trades the wrong account.
- The system exceeds the authorized capital allocation.
- The system crosses paper and live endpoints.
- The system duplicates an order.
- The system creates an unauditable trade.

## Change workflow

1. Read the functional baseline and the files that the change can affect.
2. State the affected modes and safety invariants.
3. Define acceptance criteria and failure behavior.
4. Add a failing regression test before a defect fix when practical.
5. Implement the smallest complete vertical change.
6. Run the narrow checks first, then the repository quality checks.
7. Review audit, restart, idempotency, and paper/live separation behavior.
8. Update the specification or architecture record when behavior changes.

Do not claim validation that you did not run. Record skipped checks and the
reason.

## Architecture direction

- Use C# on .NET 10 LTS for production components.
- Use PostgreSQL 18 for durable operational and audit state.
- Use an ASP.NET Core Blazor Web App for the Operator interface.
- Run Strategy code in a separate .NET worker through versioned gRPC.
- Use Alpaca paper trading as the first real Broker environment in Phase 1.
- Do not add a real Broker order path during Phase 0.
- Keep paper and live deployments, databases, secrets, and networks separate.

- Keep goal definition, authorization, decision logic, execution, broker
  integration, reconciliation, risk control, and audit storage as explicit
  boundaries.
- Keep broker-specific behavior behind an adapter owned by the execution edge.
- Keep strategy and research code unable to bypass execution authorization.
- Use explicit state machines for goal, session, order, and reconciliation
  lifecycles.
- Make externally visible actions idempotent.
- Persist enough state before an external action to recover safely.
- Prefer deterministic inputs and replayable decisions.
- Use wall-clock time only at explicit boundaries. Inject clocks elsewhere.
- Use decimal or fixed-point values for money and quantity where the selected
  broker permits them. Do not use binary floating point for accounting.
- Store identifiers from external systems exactly. Do not regenerate them.
- Keep the durable audit record append-only unless a documented retention rule
  requires another design.
- Isolate read-only research data from execution credentials and commands.

## Testing direction

- Test all six execution modes.
- Test each executable mode against the wrong endpoint and wrong account.
- Test allocation and schedule boundaries.
- Test duplicate submission, retry, timeout, and restart behavior.
- Test partial fills, cancellations, rejections, and out-of-order broker data.
- Test kill-switch activation during each order state.
- Test audit continuity across process restart.
- Use broker fakes or approved paper environments for automated tests.
- Require an explicit, manual runbook for any test that can reach live trading.

Use `./build.ps1` for the repository build and static checks. Use `./test.ps1`
for the complete automated test suite. Docker is required for the isolated
PostgreSQL 18 integration tests.

## Security and secrets

- Never commit, print, or log credentials, tokens, account secrets, private
  keys, connection strings, or local environment values.
- Store local secrets outside source control.
- Use least-privilege credentials for each environment and account.
- Redact account identifiers in logs intended for broad access.
- Treat broker responses, market data, model output, and imported files as
  untrusted input.
- Do not let model output directly authorize or submit an order.
- Require deterministic application checks after any probabilistic model step.
- Do not weaken account binding, allocation checks, audit records, idempotency,
  or the kill switch to simplify development.

## Style rules

- Never use emojis in code, documentation, comments, or generated copy unless
  the user asks for them.
- Never use em dashes in new prose. Use a hyphen, comma, parentheses, or a new
  sentence.
- Keep comments focused on why code exists.
- Prefer clear names over explanatory comments.
- Avoid speculative abstraction and provider-specific leakage.
- Keep changes small and reviewable.

## Documentation and reports

- Use ASD-STE100 Simplified Technical English, Issue 9, unless the user asks
  for another style.
- Apply this rule to new text and changed text.
- Use American English spelling.
- Use one term for one item or action.
- Use active voice when the agent is known.
- Do not use contractions, Latin abbreviations, or semicolons.
- Put one instruction in each sentence unless actions occur at the same time.
- Use no more than 20 words in a procedural sentence.
- Use no more than 25 words in a descriptive sentence.
- Use no more than six sentences in one paragraph.
- Keep code, identifiers, commands, paths, field names, quoted text, and legal
  text exact.
- Do not claim STE conformance unless you checked the text against Issue 9.

Use `docs/documentation-style.md` for the detailed checklist and controlled
Stockripper terms.

## Git and review

- Preserve user changes and unrelated worktree changes.
- Do not commit, push, open a pull request, or merge unless the user asks.
- Review safety-critical changes with findings first.
- Give each finding a concrete failure scenario and affected mode.
- Treat a plausible unintended live order as the highest severity.
- Treat missing audit evidence as a correctness defect.

## Agent setup

- This file is the canonical shared instruction file.
- Codex reads this file directly. Codex runtime and agent definitions live in
  `.codex/`.
- Claude Code reads `CLAUDE.md`, which imports this file. Claude subagents live
  in `.claude/agents/`.
- GitHub Copilot uses `.github/copilot-instructions.md` and the adapters under
  `.github/`.
- Reusable project skills use `.agents/skills/` as their canonical source.
- Copilot-compatible skill copies live in `.github/skills/`.
- Do not duplicate durable repository rules in provider adapters.
