"""BirkNext embedded offline translator (helper process).

Started by the BirkNext backend with the bundled Python runtime; never opens a port. One JSON object per line on stdin, one answer per line on
stdout (UTF-8):

  {"op": "probe"}                                   -> {"ok": true, "engine": ..., "model": ..., "sample": "..."}
  {"op": "translate", "texts": ["..."], "beam": 4}  -> {"ok": true, "texts": ["..."]}
  anything wrong                                     -> {"ok": false, "error": "..."}

Each text is split into lines and sentences (a list marker such as "1. " or "- " stays outside the translation); empty lines are kept. Placeholders
such as <x0> are opaque to this helper: the backend maps identifiers and glossary terms to them and checks that each comes back exactly once.

The bundled runtime has no socket or SSL module, so this process cannot open a network connection even by mistake.
"""
import json
import os
import re
import sys

# CTranslate2's own CPU kernels instead of Intel MKL: ~125 MB private memory instead of ~950 MB (see OfflineTranslationEngine). Set before import.
os.environ.setdefault("CT2_USE_MKL", "0")

PROTOCOL = 1
MAX_TEXTS = 200
MAX_CHARS = 20000
SAMPLE = "Open the page, fill in all required fields and select Save."

_END = re.compile(r"[.!?:;…]['\")\]]?$")
_SENTENCE = re.compile(r"(?<=[.!?])\s+(?=[A-Z\"'(<])")
_MARKER = re.compile(r"^(\s*(?:[-*•]|\d{1,3}[.)])\s+)")


def _answer(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _segments(text):
    """Yields (prefix, sentence, suffix) for every translatable piece of a text, and the text's layout to rebuild it."""
    layout = []
    pieces = []
    for line in text.split("\n"):
        if not line.strip():
            layout.append(("raw", line))
            continue
        marker = _MARKER.match(line)
        prefix = marker.group(1) if marker else ""
        body = line[len(prefix):]
        trailing = body[len(body.rstrip()):]
        body = body.rstrip()
        sentences = [s for s in _SENTENCE.split(body) if s.strip()]
        layout.append(("line", prefix, len(pieces), len(sentences), trailing))
        pieces.extend(sentences)
    return layout, pieces


def _rebuild(layout, translated):
    lines = []
    for item in layout:
        if item[0] == "raw":
            lines.append(item[1])
        else:
            _, prefix, start, count, trailing = item
            lines.append(prefix + " ".join(t.strip() for t in translated[start:start + count]) + trailing)
    return "\n".join(lines)


def main():
    args = dict(zip(sys.argv[1::2], sys.argv[2::2]))
    model_dir, spm_path = args.get("--model"), args.get("--spm")
    threads = max(1, min(8, int(args.get("--threads", "2"))))
    try:
        import ctranslate2
        import sentencepiece
        sp = sentencepiece.SentencePieceProcessor(model_file=spm_path)
        translator = ctranslate2.Translator(model_dir, device="cpu", compute_type="int8", inter_threads=1, intra_threads=threads)
    except Exception as e:  # the backend shows this as "offline translation unavailable"; nothing is fetched to repair it
        _answer({"ok": False, "ready": False, "error": f"The offline translation model could not be loaded: {type(e).__name__}: {e}"})
        return 3
    engine = {"name": "CTranslate2", "version": ctranslate2.__version__, "tokenizer": "SentencePiece " + getattr(sentencepiece, "__version__", "?"),
              "protocol": PROTOCOL, "threads": threads, "pid": os.getpid()}
    _answer({"ok": True, "ready": True, "engine": engine})

    def translate(texts, beam):
        layouts, batch = [], []
        for t in texts:
            layout, pieces = _segments(t)
            layouts.append((layout, len(batch), len(pieces)))
            batch.extend(pieces)
        out = []
        if batch:
            # A title or fragment without closing punctuation is translated as a sentence (the model reads a bare "Duplicate message" as a
            # headline and guesses: "Uformell melding"; "Duplicate message." gives "Duplisert melding."); the added full stop is removed again.
            added = [not _END.search(s.strip()) for s in batch]
            source = [s.strip() + "." if a else s for s, a in zip(batch, added)]
            results = translator.translate_batch([sp.encode(s, out_type=str) for s in source], beam_size=beam, max_input_length=512,
                                                 max_decoding_length=512, max_batch_size=16)
            out = [sp.decode(r.hypotheses[0]) for r in results]
            out = [o.rstrip()[:-1].rstrip() if a and o.rstrip().endswith(".") else o for o, a in zip(out, added)]
        return [_rebuild(layout, out[start:start + count]) for layout, start, count in layouts]

    for line in sys.stdin:
        line = line.lstrip("﻿").strip()  # a pipe from Windows PowerShell starts with a byte-order mark
        if not line:
            continue
        try:
            request = json.loads(line)
            op = request.get("op")
            if op == "probe":
                _answer({"ok": True, "engine": engine, "sample": translate([SAMPLE], 2)[0]})
            elif op == "translate":
                texts = request.get("texts")
                if not isinstance(texts, list) or len(texts) > MAX_TEXTS or any(not isinstance(t, str) for t in texts) or sum(len(t) for t in texts) > MAX_CHARS:
                    _answer({"ok": False, "error": f"At most {MAX_TEXTS} texts and {MAX_CHARS} characters per request."})
                    continue
                beam = max(1, min(6, int(request.get("beam", 4))))
                _answer({"ok": True, "texts": translate(texts, beam)})
            elif op == "exit":
                _answer({"ok": True})
                return 0
            else:
                _answer({"ok": False, "error": "Unknown operation."})
        except Exception as e:
            _answer({"ok": False, "error": f"{type(e).__name__}: {e}"})
    return 0


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
