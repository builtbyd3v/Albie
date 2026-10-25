"""GitHub import: the CIFAR-10 preset fast path, and a shallow clone for any other public repo.

`inspect_repository` reads four files through the Contents API, statically, to spot the preset CIFAR-10 +
SmallCNN layout; a match trains the checked-in ``ml-examples/cifar10`` copy (and says so). Any other public repo
is `clone_repository`'d (shallow, size-capped, symlinks checked out as plain files) into its own folder under
`import_root()`, then goes through the same cold-open setup as a local repo (ML_PLAN 6): its code runs only in
`forge.check` and, after the patch is accepted, in its own training command.
"""

from __future__ import annotations

import ast
import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urlparse
from urllib.request import Request, urlopen


API = "https://api.github.com"
MAX_FILE_BYTES = 256_000
MAX_RESPONSE_BYTES = 2_000_000
NAME_RE = re.compile(r"^[A-Za-z0-9_.-]+$")
SUPPORTED_FORMAT = "cifar10-smallcnn-v1"
SUPPORTED_FILES = ("train.py", "model.py", "data.py", "requirements.txt")
CLONE_FORMAT = "cold-open"
MAX_REPO_KB = 200_000      # GitHub's own size estimate, checked before cloning
MAX_CHECKOUT_BYTES = 300_000_000  # the files actually checked out, checked after
CLONE_TIMEOUT_S = 180


class GitHubImportError(ValueError):
    """The URL cannot be read or does not match a supported repository layout."""


def _parse_url(url: str) -> tuple[str, str, str | None, str]:
    parsed = urlparse((url or "").strip())
    if parsed.scheme != "https" or parsed.hostname not in ("github.com", "www.github.com"):
        raise GitHubImportError("Paste a public https://github.com/owner/repository URL.")
    if parsed.username or parsed.password or parsed.port or parsed.query or parsed.fragment:
        raise GitHubImportError("Use a plain public repository URL without credentials or query parameters.")
    parts = [part for part in parsed.path.split("/") if part]
    if len(parts) == 2:
        owner, repo = parts
        ref = None
        directory = ""
    elif len(parts) == 4 and parts[2] == "tree":
        owner, repo, _, ref = parts
        directory = ""
    elif len(parts) > 4 and parts[2] == "tree":
        owner, repo, _, ref = parts[:4]
        directory = "/".join(parts[4:])
    else:
        raise GitHubImportError("Use a repository URL or a GitHub folder URL ending in /tree/<ref>/<folder>.")
    repo = repo.removesuffix(".git")
    if (not NAME_RE.fullmatch(owner) or not NAME_RE.fullmatch(repo) or
            (ref and not NAME_RE.fullmatch(ref)) or
            any(not NAME_RE.fullmatch(part) or part in (".", "..") for part in directory.split("/") if part)):
        raise GitHubImportError("The repository URL contains an unsupported owner, name, or branch.")
    return owner, repo, ref, directory


def _get_json(path: str, *, ref: str | None = None) -> object:
    url = API + path
    if ref:
        url += "?" + urlencode({"ref": ref})
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "Forge-ML-Hackathon-Importer",
    }
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = Request(url, headers=headers)
    try:
        with urlopen(request, timeout=4) as response:
            data = response.read(MAX_RESPONSE_BYTES + 1)
            if len(data) > MAX_RESPONSE_BYTES:
                raise GitHubImportError("GitHub returned a response larger than the importer limit.")
            return json.loads(data)
    except HTTPError as exc:
        if exc.code == 404:
            raise GitHubImportError(
                "Repository or branch not found, or not visible to the configured GitHub token. "
                "Check the owner/repository, ref, and public visibility."
            ) from exc
        if exc.code == 403:
            raise GitHubImportError("GitHub API rate limit reached; try again later or configure GITHUB_TOKEN.") from exc
        raise GitHubImportError(f"GitHub API returned HTTP {exc.code}.") from exc
    except (URLError, TimeoutError, OSError, json.JSONDecodeError) as exc:
        raise GitHubImportError(f"Could not read the public GitHub repository ({type(exc).__name__}).") from exc


def _read_file(owner: str, repo: str, directory: str, path: str, ref: str) -> tuple[str, str]:
    remote_path = "/".join(part for part in (directory, path) if part)
    response = _get_json(f"/repos/{quote(owner)}/{quote(repo)}/contents/{quote(remote_path)}", ref=ref)
    if not isinstance(response, dict) or response.get("type") != "file":
        raise GitHubImportError(f"{path} is not a regular file in the selected GitHub folder.")
    size = response.get("size", 0)
    if not isinstance(size, int) or size > MAX_FILE_BYTES:
        raise GitHubImportError(f"{path} is larger than the {MAX_FILE_BYTES // 1000} KB import limit.")
    if response.get("encoding") != "base64" or not isinstance(response.get("content"), str):
        raise GitHubImportError(f"GitHub did not return readable text for {path}.")
    try:
        data = base64.b64decode(response["content"], validate=False)
        return data.decode("utf-8"), str(response.get("sha", ""))
    except (ValueError, UnicodeDecodeError) as exc:
        raise GitHubImportError(f"{path} is not valid UTF-8 source text.") from exc


def _class_is_torch_module(tree: ast.AST, class_name: str) -> bool:
    for node in ast.walk(tree):
        if not isinstance(node, ast.ClassDef) or node.name != class_name:
            continue
        for base in node.bases:
            if isinstance(base, ast.Attribute) and base.attr == "Module":
                return True
            if isinstance(base, ast.Name) and base.id == "Module":
                return True
    return False


def _has_function(tree: ast.AST, name: str) -> bool:
    return any(isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == name
               for node in ast.walk(tree))


def inspect_repository(url: str) -> dict:
    """Inspect GitHub Contents for the Forge CIFAR-10/SmallCNN layout without executing repo code."""
    owner, repo, requested_ref, directory = _parse_url(url)
    metadata = _get_json(f"/repos/{quote(owner)}/{quote(repo)}")
    if not isinstance(metadata, dict) or metadata.get("private"):
        raise GitHubImportError("Only public GitHub repositories are supported.")
    ref = requested_ref or str(metadata.get("default_branch") or "main")
    root_path = f"/{quote(directory)}" if directory else ""
    root = _get_json(f"/repos/{quote(owner)}/{quote(repo)}/contents{root_path}", ref=ref)
    if not isinstance(root, list):
        raise GitHubImportError("GitHub did not return the selected repository folder.")
    root_files = {item.get("name") for item in root if isinstance(item, dict) and item.get("type") == "file"}
    present = [name for name in SUPPORTED_FILES if name in root_files]
    missing = [name for name in SUPPORTED_FILES if name not in root_files]
    result = {
        "import_id": hashlib.sha256(f"{owner}/{repo}@{ref}/{directory}".encode()).hexdigest()[:12],
        "repository": f"{owner}/{repo}", "url": url.strip(), "ref": ref,
        "supported": False, "format": None, "model": None, "dataset": None,
        "files": present, "reason": "Missing supported files: " + ", ".join(missing) if missing else None,
    }
    if missing:
        return result

    contents: dict[str, str] = {}
    hashes: dict[str, str] = {}
    with ThreadPoolExecutor(max_workers=len(SUPPORTED_FILES)) as pool:
        fetched = pool.map(lambda path: _read_file(owner, repo, directory, path, ref), SUPPORTED_FILES)
        for path, (source, sha) in zip(SUPPORTED_FILES, fetched):
            contents[path], hashes[path] = source, sha
    try:
        train = ast.parse(contents["train.py"])
        model = ast.parse(contents["model.py"])
        data = ast.parse(contents["data.py"])
        requirements = {
            re.split(r"[<=>!~\[]", line.split("#", 1)[0].strip().lower(), maxsplit=1)[0].strip().replace("_", "-")
            for line in contents["requirements.txt"].splitlines()
        }
    except SyntaxError as exc:
        result["reason"] = f"Python syntax error in the supported files at line {exc.lineno}."
        return result

    uses_supported_model = _class_is_torch_module(model, "SmallCNN")
    uses_supported_data = "cifar10" in contents["data.py"].lower() and _has_function(data, "get_loaders")
    uses_supported_train = "SmallCNN" in contents["train.py"] and "get_loaders" in contents["train.py"]
    has_torch = "torch" in requirements
    has_torchvision = "torchvision" in requirements
    if not (uses_supported_model and uses_supported_data and uses_supported_train and has_torch and has_torchvision):
        result["reason"] = ("Files were found, but this is not the supported CIFAR-10 + SmallCNN format. "
                             "Expected model.py:SmallCNN(torch.nn.Module), data.py:get_loaders using CIFAR-10, "
                             "train.py importing both, and torch + torchvision requirements.")
        return result

    result.update(supported=True, format=SUPPORTED_FORMAT, model="SmallCNN", dataset="CIFAR-10",
                  reason=None, files=list(SUPPORTED_FILES), file_shas=hashes)
    return result


# --- any other public repo: a shallow clone for the cold-open setup ---------------------------------------------

def import_root() -> Path:
    """Where clones live: FORGE_ML_IMPORT_DIR, else <system temp>/forge-ml-imports (outside every repo here)."""
    return Path(os.environ.get("FORGE_ML_IMPORT_DIR") or Path(tempfile.gettempdir()) / "forge-ml-imports").resolve()


def _git(args: list[str], cwd: Path | None = None) -> None:
    """git over https only, no prompts, no credential helpers, no LFS downloads, symlinks as plain files."""
    env = {**os.environ, "GIT_TERMINAL_PROMPT": "0", "GIT_LFS_SKIP_SMUDGE": "1", "GIT_CONFIG_NOSYSTEM": "1"}
    cmd = ["git", "-c", "core.symlinks=false", "-c", "credential.helper=", "-c", "protocol.allow=never",
           "-c", "protocol.https.allow=always", *args]
    try:
        proc = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, timeout=CLONE_TIMEOUT_S)
    except FileNotFoundError as exc:
        raise GitHubImportError("git isn't installed on the hub's laptop, so the repository can't be cloned.") from exc
    except subprocess.TimeoutExpired as exc:
        raise GitHubImportError(f"Cloning took longer than {CLONE_TIMEOUT_S} s; try a smaller repository or folder.") from exc
    if proc.returncode != 0:
        lines = [line for line in proc.stderr.strip().splitlines() if line.strip()]
        raise GitHubImportError("Could not clone the repository: " + (lines[-1] if lines else "git failed"))


def _check_checkout(root: Path) -> None:
    """Refuse a checkout that is too big, links outside itself, or has no Python to train."""
    total, python_files = 0, 0
    for folder, dirs, files in os.walk(root, followlinks=False):
        dirs[:] = [d for d in dirs if d != ".git"]
        for name in dirs + files:
            path = Path(folder) / name
            if path.is_symlink() and not path.resolve().is_relative_to(root.resolve()):
                raise GitHubImportError(f"The repository has a link pointing outside itself ({path.relative_to(root)}); "
                                        "it wasn't imported.")
        for name in files:
            path = Path(folder) / name
            if not path.is_symlink():
                total += path.stat().st_size
            python_files += name.endswith(".py")
        if total > MAX_CHECKOUT_BYTES:
            raise GitHubImportError(f"The checked-out files are over {MAX_CHECKOUT_BYTES // 1_000_000} MB; "
                                    "pick a smaller repository or one folder of it.")
    if python_files == 0:
        raise GitHubImportError("The repository (or folder) has no Python files, so there is nothing to train.")


def clone_repository(url: str) -> dict:
    """Shallow-clone a public repo (only the selected folder for a /tree/<ref>/<folder> URL) into its own folder
    under `import_root()` and return {import_id, repository, url, ref, path}. A folder cloned before is reused, so
    its `.forge/` setup is too ("look again" on the panel redoes the setup; delete the folder for a fresh copy)."""
    owner, repo, requested_ref, directory = _parse_url(url)
    metadata = _get_json(f"/repos/{quote(owner)}/{quote(repo)}")
    if not isinstance(metadata, dict) or metadata.get("private"):
        raise GitHubImportError("Only public GitHub repositories are supported.")
    size_kb = metadata.get("size") if isinstance(metadata.get("size"), int) else 0
    if not directory and size_kb > MAX_REPO_KB:  # a folder is fetched on its own (sparse), so only its size counts
        raise GitHubImportError(f"{owner}/{repo} is about {size_kb // 1000} MB, over the {MAX_REPO_KB // 1000} MB "
                                "import limit; import one folder of it (a /tree/<branch>/<folder> URL).")
    ref = requested_ref or str(metadata.get("default_branch") or "main")
    import_id = hashlib.sha256(f"{owner}/{repo}@{ref}/{directory}".encode()).hexdigest()[:12]
    root = import_root()
    dest = root / f"{owner}__{repo}__{import_id}"
    if not (dest / ".git").is_dir():
        root.mkdir(parents=True, exist_ok=True)
        partial = dest.with_name(dest.name + ".partial")
        shutil.rmtree(partial, ignore_errors=True)
        args = ["clone", "--depth", "1", "--single-branch", "--no-tags", "--branch", ref]
        if directory:
            args += ["--filter=blob:none", "--sparse"]
        try:
            _git(args + [f"https://github.com/{owner}/{repo}.git", str(partial)])
            if directory:
                _git(["sparse-checkout", "set", "--", directory], cwd=partial)
            _check_checkout(partial)
        except GitHubImportError:
            shutil.rmtree(partial, ignore_errors=True)
            raise
        shutil.rmtree(dest, ignore_errors=True)
        partial.rename(dest)
    project = (dest / directory).resolve() if directory else dest
    if not project.is_relative_to(dest.resolve()) or not project.is_dir():
        raise GitHubImportError(f"There is no folder {directory!r} on {ref} in {owner}/{repo}.")
    return {"import_id": import_id, "repository": f"{owner}/{repo}", "url": url.strip(), "ref": ref,
            "path": str(project)}
