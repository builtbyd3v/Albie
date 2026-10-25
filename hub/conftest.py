"""Defaults for every test suite under hub/ (pytest.ini: cad/tests, tests, vscode/tests).

A test that builds the real hub app (main.create_app) must never start the screen bridge: that would run
`npm start` in screen-bridge/, relink the VS Code extension and open Chrome. Tests that want the supervisor
construct ScreenBridgeService themselves with a stand-in command (tests/test_screens.py).
"""
import os

os.environ.setdefault("SCREEN_BRIDGE", "0")
# A developer's hub/.env may say VOICE_PROVIDER=grok; tests must not open a Grok session (load_env never overrides).
os.environ.setdefault("VOICE_PROVIDER", "none")
# The proactive watcher and the vscode tools call the Forge Bit extension on :8770: without this, a test run opened
# its temp rocket.py files in the developer's real VS Code. Port 9 refuses; vscode/tests point BASE at a fake.
os.environ.setdefault("BIT_VSCODE_PORT", "9")
