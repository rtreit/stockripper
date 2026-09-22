# ADR-0005: Broker and market data

Status: Accepted

Date: 2026-08-18

## Context

The first Broker must provide a paper environment, explicit account identity,
stable client order identifiers, order history, and order updates. The market
data source must support deterministic snapshots and current decisions.

## Decision

Use the Alpaca Trading API as the first Broker. Phase 1 uses only the Alpaca
paper environment. Build a project-owned adapter with typed HTTP contracts.
Do not expose Alpaca types outside the adapter.

Pin the expected paper endpoint identity in the immutable Broker connection
version. Verify the authenticated account before each executable Session. Use
the trading update stream as a low-latency input. Use REST queries as the
authoritative recovery and reconciliation path.

Use Alpaca historical and current stock data as the first market data source.
Phase 0 can use retained fixtures and historical snapshots. Phase 1 can use IEX
data for paper tests. Require consolidated SIP data before the first live Goal
activation.

Do not use a real Broker path in Phase 0. Automated tests use the project Broker
fake. Paper end-to-end tests require an explicit test configuration. Automated
tests must never load a live secret reference.

## Safety impact

Alpaca publishes separate paper and live domains with separate credentials.
The adapter verifies the configured endpoint, environment, and account. REST
reconciliation resolves missed, duplicated, or out-of-order stream events.

Consolidated data reduces the risk that live decisions use one exchange as a
market-wide price. Broker truth remains authoritative for orders, fills, and
positions.

## Rejected options

- A Broker-neutral HTTP gateway was rejected. It creates an additional trust
  boundary without a second Broker requirement.
- A community Broker SDK was rejected as the adapter boundary. Project-owned
  contracts make endpoint selection and payload validation explicit.
- IEX-only data was rejected for live execution. It covers one exchange and is
  not a consolidated market view.
- Direct live integration during Phase 1 was rejected. It bypasses the approved
  delivery gates.

## Migration constraints

Broker identifiers must remain exact strings. Project domain records must not
depend on Alpaca enum names. A second Broker needs a separate adapter,
capability map, fake, reconciliation suite, and connection version.

Changing a Broker endpoint, credential reference, account, or data feed creates
a new immutable version. It cannot mutate an active Goal binding.

## References

- [Alpaca authentication](https://docs.alpaca.markets/us/v1.1/docs/authentication-1)
- [Alpaca WebSocket streaming](https://docs.alpaca.markets/us/docs/websocket-streaming)
- [Alpaca historical stock data](https://docs.alpaca.markets/us/docs/historical-stock-data-1)
- [Alpaca market data FAQ](https://docs.alpaca.markets/us/docs/market-data-faq)
