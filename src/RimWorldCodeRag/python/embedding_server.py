#!/usr/bin/env python3
"""Persistent embedding server.

Two backends, chosen automatically from the model directory:

* **sentence-transformers** (preferred). The model repo ships its own ST config
  (``modules.json``, ``1_Pooling/config.json``, ``config_sentence_transformers.json``),
  so pooling, normalization and the query instruction all come *from the model* rather than
  being hard-coded here. This matters for Qwen3-Embedding: it needs **last-token** pooling and
  an instruction prefix on the **query side only** — reusing e5's ``query:`` / ``passage:``
  prefixes with mean pooling silently wrecks it.

* **transformers** (legacy fallback). e5-style mean pooling plus the ``query:`` / ``passage:``
  prefixes, so an index built with e5 stays searchable.

The HTTP contract is unchanged: ``POST /embed {"mode": "query"|"passage", "items": [...]}``.
"""

from __future__ import annotations

import argparse
import logging
from typing import Any, Dict, List, Optional

from flask import Flask, jsonify, request

logging.basicConfig(level=logging.INFO, format="[embedding-server] %(message)s")
logger = logging.getLogger(__name__)

app = Flask(__name__)

# Loaded once at startup.
state: Dict[str, Any] = {
    "backend": None,        # "sentence-transformers" | "transformers"
    "model": None,          # SentenceTransformer or AutoModel
    "tokenizer": None,      # only for the legacy backend
    "prompts": {},          # ST prompt templates, e.g. {"query": "Instruct: ...\nQuery:"}
    "device": None,
    "dim": 0,
    "max_length": 0,
    "st_batch_size": 8,
    "st_token_budget": 12000,
    "dtype": None,
    "model_path": None,
}


def _normalize_fp32(encoded):
    """L2-normalize in float32 so ``|v| == 1`` to float precision, independent of the model dtype."""
    import numpy as np

    array = np.asarray(encoded, dtype=np.float32)
    norms = np.linalg.norm(array, axis=1, keepdims=True)
    norms[norms == 0] = 1.0
    return (array / norms).tolist()


def _text_of(item: Dict[str, Any]) -> str:
    text = (item.get("text") or "").strip()
    if not text:
        text = (item.get("preview") or "").strip()
    return text


def _prepare_legacy(item: Dict[str, Any], mode: str) -> str:
    """e5 convention: every input carries a `query:` / `passage:` prefix."""
    text = _text_of(item)
    return f"{mode}: {text}" if text else f"{mode}: "


def _budgeted_batches(texts: List[str], max_batch: int, token_budget: int):
    """Split texts into forward-pass batches bounded by *both* count and total length.

    Padding is per batch, so a batch's activation memory scales with
    ``len(batch) x max_length_in_batch`` (and attention with an extra factor of the length).
    A fixed batch size therefore OOMs as soon as one batch happens to contain long XML defs.
    Bounding the token total keeps memory flat regardless of the length distribution, while
    preserving input order.
    """
    batch: List[str] = []
    batch_max = 0
    for text in texts:
        estimate = max(1, len(text) // 3)  # ~3 chars per token for code/XML
        if batch and (len(batch) >= max_batch or max(batch_max, estimate) * (len(batch) + 1) > token_budget):
            yield batch
            batch, batch_max = [], 0
        batch.append(text)
        batch_max = max(batch_max, estimate)
    if batch:
        yield batch


def _encode_batch(items: List[Dict[str, Any]], mode: str) -> List[List[float]]:
    if not items:
        return []

    backend = state["backend"]

    if backend == "sentence-transformers":
        model = state["model"]
        texts = [_text_of(item) for item in items]

        # Only apply the instruction when the model actually ships one AND we are embedding a
        # query. Documents must stay prompt-free (the Qwen3 config sets `document: ""`).
        prompt_name: Optional[str] = None
        if mode == "query" and "query" in state["prompts"]:
            prompt_name = "query"

        vectors: List[List[float]] = []
        for batch in _budgeted_batches(texts, state["st_batch_size"], state["st_token_budget"]):
            encoded = model.encode(
                batch,
                prompt_name=prompt_name,
                batch_size=len(batch),
                # Normalize ourselves in float32 below. Letting sentence-transformers normalize in
                # bfloat16 leaves the vectors only approximately unit length (measured |v| = 1.0012),
                # which biases every dot product by ~0.1%.
                normalize_embeddings=False,
                show_progress_bar=False,
                convert_to_numpy=True,
            )
            vectors.extend(_normalize_fp32(encoded))
        return vectors

    # Legacy transformers path.
    import torch

    tokenizer = state["tokenizer"]
    model = state["model"]
    device = state["device"]

    texts = [_prepare_legacy(item, mode) for item in items]
    encoded = tokenizer(
        texts,
        padding=True,
        truncation=True,
        max_length=state["max_length"],
        return_tensors="pt",
    )
    encoded = {key: value.to(device) for key, value in encoded.items()}

    with torch.no_grad():
        outputs = model(**encoded)
        embeddings = outputs.last_hidden_state.mean(dim=1)

    embeddings = torch.nn.functional.normalize(embeddings, p=2, dim=1)
    return embeddings.cpu().tolist()


@app.route("/health", methods=["GET"])
def health():
    return jsonify(
        {
            "status": "healthy",
            "device": str(state["device"]),
            "model_loaded": state["model"] is not None,
            "backend": state["backend"],
            "model_path": state["model_path"],
            "dim": state["dim"],
            "max_length": state["max_length"],
            "dtype": str(state["dtype"]),
            "prompts": sorted(state["prompts"].keys()),
        }
    )


@app.route("/embed", methods=["POST"])
def embed():
    try:
        payload = request.get_json(silent=True)
        if not payload:
            return jsonify({"error": "No JSON payload"}), 400

        items = payload.get("items", [])
        if not isinstance(items, list):
            return jsonify({"error": "items must be a list"}), 400

        mode = payload.get("mode", "passage")
        if mode not in ("passage", "query"):
            return jsonify({"error": f"Invalid mode '{mode}'"}), 400

        if not items:
            return jsonify({"vectors": []})

        # The caller already batches (indexing uses ~1024 items per request); chunk it anyway so a
        # long request cannot blow up VRAM.
        batch_size = 256
        all_vectors: List[List[float]] = []
        for i in range(0, len(items), batch_size):
            all_vectors.extend(_encode_batch(items[i : i + batch_size], mode))

        return jsonify({"vectors": all_vectors, "dim": state["dim"], "mode": mode})

    except Exception as exc:  # noqa: BLE001 - surfaced to the caller as a 500
        logger.error("Error processing request: %s", exc, exc_info=True)
        return jsonify({"error": str(exc)}), 500


def _resolve_dtype(name: str, device: str):
    """Pick the compute dtype.

    Loading Qwen3-Embedding without an explicit dtype gives **float32** (2.4 GB of weights for a
    0.6B model) even though the checkpoint is bfloat16 — measured on an 8 GB laptop GPU that
    filled VRAM, made batch 16 *slower* than batch 8, and crashed at batch 32. bfloat16 halves
    both weights and activations and is the model's native precision.
    """
    import torch

    if name == "float32":
        return torch.float32
    if name == "float16":
        return torch.float16
    if name == "bfloat16":
        return torch.bfloat16

    # auto
    if device == "cuda":
        return torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
    return torch.float32


def _load_sentence_transformers(model_path: str, device: str, max_length: int, dtype_name: str) -> bool:
    try:
        from sentence_transformers import SentenceTransformer
    except ImportError:
        logger.warning("sentence-transformers is not installed; using the legacy transformers backend.")
        return False

    dtype = _resolve_dtype(dtype_name, device)
    try:
        model = SentenceTransformer(
            model_path,
            device=device,
            trust_remote_code=True,
            model_kwargs={"torch_dtype": dtype},
        )
    except Exception as exc:  # noqa: BLE001
        logger.warning("Could not load '%s' as a SentenceTransformer (%s); falling back.", model_path, exc)
        return False

    prompts = dict(getattr(model, "prompts", None) or {})
    if max_length > 0:
        model.max_seq_length = max_length

    state.update(
        backend="sentence-transformers",
        model=model,
        tokenizer=None,
        prompts=prompts,
        dim=model.get_sentence_embedding_dimension(),
        max_length=model.max_seq_length,
        dtype=dtype,
    )
    logger.info("sentence-transformers backend: dim=%s max_seq_length=%s dtype=%s prompts=%s",
                state["dim"], state["max_length"], dtype, sorted(prompts.keys()))
    return True


def _load_transformers(model_path: str, device: str, max_length: int) -> None:
    import torch
    from transformers import AutoModel, AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(model_path)
    model = AutoModel.from_pretrained(model_path)
    dev = torch.device(device)
    model.to(dev)
    model.eval()

    hidden = getattr(model.config, "hidden_size", 0)
    state.update(
        backend="transformers",
        model=model,
        tokenizer=tokenizer,
        prompts={},
        device=dev,
        dim=hidden,
        max_length=max_length,
    )
    logger.info("legacy transformers backend: dim=%s max_length=%s (e5-style prefixes)",
                state["dim"], state["max_length"])


def main() -> None:
    parser = argparse.ArgumentParser(description="Embedding server")
    parser.add_argument("--model", required=True, help="Model directory")
    parser.add_argument("--max-length", type=int, default=0,
                        help="Max sequence length; 0 = use the model's own limit")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5000)
    parser.add_argument("--backend", choices=["auto", "sentence-transformers", "transformers"],
                        default="auto")
    parser.add_argument("--st-batch-size", type=int, default=8,
                        help="Forward-pass batch size for the sentence-transformers backend. "
                             "Must stay small: memory scales with batch x seq_len^2.")
    parser.add_argument("--st-token-budget", type=int, default=12000,
                        help="Upper bound on batch_size x longest_sequence for one forward pass. "
                             "Keeps VRAM flat when long XML defs land in the same batch.")
    parser.add_argument("--dtype", choices=["auto", "float32", "float16", "bfloat16"], default="auto",
                        help="Compute dtype. 'auto' picks bfloat16 on a GPU that supports it; "
                             "loading without this gives float32 and roughly halves throughput.")
    args = parser.parse_args()

    state["st_batch_size"] = max(1, args.st_batch_size)
    state["st_token_budget"] = max(256, args.st_token_budget)

    import torch

    device = "cuda" if torch.cuda.is_available() else "cpu"
    state["device"] = device
    state["model_path"] = args.model

    logger.info("Loading model from %s", args.model)
    loaded = False
    if args.backend in ("auto", "sentence-transformers"):
        loaded = _load_sentence_transformers(args.model, device, args.max_length, args.dtype)
    if not loaded:
        if args.backend == "sentence-transformers":
            raise SystemExit("--backend sentence-transformers requested but loading failed")
        _load_transformers(args.model, device, args.max_length or 512)

    logger.info("Model ready on %s; serving on %s:%s", device, args.host, args.port)
    app.run(host=args.host, port=args.port, threaded=True)


if __name__ == "__main__":
    main()
