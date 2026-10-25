# Disclosure (draft for the Devpost submission)

- All project code was written during Gator Hack V (Fri Oct 23, 5:00 PM to Sun Oct 25, 5:00 PM ET). The first commit is Fri Oct 23, 6:20 PM ET.
- The idea and planning doc (PROJECT_CONTEXT.md) were written by the team. No pre-written project code was used.
- AI tools assisted with coding. They are listed below, as MLH rules require.
- Third-party libraries, APIs, and assets are listed below. Each is used under its own license.

## AI tools
- Claude Code (Anthropic): code generation, debugging, docs. [TEAM: add who used it and for what]
- Claude API via the Anthropic Python SDK (anthropic, MIT), at runtime: the ML desk's setup (hub/ml/setup/) and Albie's "what can I do better?" proposals (hub/ml/improve.py)
- [TEAM: add any others, e.g. Copilot, ChatGPT, Cursor]

## Engines, SDKs, and libraries
- Unity 6 (6000.6.7f1), URP
- Meta XR All-in-One SDK 207, OpenXR, Meta XR Simulator
- com.unity.webrtc (screen streaming), glTFast (runtime GLB loading)
- Python: FastAPI, build123d (CAD), mss + Pillow (JPEG screen fallback), matplotlib (CIFAR-10 example extra chart)
- SciPy (BSD-3): convex hulls for the humanoid design (hub/cad/parts/humanoid.py)
- Offline data prep only, not run by the hub (hub/cad/tools_offline/): trimesh (MIT) and fast-simplification (MIT) for the G1 meshes; MuJoCo (Apache-2.0), PyTorch (BSD-3), PyYAML (MIT) and matplotlib to record the G1 walk clip
- ML example (ml-examples/cifar10/, run in the user's own env, not the hub): PyTorch (BSD-3), torchvision (BSD-3), NumPy (BSD-3), Pillow (MIT-CMU), matplotlib (PSF-based). TensorBoard logs are read by our own decoder (hub/ml/logtail.py); TensorBoard itself is not a dependency
- [TEAM: add NativeWebSocket, Newtonsoft JSON, DOTween, XCharts, PyTorch, etc. once they are actually used]

## APIs and services
- TypeSafe Jev (repo-aware panel picking spike)
- macOS Messages, optional and off by default (`IMESSAGE_ENABLED=1`): the hub reads the local `~/Library/Messages/chat.db` read-only
- imsg CLI (github.com/steipete/imsg, Homebrew tap `steipete/tap/imsg`): sends confirmed iMessage drafts. Optional, never installed by the hub; the user installs it
- [TEAM: Grok Voice Agent API, WhatsApp Cloud API, cloudflared/ngrok, if used]

## Assets and data
- Unitree G1 robot description (URDF and meshes) from unitreerobotics/unitree_ros, BSD 3-Clause, in hub/cad/data/unitree_g1/ (license: see hub/cad/data/unitree_g1/LICENSE, details in NOTICE.md)
- Unitree G1 walking policy from unitreerobotics/unitree_rl_gym, BSD 3-Clause, used offline to record walk_clip.json (license: see hub/cad/data/unitree_g1/LICENSE-unitree_rl_gym)
- CIFAR-10 dataset (Krizhevsky, Nair and Hinton, 2009, University of Toronto), downloaded at run time by torchvision for the ML demo (ml-examples/cifar10/); not committed to the repo
- [TEAM: pet model source and license, e.g. Quaternius CC0]
