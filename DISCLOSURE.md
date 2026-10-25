# Disclosure (draft for the Devpost submission)

- All project code was written during Gator Hack V (Fri Oct 23, 5:00 PM to Sun Oct 25, 5:00 PM ET). The first commit is Fri Oct 23, 6:20 PM ET.
- The idea and planning doc (PROJECT_CONTEXT.md) were written by the team. No pre-written project code was used.
- AI tools assisted with coding. They are listed below, as MLH rules require.
- Third-party libraries, APIs, and assets are listed below. Each is used under its own license.

## AI tools
- Claude Code (Anthropic; Claude Opus 5.5 and Claude Fable 5.1): used by Dev, Hudson, Adrian and Satyabrata for code generation, debugging, tests and docs. Many commits carry a Claude Co-Authored-By trailer. [TEAM: Nandani, confirm]
- Claude API via the Anthropic Python SDK (anthropic, MIT), at runtime: the ML desk's setup (hub/ml/setup/) and Albie's "what can I do better?" proposals (hub/ml/improve.py)
- Claude Agent SDK (claude-agent-sdk, MIT), at runtime: Albie's coding and Q&A runner and the crew gators (hub/agent/runner.py, crew.py)
- Grok Voice Agent API (xAI, grok-voice-3.0), at runtime: Albie's speech-to-speech voice and transcription (hub/agent/voice.py)
- [TEAM: add any others you used, e.g. Copilot, ChatGPT, Cursor, Codex; delete this line if none]

## Engines, SDKs, and libraries
- Unity 6 (6000.6.7f1), URP
- Meta XR All-in-One SDK 207, OpenXR, Meta XR Simulator
- com.unity.webrtc (screen streaming), glTFast (runtime GLB loading)
- Python: FastAPI, build123d (CAD), mss + Pillow (JPEG screen fallback), matplotlib (CIFAR-10 example extra chart)
- Python hub (hub/requirements.txt): uvicorn (BSD-3), pydantic (MIT), watchfiles (MIT), RocketPy (MIT, flight sim), PyYAML (MIT), python-dotenv (BSD-3), websockets (BSD-3), aiohttp (Apache-2.0), certifi (MPL-2.0); tests: pytest (MIT), httpx (BSD-3)
- Slack: Bolt for Python (slack-bolt, MIT) and the Python Slack SDK (slack-sdk, MIT); the Slack CLI for app setup
- Unity packages: Meta Interaction SDK (part of Meta XR All-in-One, Meta license), Input System, Newtonsoft Json (com.unity.nuget.newtonsoft-json), TextMesh Pro
- cad-vision (github.com/BAAA-Hacks/cad-vision, MIT), copied unchanged into unity/Assets/_Forge/CAD/ThirdParty/CadVision/
- Screen bridge: playwright-core (Apache-2.0) drives Google Chrome to share the VS Code window over WebRTC
- Neural Net Lab: our own C# re-implementation of the TensorFlow Playground idea (datasets, features, init); no Playground code is copied
- Blender: the gator's opening mouth and our own animation clips (art/gator/, scripts build_gator_*.py and export_gator_fbx.py)
- faster-whisper (MIT), optional and not installed by default: local speech-to-text when STT_PROVIDER=whisper
- SciPy (BSD-3): convex hulls for the humanoid design (hub/cad/parts/humanoid.py)
- Offline data prep only, not run by the hub (hub/cad/tools_offline/): trimesh (MIT) and fast-simplification (MIT) for the G1 meshes; MuJoCo (Apache-2.0), PyTorch (BSD-3), PyYAML (MIT) and matplotlib to record the G1 walk clip
- ML example (ml-examples/cifar10/, run in the user's own env, not the hub): PyTorch (BSD-3), torchvision (BSD-3), NumPy (BSD-3), Pillow (MIT-CMU), matplotlib (PSF-based). TensorBoard logs are read by our own decoder (hub/ml/logtail.py); TensorBoard itself is not a dependency
- ML example (ml-examples/text-classification/, run in the user's own env, not the hub): PyTorch (BSD-3), NumPy (BSD-3), Pillow (MIT-CMU); text cards use Pillow's bundled default font

## APIs and services
- TypeSafe Jev (repo-aware panel picking spike)
- GitHub REST API through the GitHub CLI (`gh api`, MIT): Albie's ml_github finds a repo by name or search (hub/ml/github.py). Repos it clones are not committed and keep their own licenses
- macOS Messages, optional and off by default (`IMESSAGE_ENABLED=1`): the hub reads the local `~/Library/Messages/chat.db` read-only
- imsg CLI (github.com/steipete/imsg, Homebrew tap `steipete/tap/imsg`): sends confirmed iMessage drafts. Optional, never installed by the hub; the user installs it
- GitHub REST API (api.github.com, optional GITHUB_TOKEN): the ML desk's GitHub import reads a repo's metadata and files, then shallow-clones a public repo over https into the system temp folder (hub/ml/github_import.py). Clones are not committed and keep their own licenses

## Assets and data
- Unitree G1 robot description (URDF and meshes) from unitreerobotics/unitree_ros, BSD 3-Clause, in hub/cad/data/unitree_g1/ (license: see hub/cad/data/unitree_g1/LICENSE, details in NOTICE.md)
- Unitree G1 walking policy from unitreerobotics/unitree_rl_gym, BSD 3-Clause, used offline to record walk_clip.json (license: see hub/cad/data/unitree_g1/LICENSE-unitree_rl_gym)
- CIFAR-10 dataset (Krizhevsky, Nair and Hinton, 2009, University of Toronto), downloaded at run time by torchvision for the ML demo (ml-examples/cifar10/); not committed to the repo
- Estes C6 motor thrust curve (RASP .eng file made from NAR published certification data, 2000), hub/cad/data/estes_c6.eng: the rocket's flight sim
- Fonts: Fredoka (SIL OFL 1.1, unity/Assets/_Forge/Pet/ThirdParty/Fredoka/), Inter (SIL OFL 1.1, unity/Assets/_Forge/Core/Fonts/), Liberation Sans (SIL OFL, TextMesh Pro default)
- Gator model and animations: Quirky Series Ultimate Pack Vol.1 alligator (Unity Asset Store), in unity/Assets/_Forge/Pet/ThirdParty/Quirky Series/. [TEAM: confirm its license allows a public repo]
- AG News topic dataset (Zhang, Zhao and LeCun, 2015, from AG's corpus), CSVs downloaded at run time from github.com/mhjabreel/CharCnn_Keras for ml-examples/text-classification/; not committed
- "PyTorch MNIST" import preset: github.com/pytorch/examples (BSD-3) and the MNIST dataset (LeCun, Cortes and Burges), downloaded at run time by that repo; not committed
