# ADR-0006: Initial trading scope

Status: Accepted

Date: 2026-08-18

## Context

The first trading scope must be small enough to test authority, exposure,
submission, reconciliation, and recovery without unrelated product complexity.

## Decision

Support United States exchange-listed stocks and ETFs in the first Broker
adapter. Allow only long positions, whole-share quantities, limit orders, and
`day` time in force. Allow trading only during regular market hours.

The initial scope excludes these capabilities:

- Margin borrowing
- Short sales
- Options
- Cryptoassets
- Fractional shares
- Extended-hours trading
- Market orders
- Stop orders
- Trailing orders
- Bracket orders
- Order replacement

Use one configured base currency. Reject an instrument, order form, direction,
or schedule that is outside the Goal and policy bindings.

## Safety impact

This scope bounds order semantics and reconciliation cases. Limit prices give
the Risk component an explicit price constraint for exposure calculation.
Whole shares avoid early quantity precision and fractional-order differences.

## Rejected options

- Market orders were rejected because their execution price is not bounded.
- Fractional shares were deferred because they add notional and precision
  behavior to allocation and Fill reconciliation.
- Short sales and margin were deferred because they add borrowing, maintenance,
  and forced-liquidation risks.
- Options and cryptoassets were deferred because they need different market,
  risk, schedule, and settlement models.

## Migration constraints

Each added order type or instrument class needs an approved functional change,
versioned policy fields, Broker capability checks, and acceptance tests. Existing
Goal versions remain limited to their published capability set.
