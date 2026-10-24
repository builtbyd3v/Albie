"""Read-only importer for the one supported public PyTorch repository format.

Repository files are fetched for static inspection only. They are never imported or executed; training uses
the checked-in CIFAR-10/SmallCNN implementation in ``ml-examples/cifar10``.
"""

from __future__ import annotations

import ast
import base64
import hashlib
import json
import os
import re
from concurrent.futures import ThreadPoolExecutor
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urlparse
from urllib.request import Request, urlopen


API = "https://api.github.com"
MAX_FILE_BYTES = 256_000
MAX_RESPONSE_BYTES = 2_000_000
NAME_RE = re.compile(r"^[A-Za-z0-9_.-]+$")
SUPPORTED_FORMAT = "cifar10-smallcnn-v1"
SUPPORTED_FILES = ("train.py", "model.py", "data.py", "requirements.txt")


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
            raise GitHubImportError("Repository or branch not found. Make sure it is public.") from exc
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
