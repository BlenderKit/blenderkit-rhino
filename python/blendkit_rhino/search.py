"""Build search payloads and hold cached results.

Only asset types that make sense in Rhino are exposed:
    MODEL, MATERIAL, HDR, PRINTABLE

BRUSH / NODEGROUP / ADDON / SCENE are intentionally omitted.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

SUPPORTED_ASSET_TYPES = ("MODEL", "MATERIAL", "HDR", "PRINTABLE")
MAX_PAGE_SIZE = 80


@dataclass
class SearchFilters:
    style: str = ""
    design_year: str = ""
    polycount: str = ""
    texture_resolution: str = ""
    file_size: str = ""
    condition: str = ""
    free_only: bool = False
    bookmarks: bool = False
    quality_limit: int = 0
    license: str = ""
    order: str = ""
    category: str = ""


@dataclass
class SearchState:
    query: str = ""
    asset_type: str = "MODEL"
    filters: SearchFilters = field(default_factory=SearchFilters)
    page: int = 1
    results: list[dict[str, Any]] = field(default_factory=list)
    total: int = 0
    last_task_id: str | None = None


def build_payload(state: SearchState, api_key: str, addon_version: str) -> dict[str, Any]:
    f = state.filters
    return {
        "PREFS": {
            "api_key": api_key,
            "addon_version": addon_version,
            "software": "rhino",
            "page_size": MAX_PAGE_SIZE,
        },
        "urlquery": _url_query(state),
        "query": state.query,
        "asset_type": state.asset_type,
        "page": state.page,
        "filters": {
            k: v for k, v in {
                "style": f.style, "design_year": f.design_year,
                "polycount": f.polycount, "texture_resolution": f.texture_resolution,
                "file_size": f.file_size, "condition": f.condition,
                "free_only": f.free_only, "bookmarks_rating": 1 if f.bookmarks else 0,
                "quality_limit": f.quality_limit, "license": f.license,
                "order": f.order, "category": f.category,
            }.items() if v not in ("", 0, False)
        },
    }


def _url_query(state: SearchState) -> str:
    """Build the blendkit.com-style URL query string the server expects."""
    parts = [f"query={state.query}"] if state.query else []
    parts.append(f"asset_type={state.asset_type.lower()}")
    f = state.filters
    if f.category: parts.append(f"category_subtree={f.category}")
    if f.free_only: parts.append("is_free=True")
    if f.quality_limit: parts.append(f"quality_count>={f.quality_limit}")
    if f.license: parts.append(f"license={f.license}")
    if f.order: parts.append(f"order={f.order}")
    return "+".join(parts)
