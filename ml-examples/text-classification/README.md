# AG News text classification (forge test repo for `text_classification`)

A fastText-style classifier (word embeddings, averaged, then a small MLP) on AG News: 4 topics (World, Sports, Business, Sci/Tech), a 12,000-headline train subset (3,000 per topic) and a fixed 500-headline val subset (125 per topic, from AG News' test split). Each sample is "title. description".

```bash
conda activate gatorhack-dubz            # or any env with requirements.txt + forge-sdk installed
cd ml-examples/text-classification
python data.py                           # first run downloads the AG News CSVs (~31 MB) into data/
python train.py --epochs 8
python -m forge.check .                  # adapter sanity check on 8 samples
```

**Epoch time:** about a second on a laptop CPU (8 epochs, startup included, in about 10 s).

**What's interesting in the results**
- **Overfitting:** val accuracy peaks around 82% by epoch 2 or 3 while train accuracy heads to ~100%; val loss rises from there, so the overfit alert fires around epoch 4.
- **Noisy labels:** 3% of the val labels (15 headlines) are flipped on purpose (`data.flip_labels`). They show up as confident mistakes where the model is actually right.
- **Confusions:** Business and Sci/Tech share a lot of vocabulary (companies, markets, tech firms), so they are the usual mix-up.

**In the headset:** the image panels show each headline as a text card (the first words, from the atlas `forge` builds for text datasets), and the detail cards show the whole text. The layout is the image one (`hub/ml/archetypes.py`): stack, model, training, results with the dot wall and mistakes gallery.

**Relabel / remove:** same as CIFAR-10: the hub writes `.forge/labels_override.json`; `data.py` applies it on the next run. Sample ids come from the row in the AG News CSV, so they stay stable.

**`.forge/`** is hand-written here (`profile.json`, `adapter.py`), so it opens without an LLM; `POST /ml/project/rescan` classifies and adapts it from scratch. Lines in `train.py` marked `# forge` are the patch.
