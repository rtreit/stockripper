"""Tests for :class:`MarketCapBand.classify`."""

from __future__ import annotations

from decimal import Decimal

import pytest

from stockripper.data.universe_policy import MarketCapBand


@pytest.mark.parametrize(
    ("cap_usd", "expected"),
    [
        (Decimal("250000000000"), MarketCapBand.MEGA),
        (Decimal("200000000001"), MarketCapBand.MEGA),
        (Decimal("199999999999"), MarketCapBand.LARGE),
        (Decimal("10000000001"), MarketCapBand.LARGE),
        (Decimal("9999999999"), MarketCapBand.MID),
        (Decimal("2000000001"), MarketCapBand.MID),
        (Decimal("1999999999"), MarketCapBand.SMALL),
        (Decimal("300000001"), MarketCapBand.SMALL),
        (Decimal("299999999"), MarketCapBand.MICRO),
        (Decimal("50000001"), MarketCapBand.MICRO),
        (Decimal("49999999"), MarketCapBand.NANO),
        (Decimal("1"), MarketCapBand.NANO),
    ],
)
def test_classify_boundaries(cap_usd: Decimal, expected: MarketCapBand) -> None:
    assert MarketCapBand.classify(cap_usd) is expected


def test_classify_none_for_zero_or_negative() -> None:
    assert MarketCapBand.classify(None) is None
    assert MarketCapBand.classify(Decimal("0")) is None
    assert MarketCapBand.classify(Decimal("-1")) is None
