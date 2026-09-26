#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Shared helpers for reading SCORE_BLOCK PPO training artifacts.

The checkpoint layout is produced by ``stable_baselines3``'s
``CheckpointCallback`` (``save_path``/``name_prefix``), so the filename encodes
the *global* ``model.num_timesteps`` at save time. With the single-environment
setup used by this pilot that equals the number of single-environment steps,
which lets ``run-config.json`` cross-check filename steps against the registry.
"""

from __future__ import annotations

import csv
import hashlib
import importlib.metadata
import json
import re
from pathlib import Path
from typing import Any

FINAL_MODEL_STEM = "ppo_score_block"
FINAL_MODEL_NAME = f"{FINAL_MODEL_STEM}.zip"
CHECKPOINT_DIR_NAME = "checkpoints"
CHECKPOINT_NAME_PREFIX = "rl_model"
PROGRESS_CSV_NAME = "progress.csv"
MONITOR_CSV_NAME = "episodes.monitor.csv"
RUN_CONFIG_NAME = "run-config.json"

CHECKPOINT_PATTERN = re.compile(rf"^{CHECKPOINT_NAME_PREFIX}_(\d+)_steps\.zip$")


def sha256_file(path: Path | str | None) -> str | None:
    """SHA-256 of a file, or ``None`` when it does not exist."""
    if path is None:
        return None
    candidate = Path(path)
    if not candidate.is_file():
        return None
    digest = hashlib.sha256()
    with candidate.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "not-installed"


def parse_checkpoint_steps(filename: str) -> int | None:
    """Training steps encoded in a CheckpointCallback filename, else ``None``."""
    match = CHECKPOINT_PATTERN.match(Path(filename).name)
    return int(match.group(1)) if match else None


def assert_unique_checkpoint_steps(rows: list[dict[str, Any]]) -> None:
    """Invariant: one checkpoint per training-step count.

    ``CheckpointCallback`` encodes ``num_timesteps`` in the filename, so with the
    current naming scheme this cannot collide - the helper exists so the
    invariant is explicit (and unit-testable) rather than implicit, and so a
    future name-prefix change cannot silently make the registry ambiguous.
    """
    seen: dict[int, str] = {}
    for row in rows:
        steps = int(row["training_steps"])
        if steps in seen:
            raise ValueError(
                f"duplicate checkpoint step count {steps}: {seen[steps]} and {row['filename']}")
        seen[steps] = row["filename"]


def discover_checkpoints(directory: Path | str) -> list[dict[str, Any]]:
    """Return checkpoint metadata sorted by training steps.

    Files whose basename matches the ``glob`` but not the exact
    ``rl_model_<steps>_steps.zip`` pattern are ignored.
    """
    folder = Path(directory)
    if not folder.is_dir():
        return []
    rows: list[dict[str, Any]] = []
    for path in sorted(folder.glob(f"{CHECKPOINT_NAME_PREFIX}_*_steps.zip")):
        steps = parse_checkpoint_steps(path.name)
        if steps is None:
            continue
        rows.append({
            "path": str(path.resolve()),
            "filename": path.name,
            "training_steps": steps,
            "sha256": sha256_file(path),
        })
    rows.sort(key=lambda row: row["training_steps"])
    assert_unique_checkpoint_steps(rows)
    return rows


def load_json(path: Path | str) -> dict[str, Any]:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def read_progress_csv(path: Path | str) -> dict[str, Any]:
    """Parse the SB3 CSV logger output (plain header row, no comment prefix)."""
    candidate = Path(path)
    if not candidate.is_file():
        raise FileNotFoundError(f"progress CSV not found: {candidate}")
    with candidate.open("r", encoding="utf-8", newline="") as stream:
        reader = csv.DictReader(stream)
        header = list(reader.fieldnames or [])
        rows = [dict(row) for row in reader]
    return {"path": str(candidate.resolve()), "header": header, "rows": rows,
            "row_count": len(rows)}


def read_monitor_csv(path: Path | str) -> dict[str, Any]:
    """Parse the SB3 ``Monitor`` episode CSV (first line is ``#`` JSON metadata)."""
    candidate = Path(path)
    if not candidate.is_file():
        raise FileNotFoundError(f"Monitor CSV not found: {candidate}")
    metadata: dict[str, Any] = {}
    with candidate.open("r", encoding="utf-8", newline="") as stream:
        first = stream.readline()
        if first.startswith("#"):
            try:
                metadata = json.loads(first[1:].strip() or "{}")
            except json.JSONDecodeError:
                metadata = {"raw": first.strip()}
        else:
            stream.seek(0)
        reader = csv.DictReader(stream)
        header = list(reader.fieldnames or [])
        rows = [dict(row) for row in reader]
    return {"path": str(candidate.resolve()), "metadata": metadata, "header": header,
            "rows": rows, "row_count": len(rows)}


def float_column_stats(rows: list[dict[str, Any]], column: str) -> dict[str, Any]:
    """Count how many rows hold a finite float in ``column``."""
    parsed: list[float] = []
    blanks = 0
    invalid = 0
    for row in rows:
        raw = row.get(column)
        if raw is None or raw == "":
            blanks += 1
            continue
        try:
            value = float(raw)
        except (TypeError, ValueError):
            invalid += 1
            continue
        if value != value or value in (float("inf"), float("-inf")):
            invalid += 1
            continue
        parsed.append(value)
    return {
        "column": column,
        "present": column in (rows[0].keys() if rows else []),
        "parsed": len(parsed),
        "blank": blanks,
        "invalid": invalid,
        "min": min(parsed) if parsed else None,
        "max": max(parsed) if parsed else None,
    }


def audit_checkpoints(run_config: dict[str, Any], out_dir: Path | str) -> dict[str, Any]:
    """Cross-check the ``run-config.json`` checkpoint registry against disk."""
    out = Path(out_dir)
    registered = run_config.get("checkpoints") or []
    on_disk = discover_checkpoints(out / CHECKPOINT_DIR_NAME)
    registered_steps = sorted(int(row.get("training_steps", -1)) for row in registered)
    disk_steps = sorted(int(row["training_steps"]) for row in on_disk)
    issues: list[str] = []
    if registered_steps != disk_steps:
        issues.append(
            f"checkpoint step sets differ: run-config={registered_steps} disk={disk_steps}")
    by_steps = {int(row["training_steps"]): row for row in on_disk}
    for row in registered:
        steps = int(row.get("training_steps", -1))
        disk_row = by_steps.get(steps)
        if disk_row is None:
            issues.append(f"registered checkpoint {steps} steps is missing on disk")
            continue
        if row.get("sha256") != disk_row["sha256"]:
            issues.append(
                f"checkpoint {steps} steps sha256 mismatch: "
                f"run-config={row.get('sha256')} disk={disk_row['sha256']}")
        if row.get("filename") not in (None, disk_row["filename"]):
            issues.append(
                f"checkpoint {steps} steps filename mismatch: "
                f"run-config={row.get('filename')} disk={disk_row['filename']}")
    final_model = out / FINAL_MODEL_NAME
    if run_config.get("model_zip_sha256") != sha256_file(final_model):
        issues.append(
            "final model sha256 mismatch: "
            f"run-config={run_config.get('model_zip_sha256')} disk={sha256_file(final_model)}")
    return {
        "ok": not issues,
        "issues": issues,
        "registered_checkpoints": len(registered),
        "checkpoints_on_disk": len(on_disk),
        "final_model": str(final_model.resolve()),
        "final_model_sha256": sha256_file(final_model),
        "progress_csv_rows": (read_progress_csv(out / PROGRESS_CSV_NAME)["row_count"]
                              if (out / PROGRESS_CSV_NAME).is_file() else None),
        "monitor_csv_rows": (read_monitor_csv(out / MONITOR_CSV_NAME)["row_count"]
                             if (out / MONITOR_CSV_NAME).is_file() else None),
    }
