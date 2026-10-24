"""Defaults for every test suite under hub/ (pytest.ini: cad/tests, tests, vscode/tests).

A test that builds the real hub app (main.create_app) must never start the screen bridge: that would run
`npm start` in screen-bridge/, relink the VS Code extension and open Chrome. Tests that want the supervisor
construct ScreenBridgeService themselves with a stand-in command (tests/test_screens.py).
"""
import os

os.environ.setdefault("SCREEN_BRIDGE", "0")
# A developer's hub/.env may say VOICE_PROVIDER=grok; tests must not open a Grok session (load_env never overrides).
os.environ.setdefault("VOICE_PROVIDER", "none")
