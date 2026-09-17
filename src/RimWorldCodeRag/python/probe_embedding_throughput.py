#!/usr/bin/env python3
"""Throughput probe for the embedding server.

Runs before a multi-hour re-embed: it reports chunks/s and tokens/s at realistic chunk lengths so
the ETA is measured rather than guessed, and it reports whether any input is being truncated at the
server's max sequence length (the thing a longer-context model is supposed to fix).

Usage:
    python probe_embedding_throughput.py --url http://127.0.0.1:5001 --chunks 64 --chars 3000
"""

from __future__ import annotations

import argparse
import json
import statistics
import time
from typing import List

import requests

SAMPLE = """public class Need_Food : Need
{
    public Need_Food(Pawn pawn) : base(pawn)
    {
        this.threshPercents = new List<float>();
        this.threshPercents.Add(0.2f);
    }

    public override void SetInitialLevel()
    {
        this.CurLevelPercentage = 0.5f;
    }

    public override void NeedInterval()
    {
        if (!this.IsFrozen)
        {
            float num = this.MetabolismFactor();
            this.CurLevel -= num * 0.0016666667f * 150f;
        }
    }

    private float MetabolismFactor()
    {
        Hediff firstHediffOfDef = this.pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition, false);
        if (firstHediffOfDef != null)
        {
            return 0.2f;
        }
        return Mathf.Max(0.1f, this.pawn.health.capacities.GetLevel(PawnCapacityDefOf.Metabolism));
    }
}
"""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5001")
    parser.add_argument("--chunks", type=int, default=64)
    parser.add_argument("--chars", type=int, default=3000, help="Target characters per chunk")
    parser.add_argument("--repeat", type=int, default=2)
    parser.add_argument("--mode", default="passage")
    args = parser.parse_args()

    health = requests.get(f"{args.url}/health", timeout=30).json()
    print(f"health: {json.dumps(health, ensure_ascii=False)}")

    body = SAMPLE * (args.chars // len(SAMPLE) + 1)
    texts = [body[: args.chars] for _ in range(args.chunks)]
    items = [{"text": t} for t in texts]

    # Token count as the server's own tokenizer sees it, for a per-token rate.
    token_count = None
    try:
        import sys

        sys.path.insert(0, "src/RimWorldCodeRag/python")
        from transformers import AutoTokenizer

        tokenizer = AutoTokenizer.from_pretrained(health["model_path"])
        token_count = len(tokenizer(texts[0])["input_ids"])
        print(f"tokens per chunk (server tokenizer): {token_count}")
    except Exception as exc:  # noqa: BLE001
        print(f"(token count unavailable: {exc})")

    rates: List[float] = []
    for run in range(args.repeat):
        started = time.perf_counter()
        response = requests.post(
            f"{args.url}/embed", json={"mode": args.mode, "items": items}, timeout=1800
        )
        response.raise_for_status()
        elapsed = time.perf_counter() - started
        vectors = response.json()["vectors"]
        if len(vectors) != len(items):
            print(f"FAIL: server returned {len(vectors)} vectors for {len(items)} items")
            return 1
        rate = len(items) / elapsed
        rates.append(rate)
        line = f"run {run + 1}: {len(items)} chunks in {elapsed:.1f}s -> {rate:.1f} chunks/s"
        if token_count:
            line += f", {token_count * rate:.0f} tokens/s"
        print(line)

    best = max(rates)
    print()
    print(f"best: {best:.1f} chunks/s")
    for total in (144_732, 167_213):
        hours = total / best / 3600
        print(f"  ETA for {total:,} chunks: {hours:.1f} h")
    print(f"median: {statistics.median(rates):.1f} chunks/s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
