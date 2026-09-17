#!/usr/bin/env python3
"""Smoke test for an embedding server (see embedding_server.py).

Verifies the contract the C# side depends on:

1. ``/health`` reports the expected dimension and backend.
2. ``/embed`` returns L2-normalized vectors of that dimension.
3. **query and passage embeddings differ**, which proves the model's query-side
   instruction is actually applied (on Qwen3-Embedding this is the difference between a
   working index and a silently degraded one).
4. A known-similar pair scores higher than a known-unrelated pair, so the space is sane.
5. Batching and repeated calls are deterministic.

Usage:
    python smoke_embedding_server.py [--url http://127.0.0.1:5001] [--expect-dim 1024]
"""

from __future__ import annotations

import argparse
import math
import sys
from typing import List

import requests


def embed(url: str, texts: List[str], mode: str) -> List[List[float]]:
    response = requests.post(
        f"{url}/embed",
        json={"mode": mode, "items": [{"text": t} for t in texts]},
        timeout=120,
    )
    response.raise_for_status()
    return response.json()["vectors"]


def cosine(a: List[float], b: List[float]) -> float:
    return sum(x * y for x, y in zip(a, b))


def norm(a: List[float]) -> float:
    return math.sqrt(sum(x * x for x in a))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5001")
    parser.add_argument("--expect-dim", type=int, default=1024)
    args = parser.parse_args()

    failures: List[str] = []
    health = requests.get(f"{args.url}/health", timeout=30).json()
    print(f"health: {health}")

    if health.get("dim") != args.expect_dim:
        failures.append(f"health dim {health.get('dim')} != expected {args.expect_dim}")

    query = "how does a pawn's hunger need decrease over time"
    passages = [
        "public class Need_Food : Need { public override void NeedInterval() { ... } }",
        "public class CompPowerTrader : ThingComp { public float PowerOutput; }",
        "xml:ThingDef:Steel <ThingDef><defName>Steel</defName><stuffProps>...</stuffProps></ThingDef>",
    ]

    query_vec = embed(args.url, [query], "query")[0]
    passage_vecs = embed(args.url, passages, "passage")

    if len(query_vec) != args.expect_dim:
        failures.append(f"query vector dim {len(query_vec)} != {args.expect_dim}")
    for index, vec in enumerate(passage_vecs):
        if len(vec) != args.expect_dim:
            failures.append(f"passage[{index}] dim {len(vec)} != {args.expect_dim}")

    norms = [norm(query_vec)] + [norm(v) for v in passage_vecs]
    for index, value in enumerate(norms):
        # The server normalizes in float32, so this is tight; bfloat16 forward passes are not.
        if abs(value - 1.0) > 1e-5:
            failures.append(f"vector[{index}] is not L2-normalized: |v| = {value:.6f}")
    print(f"norms: {[round(n, 6) for n in norms]}")

    # Same text through both modes: they MUST differ, because the query side carries the
    # model's instruction prefix and the passage side must not.
    same_text = "Need_Food pawn hunger interval"
    as_query = embed(args.url, [same_text], "query")[0]
    as_passage = embed(args.url, [same_text], "passage")[0]
    mode_cosine = cosine(as_query, as_passage)
    print(f"cosine(same text as query, as passage) = {mode_cosine:.4f}")
    if mode_cosine > 0.999:
        failures.append(
            "query and passage embeddings are identical — the query instruction is NOT being "
            "applied (check the backend and the model's prompts)"
        )

    ranked = sorted(
        ((cosine(query_vec, vec), text) for vec, text in zip(passage_vecs, passages)),
        reverse=True,
    )
    print("ranking for the hunger query:")
    for score, text in ranked:
        print(f"  {score:+.4f}  {text[:70]}")
    if not ranked[0][1].startswith("public class Need_Food"):
        failures.append("the hunger passage did not rank first for the hunger query")

    # Batch == single, and repeated calls agree. bfloat16 GPU kernels are not bit-reproducible, so
    # this checks agreement to a cosine tolerance rather than exact equality.
    batched = embed(args.url, passages, "passage")
    for index, (a, b) in enumerate(zip(batched, passage_vecs)):
        cosine_ab = cosine(a, b)
        if abs(cosine_ab - 1.0) > 1e-3:
            failures.append(f"passage[{index}] differs between calls (cosine {cosine_ab:.6f})")

    print()
    if failures:
        print("FAILED:")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print("PASS: dimension, normalization, query/passage distinction, ranking and determinism")
    return 0


if __name__ == "__main__":
    sys.exit(main())
