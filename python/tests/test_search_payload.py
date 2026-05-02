"""Smoke tests that run outside Rhino (no Rhino/Eto imports).

Covers payload shaping — the bits we want to refactor confidently even before
a live Rhino is available.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from blendkit_rhino import search  # noqa: E402


def test_supported_asset_types():
    assert "MODEL" in search.SUPPORTED_ASSET_TYPES
    assert "BRUSH" not in search.SUPPORTED_ASSET_TYPES


def test_build_payload_minimal():
    state = search.SearchState(query="chair", asset_type="MODEL")
    payload = search.build_payload(state, api_key="k", addon_version="0.1.0")
    assert payload["query"] == "chair"
    assert payload["asset_type"] == "MODEL"
    assert payload["PREFS"]["software"] == "rhino"
    assert "style" not in payload["filters"]  # empty filters pruned


def test_build_payload_with_filters():
    state = search.SearchState(
        query="oak",
        asset_type="MATERIAL",
        filters=search.SearchFilters(free_only=True, license="cc0"),
    )
    payload = search.build_payload(state, api_key="k", addon_version="0.1.0")
    assert payload["filters"]["free_only"] is True
    assert payload["filters"]["license"] == "cc0"


if __name__ == "__main__":
    test_supported_asset_types()
    test_build_payload_minimal()
    test_build_payload_with_filters()
    print("OK")
