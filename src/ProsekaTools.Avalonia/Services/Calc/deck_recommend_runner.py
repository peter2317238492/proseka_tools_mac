#!/usr/bin/env python3
import argparse
import json
import os
import sys
import time
from typing import Optional, List

try:
    from sekai_deck_recommend_cpp import SekaiDeckRecommend, DeckRecommendOptions
except Exception as exc:  # pragma: no cover - import error path
    print(f"[deck_recommend_runner] Failed to import sekai_deck_recommend_cpp: {exc}", file=sys.stderr)
    sys.exit(2)


EVENTS_URLS = {
    "jp": "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-diff/main/events.json",
    "tw": "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-tc-diff/main/events.json",
    "en": "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-en-diff/main/events.json",
    "kr": "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-kr-diff/main/events.json",
    "cn": "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-cn-diff/main/events.json",
}


def get_app_data_root() -> str:
    if sys.platform == "win32":
        base = os.environ.get("APPDATA") or os.path.expanduser("~")
    elif sys.platform == "darwin":
        base = os.path.expanduser("~/Library/Application Support")
    else:
        base = os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config")
    return os.path.join(base, "ProsekaTools")


def get_events_cache_path(region: str) -> str:
    return os.path.join(get_app_data_root(), "cache", "events", f"events_{region}.json")


def get_assets_events_fallback() -> Optional[str]:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    app_root = os.path.abspath(os.path.join(script_dir, "..", ".."))
    candidate = os.path.join(app_root, "Assets", "master", "events.json")
    return candidate if os.path.exists(candidate) else None


def try_download_events_json(region: str) -> Optional[bytes]:
    url = EVENTS_URLS.get(region, EVENTS_URLS["jp"])
    try:
        import requests  # type: ignore
        resp = requests.get(url, timeout=15)
        if resp.status_code == 200 and resp.content:
            return resp.content
    except Exception:
        return None
    return None


def try_load_events_json(region: str) -> Optional[bytes]:
    # try download
    data = try_download_events_json(region)
    if data:
        cache_path = get_events_cache_path(region)
        os.makedirs(os.path.dirname(cache_path), exist_ok=True)
        try:
            with open(cache_path, "wb") as f:
                f.write(data)
        except Exception:
            pass
        return data

    # cache
    cache_path = get_events_cache_path(region)
    if os.path.exists(cache_path):
        try:
            with open(cache_path, "rb") as f:
                return f.read()
        except Exception:
            pass

    # fallback assets
    fallback = get_assets_events_fallback()
    if fallback:
        try:
            with open(fallback, "rb") as f:
                return f.read()
        except Exception:
            pass

    return None


def get_current_event_id(region: str) -> Optional[int]:
    data = try_load_events_json(region)
    if not data:
        return None
    try:
        doc = json.loads(data)
        if not isinstance(doc, list):
            return None
        now_ms = int(time.time() * 1000)
        current_id = None
        current_start = -2**63
        for el in doc:
            if not isinstance(el, dict):
                continue
            start_at = try_get_int(el, "startAt")
            aggregate_at = try_get_int(el, "aggregateAt")
            if start_at is None or aggregate_at is None:
                continue
            if start_at <= now_ms < aggregate_at:
                event_id = try_get_int(el, "id")
                if event_id is not None and start_at >= current_start:
                    current_start = start_at
                    current_id = event_id
        return current_id
    except Exception:
        return None


def try_get_int(d: dict, key: str) -> Optional[int]:
    if key not in d:
        return None
    v = d.get(key)
    if isinstance(v, int):
        return v
    if isinstance(v, str):
        try:
            return int(v)
        except Exception:
            return None
    return None


def parse_fixed_cards(text: Optional[str]) -> Optional[List[int]]:
    if not text:
        return None
    parts = [p.strip() for p in text.split(",") if p.strip()]
    if not parts:
        return None
    result: List[int] = []
    for p in parts:
        try:
            result.append(int(p))
        except Exception:
            pass
    return result or None


def build_options(args: argparse.Namespace, event_id: Optional[int]) -> DeckRecommendOptions:
    options = DeckRecommendOptions()
    options.target = args.target
    options.algorithm = args.algorithm
    options.region = args.region
    options.user_data_file_path = args.suite_json
    options.live_type = args.live_type
    options.music_id = args.music_id
    options.music_diff = args.music_diff
    options.limit = args.limit
    if event_id is not None:
        options.event_id = event_id
    fixed_cards = parse_fixed_cards(args.fixed_cards)
    if fixed_cards:
        options.fixed_cards = fixed_cards
    return options


def parse_args(argv: List[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Proseka deck recommend runner")
    parser.add_argument("--suite-json", required=True, help="suite JSON path (owned_cards)")
    parser.add_argument("--master-dir", required=True, help="masterdata directory")
    parser.add_argument("--music-metas", required=True, help="music metas json path")
    parser.add_argument("--region", default="jp")
    parser.add_argument("--live-type", default="multi")
    parser.add_argument("--music-id", type=int, default=1)
    parser.add_argument("--music-diff", default="expert")
    parser.add_argument("--target", default="score")
    parser.add_argument("--algorithm", default="ga")
    parser.add_argument("--limit", type=int, default=10)
    parser.add_argument("--event-id", type=int, default=None)
    parser.add_argument("--fixed-cards", default=None)
    parser.add_argument("--output", required=True, help="output json path")
    return parser.parse_args(argv)


def main(argv: List[str]) -> int:
    args = parse_args(argv)
    event_id = args.event_id
    if event_id is not None and event_id <= 0:
        event_id = None
    if event_id is None:
        event_id = get_current_event_id(args.region)
        if event_id is not None:
            print(f"[deck_recommend_runner] Use current event id {event_id} for region {args.region}")

    try:
        recommender = SekaiDeckRecommend()
        recommender.update_masterdata(args.master_dir, args.region)
        recommender.update_musicmetas(args.music_metas, args.region)
        options = build_options(args, event_id)
        result = recommender.recommend(options)
        result_dict = result.to_dict() if hasattr(result, "to_dict") else result
        os.makedirs(os.path.dirname(args.output), exist_ok=True)
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(result_dict, f, ensure_ascii=False)
        print(f"[deck_recommend_runner] Wrote {args.output}")
        return 0
    except Exception as exc:
        print(f"[deck_recommend_runner] Failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
