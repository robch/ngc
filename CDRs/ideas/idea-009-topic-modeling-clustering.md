# Idea 009: Topic Modeling / Clustering

## The question it answers
"What are the *themes* in this pile of documents?"

## What it is
Statistical or embedding-based methods for discovering latent topics/themes
across a document set, rather than just ranking individual phrases:
- **LDA** (Latent Dirichlet Allocation) and **NMF** (Non-negative Matrix
  Factorization) — classic bag-of-words topic modeling (gensim, scikit-learn).
- **Embedding-based clustering** (e.g. **BERTopic**) — clusters documents/
  passages using dense vector embeddings, often producing more coherent,
  human-readable topics than classic LDA on modern text.

## Why it matters
N-grams alone answer "what phrases are common" only crudely as a proxy for
"what is this corpus about" — you can eyeball a top-n-gram list and infer
themes, but topic modeling does that inference directly and can surface
groupings that no single phrase would reveal (e.g. a topic defined by a mix
of loosely-related vocabulary rather than one repeated phrase).

## Why it's a bigger lift
Unlike most other ideas here, this is not a small feature to bolt onto
existing counting infrastructure — it requires either a statistical modeling
approach (LDA/NMF) or embedding models (BERTopic-style), i.e. real algorithmic
weight, and possibly (for embeddings) a vendored model artifact. Worth naming
as the natural "next tier up" from frequency counting, but deserving its own
design discussion before any implementation commitment.

## Possible ngc extension (porting strategy)
- **LDA/NMF core** — genuinely portable as a from-scratch C# implementation:
  these are classic unsupervised ML algorithms *trained fresh on whatever
  corpus you give them* every run, with no pretrained weights to source at
  all. This fits `ngc`'s existing "stateless, corpus-in / stats-out"
  philosophy well, and is the natural first step for this idea.
- **BERTopic-style embedding clustering** — a bigger lift: requires a
  pretrained embedding model consumed via ONNX Runtime (same pattern as
  ideas 005/006), plus our own clustering logic (e.g. a from-scratch or
  ported HDBSCAN/k-means implementation) written around it. Worth deferring
  until the ONNX-consumption pattern is proven out on 005/006 first.

## Licensing / porting notes
- **LDA**: implement from the original published algorithm (Blei, Ng, and
  Jordan's 2003 paper) rather than porting gensim's source line-by-line —
  **gensim is LGPL** ([piskvorky/gensim](https://github.com/piskvorky/gensim)),
  and a rewritten/ported derivative of its code would not cleanly fall under
  LGPL's dynamic-linking exception, so treat it as a porting source to avoid.
  The LDA *algorithm itself* is public academic knowledge, not gensim's IP, so
  implementing it from the paper is fine.
- **NMF**: use **scikit-learn (BSD)** —
  [scikit-learn/scikit-learn](https://github.com/scikit-learn/scikit-learn) —
  as the porting reference instead of gensim — fully permissive, safe to port
  from directly.
- **BERTopic**: MIT-licensed —
  [MaartenGr/BERTopic](https://github.com/MaartenGr/BERTopic) — safe to port
  clustering/topic-extraction logic from directly; any underlying embedding
  model would still need its own license check per model (most common ones —
  e.g. sentence-transformers models — are Apache/MIT, but verify per specific
  model chosen).

See [idea-999-reference-repos.md](idea-999-reference-repos.md) for the full
repo/license lookup table.
