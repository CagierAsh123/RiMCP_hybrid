#!/usr/bin/env python3
"""Cross-encoder reranker server (plan task 2.4).

Why not sentence-transformers: the official Qwen3-Reranker repos ship an ST **5.x** module layout
(``sentence_transformers.cross_encoder.modules.logit_score.LogitScore``), and this machine has
ST 3.4.1 — which cannot load them. Upgrading ST would risk the embedding server that the index
depends on, so the reranker is implemented directly against ``transformers`` using the official
Qwen3-Reranker recipe: build the judge prompt, read the last-position logits at the ``yes`` / ``no``
token ids, and return P(yes) as the score.

Endpoint contract::

    POST /rerank  {"query": "...", "documents": ["...", ...], "instruction": "..."}
    -> {"scores": [0.97, 0.03, ...], "model": "...", "device": "cuda"}

Scores are in [0, 1] and are NOT comparable across queries — only the ordering within one request
matters.
"""

from __future__ import annotations

import argparse
import logging
from typing import Any, Dict, List, Optional

from flask import Flask, jsonify, request

logging.basicConfig(level=logging.INFO, format="[rerank-server] %(message)s")
logger = logging.getLogger(__name__)

app = Flask(__name__)

DEFAULT_INSTRUCTION = "Given a web search query, retrieve relevant passages that answer the query"

# Fallback prompt, used only when the checkpoint has no chat template. The checkpoint's own template
# is authoritative and differs from this in two ways that measurably change the scores: the
# instruction comes from the *system* message, and `query` / `document` are separate roles. It also
# ends the assistant turn with an empty think block — omitting it depressed every score in testing
# (a relevant passage scored 0.14 instead of ~0.9).
PROMPT_SYSTEM = (
    "Judge whether the Document meets the requirements based on the Query and the Instruct provided. "
    'Note that the answer can only be "yes" or "no".'
)
PROMPT_PREFIX = f"<|im_start|>system\n{PROMPT_SYSTEM}<|im_end|>\n<|im_start|>user\n"
PROMPT_SUFFIX = "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n"

state: Dict[str, Any] = {
    "model": None,
    "tokenizer": None,
    "device": None,
    "dtype": None,
    "max_length": 2048,
    "true_token_id": None,
    "false_token_id": None,
    "model_path": None,
    "supports_num_logits_to_keep": True,
    "has_chat_template": False,
}


def _format_pair(instruction: str, query: str, document: str) -> str:
    """Build the judge prompt with the checkpoint's own chat template when it has one."""
    tokenizer = state["tokenizer"]
    if state["has_chat_template"]:
        messages = [
            {"role": "system", "content": instruction},
            {"role": "query", "content": query},
            {"role": "document", "content": document},
        ]
        try:
            return tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
        except Exception as exc:  # noqa: BLE001 - fall back rather than fail the request
            logger.warning("apply_chat_template failed (%s); using the built-in prompt", exc)
            state["has_chat_template"] = False

    return (
        f"{PROMPT_PREFIX}<Instruct>: {instruction}\n<Query>: {query}\n<Document>: {document}{PROMPT_SUFFIX}"
    )


def _score_batch(pairs: List[str]) -> List[float]:
    import torch

    tokenizer = state["tokenizer"]
    model = state["model"]
    device = state["device"]

    inputs = tokenizer(
        pairs,
        padding=True,
        truncation=True,
        max_length=state["max_length"],
        return_tensors="pt",
    ).to(device)

    # A 0.6B causal LM over a 151k vocabulary would materialise batch x seq x vocab logits
    # (several GB) if we let it; we only need the final position.
    kwargs = {}
    if state["supports_num_logits_to_keep"]:
        kwargs["num_logits_to_keep"] = 1

    with torch.no_grad():
        try:
            logits = model(**inputs, **kwargs).logits
        except TypeError:
            # Older/newer signature: fall back to full logits and slice.
            state["supports_num_logits_to_keep"] = False
            logits = model(**inputs).logits

    # Gather each row's LAST REAL position rather than blindly indexing -1: with right padding the
    # shorter sequences would be scored at a pad token, which scrambles the ranking while still
    # returning plausible-looking probabilities.
    last_index = inputs["attention_mask"].sum(dim=1).clamp(min=1) - 1
    last_logits = logits[torch.arange(logits.shape[0], device=logits.device), last_index, :]

    false_logits = last_logits[:, state["false_token_id"]]
    true_logits = last_logits[:, state["true_token_id"]]
    scores = torch.softmax(torch.stack([false_logits, true_logits], dim=1), dim=1)[:, 1]
    return [float(s) for s in scores]


def _resolve_dtype(name: str, device: str):
    import torch

    if name == "float32":
        return torch.float32
    if name == "float16":
        return torch.float16
    if name == "bfloat16":
        return torch.bfloat16
    if device == "cuda":
        return torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
    return torch.float32


def _load(model_path: str, device_name: str, dtype_name: str, max_length: int) -> None:
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer

    device = torch.device(device_name)
    dtype = _resolve_dtype(dtype_name, device_name)

    tokenizer = AutoTokenizer.from_pretrained(model_path)
    # Decoder-only cross-encoders must pad on the LEFT: the score is read from the last position, and
    # right padding would make the shorter sequences end on a pad token.
    tokenizer.padding_side = "left"
    has_chat_template = bool(getattr(tokenizer, "chat_template", None))
    model = AutoModelForCausalLM.from_pretrained(model_path, torch_dtype=dtype)
    model.to(device)
    model.eval()

    true_id = tokenizer.convert_tokens_to_ids("yes")
    false_id = tokenizer.convert_tokens_to_ids("no")
    if true_id is None or false_id is None:
        raise SystemExit("tokenizer has no 'yes'/'no' tokens; not a Qwen3-Reranker checkpoint")

    state.update(
        model=model,
        tokenizer=tokenizer,
        device=device,
        dtype=dtype,
        max_length=max_length,
        true_token_id=true_id,
        false_token_id=false_id,
        model_path=model_path,
        has_chat_template=has_chat_template,
    )
    logger.info(
        "loaded %s on %s (%s); yes=%s no=%s max_length=%s chat_template=%s",
        model_path, device, dtype, true_id, false_id, max_length, has_chat_template,
    )


@app.route("/health", methods=["GET"])
def health():
    return jsonify(
        {
            "status": "healthy",
            "model_loaded": state["model"] is not None,
            "device": str(state["device"]),
            "dtype": str(state["dtype"]),
            "max_length": state["max_length"],
            "model_path": state["model_path"],
            "true_token_id": state["true_token_id"],
            "false_token_id": state["false_token_id"],
            "chat_template": state["has_chat_template"],
        }
    )


@app.route("/rerank", methods=["POST"])
def rerank():
    try:
        payload = request.get_json(silent=True)
        if not payload:
            return jsonify({"error": "No JSON payload"}), 400

        query = payload.get("query") or ""
        documents = payload.get("documents") or []
        if not isinstance(documents, list):
            return jsonify({"error": "documents must be a list"}), 400
        if not documents:
            return jsonify({"scores": []})

        instruction = payload.get("instruction") or DEFAULT_INSTRUCTION
        batch_size = max(1, int(payload.get("batch_size") or 8))

        pairs = [_format_pair(instruction, query, str(doc)) for doc in documents]

        scores: List[float] = []
        for i in range(0, len(pairs), batch_size):
            scores.extend(_score_batch(pairs[i : i + batch_size]))

        return jsonify({"scores": scores, "model": state["model_path"], "count": len(scores)})

    except Exception as exc:  # noqa: BLE001 - surfaced to the caller as a 500
        logger.error("Error processing request: %s", exc, exc_info=True)
        return jsonify({"error": str(exc)}), 500


def main() -> None:
    parser = argparse.ArgumentParser(description="Cross-encoder reranker server")
    parser.add_argument("--model", required=True, help="Model directory")
    parser.add_argument("--port", type=int, default=5002)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--device", default="auto", help="auto | cpu | cuda")
    parser.add_argument("--dtype", choices=["auto", "float32", "float16", "bfloat16"], default="auto")
    parser.add_argument("--max-length", type=int, default=2048)
    args = parser.parse_args()

    import torch

    device = args.device
    if device == "auto":
        device = "cuda" if torch.cuda.is_available() else "cpu"

    logger.info("Loading reranker from %s", args.model)
    _load(args.model, device, args.dtype, args.max_length)
    logger.info("Serving on %s:%s", args.host, args.port)
    app.run(host=args.host, port=args.port, threaded=False)


if __name__ == "__main__":
    main()
