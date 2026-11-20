"""Build-time only: converts the HPLT Marian model (.npz) to CTranslate2 int8.

Run by scripts/build-offline-translation.ps1 with the embeddable Python and the pinned wheels (ctranslate2 with its converters, numpy, PyYAML)
extracted into a build folder. Never shipped and never run on a tester's PC.

The model's SentencePiece vocabulary (.vocab: piece<TAB>score, in id order) is its Marian vocabulary, so it is read directly instead of a Marian
.yml vocabulary file.
"""
import sys


def main():
    build_lib, model, vocab, out = sys.argv[1:5]
    sys.path.insert(0, build_lib)
    import ctranslate2.converters.marian as marian

    def load_vocab(path):
        with open(path, encoding="utf-8") as f:
            return [line.rstrip("\n").split("\t")[0] for line in f if line.rstrip("\n")]

    marian.load_vocab = load_vocab
    marian.MarianConverter(model, [vocab]).convert(out, quantization="int8", force=True)
    print("converted", out)


if __name__ == "__main__":
    main()
