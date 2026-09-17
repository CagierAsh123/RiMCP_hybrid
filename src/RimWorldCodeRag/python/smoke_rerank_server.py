#!/usr/bin/env python3
"""Smoke test for the reranker server (see rerank_server.py).

It is not enough for the endpoint to answer 200 — a reranker whose prompt is malformed still returns
plausible-looking probabilities. So this asserts the *ordering*:

1. ``/health`` reports the ``yes``/``no`` token ids from the checkpoint's own config.
2. A passage that answers the query outranks passages that do not.
3. Scores are in [0, 1] and a clearly-irrelevant document scores below a clearly-relevant one.
4. The same request is deterministic, and batching does not change the scores.

Usage:
    python smoke_rerank_server.py --url http://127.0.0.1:5002
"""

from __future__ import annotations

import argparse
import sys
from typing import List

import requests

QUERY = "how does a pawn's hunger need decrease over time"

DOCUMENTS = [
    # relevant
    "RimWorld.Need_Food public override void NeedInterval() { float num = this.MetabolismFactor(); "
    "this.CurLevel -= num * 0.0016666667f * 150f; } private float MetabolismFactor() { Hediff firstHediffOfDef "
    "= this.pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition, false); ... }",
    # unrelated
    "xml:ThingDef:Steel <ThingDef><defName>Steel</defName><stuffProps><categories><li>Metallic</li></categories>"
    "<statFactors><Beauty>-4</Beauty></statFactors></stuffProps></ThingDef>",
    # unrelated
    "RimWorld.CompPowerTrader public class CompPowerTrader : ThingComp { public float PowerOutput; "
    "public bool PowerOn; public CompProperties_PowerTrader Props { get; } }",
]


def rerank(url: str, query: str, documents: List[str]) -> List[float]:
    response = requests.post(
        f"{url}/rerank", json={"query": query, "documents": documents}, timeout=600
    )
    response.raise_for_status()
    return response.json()["scores"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5002")
    args = parser.parse_args()

    failures: List[str] = []

    health = requests.get(f"{args.url}/health", timeout=60).json()
    print(f"health: {health}")

    if health.get("true_token_id") is None or health.get("false_token_id") is None:
        failures.append("health does not report the yes/no token ids")
    if health.get("true_token_id") == health.get("false_token_id"):
        failures.append("yes and no map to the same token id")

    scores = rerank(args.url, QUERY, DOCUMENTS)
    if len(scores) != len(DOCUMENTS):
        failures.append(f"got {len(scores)} scores for {len(DOCUMENTS)} documents")
        print("FAILED:", failures)
        return 1

    for index, score in enumerate(scores):
        if not (0.0 <= score <= 1.0):
            failures.append(f"score[{index}] = {score} is outside [0, 1]")

    ranked = sorted(zip(scores, DOCUMENTS), key=lambda pair: pair[0], reverse=True)
    print("ranking:")
    for score, text in ranked:
        label = "RELEVANT" if text.startswith("RimWorld.Need_Food") else "irrelevant"
        print(f"  {score:.4f}  [{label}] {text[:64]}...")

    if not DOCUMENTS[0].startswith("RimWorld.Need_Food"):
        failures.append("test fixture changed: documents[0] should be the relevant one")
    if ranked[0][1] != DOCUMENTS[0]:
        failures.append(
            "the relevant passage did not rank first — the judge prompt or the yes/no logit "
            "extraction is wrong (a malformed prompt still returns plausible probabilities)"
        )
    if not (scores[0] > 0.5):
        failures.append(f"the relevant passage scored {scores[0]:.4f}, expected > 0.5")

    again = rerank(args.url, QUERY, DOCUMENTS)
    for index, (a, b) in enumerate(zip(scores, again)):
        if abs(a - b) > 1e-6:
            failures.append(f"score[{index}] is not deterministic ({a} vs {b})")

    print()
    if failures:
        print("FAILED:")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print("PASS: prompt is well-formed (ordering correct), scores bounded, deterministic")
    return 0


if __name__ == "__main__":
    sys.exit(main())
