# CIFAR-10 (forge test repo #0 and stage backup)

Small CNN (3 conv blocks) on CIFAR-10. 10,000-image train subset by default (`--full` for all 50,000), fixed 1,000-image val subset (the one shown in XR). ML_PLAN 4.3.

```bash
conda activate gatorhack-dubz            # or any env with requirements.txt + forge-sdk installed
cd ml-examples/cifar10
python data.py                           # first run downloads CIFAR-10 (~170 MB) into data/, writes noise.json
python train.py --epochs 10
python -m forge.check .                  # adapter sanity check on 8 samples
```

**Epoch time:** about 2 s on an M-series Mac (MPS) with the 10k subset; 20 epochs take under a minute.

**What's interesting in the results**
- **Noisy labels:** 3% of labels are flipped on purpose (1,500 train, exactly 30 val), listed in `noise.json`. They show up as confident mistakes where the model is actually right: "the model was right, the data was wrong."
- **Overfitting:** with only 10k images and no augmentation, val accuracy peaks around 60% near epoch 9 while train accuracy heads to ~100%. Run `--epochs 20`: the overfit alert fires around epoch 11 (val loss zig-zags before that, so 10 epochs may not trigger it).
- **Weak classes:** cat and dog are the usual worst classes, and mostly confused with each other.

**Relabel / remove:** the hub writes `.forge/labels_override.json` (`{"relabel": {"val_0007": 3}, "remove": ["train_01234"]}`); `data.py` applies it on the next run. Sample ids come from the CIFAR index, so they stay stable.

**`.forge/`** is hand-written here (`profile.json`, `adapter.py`); for other repos the setup step writes it. Lines in `train.py` marked `# forge` are the patch.
