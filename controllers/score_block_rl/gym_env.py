#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Gymnasium environment for the MuJoCo SCORE_BLOCK privileged-state pilot."""

from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path
from typing import Any, Optional

import gymnasium as gym
import numpy as np


def resolve_dotnet_executable(override: str | None = None) -> str:
    executable = shutil.which(override or "dotnet")
    if executable:
        return executable
    if override:
        return str(Path(override).expanduser().resolve())
    return str(Path.home() / "AppData/Local/Temp/robot-simulator-dotnet-sdk/dotnet.exe")


class ScoreBlockEnv(gym.Env):
    """Wrap the persistent ``rl-env`` JSONL process as a Gymnasium Env.

    The eleven-value observation includes simulator ground-truth block coordinates;
    this is privileged training state and is not a real-robot sensor policy.
    """

    metadata = {"render_modes": []}

    def __init__(self, dotnet_exe: str, cli_dll: str, scenario_path: str,
                 duration: float = 120.0, seed_pool: Optional[list[int]] = None,
                 max_policy_ticks: int = 2400):
        super().__init__()
        if seed_pool is not None and not seed_pool:
            raise ValueError("seed_pool must contain at least one seed")
        self._dotnet = str(dotnet_exe)
        self._cli = str(cli_dll)
        self._scenario = str(scenario_path)
        self._duration = float(duration)
        self._seed_pool = list(seed_pool) if seed_pool is not None else [42]
        self._pool_idx = 0
        self._max_policy_ticks = int(max_policy_ticks)
        if self._duration <= 0 or self._max_policy_ticks <= 0:
            raise ValueError("duration and max_policy_ticks must be positive")
        self.action_space = gym.spaces.Box(low=-1.0, high=1.0, shape=(2,), dtype=np.float32)
        self.observation_space = gym.spaces.Box(low=-1.0, high=1.0, shape=(11,), dtype=np.float32)
        self._proc = subprocess.Popen(
            [self._dotnet, self._cli, "rl-env", "--scenario", self._scenario,
             "--duration", str(self._duration)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            # CLI stdout is the JSONL protocol. Inherit stderr so diagnostics remain visible.
            stderr=None, text=True, encoding="utf-8", errors="replace",
            bufsize=1, cwd=self._repo_root())
        self._last_info: dict[str, Any] = {}
        self._closed = False

    @staticmethod
    def _repo_root() -> str:
        return str(Path(__file__).resolve().parents[2])

    def _send(self, payload: dict[str, Any]) -> dict[str, Any]:
        if self._closed:
            raise RuntimeError("rl-env bridge is closed")
        if self._proc.stdin is None or self._proc.stdout is None:
            raise RuntimeError("rl-env bridge pipes are unavailable")
        try:
            self._proc.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
            self._proc.stdin.flush()
            line = self._proc.stdout.readline()
        except (BrokenPipeError, OSError, ValueError) as exc:
            raise RuntimeError(f"rl-env bridge I/O failed: {exc}") from exc
        if not line:
            code = self._proc.poll()
            raise RuntimeError(f"rl-env bridge exited without a response (exit code {code})")
        try:
            reply = json.loads(line)
        except json.JSONDecodeError as exc:
            raise RuntimeError(f"rl-env returned invalid JSONL: {exc}") from exc
        if not isinstance(reply, dict):
            raise RuntimeError("rl-env response must be a JSON object")
        if reply.get("type") == "error":
            raise RuntimeError(f"rl-env error: {reply.get('message', 'unspecified error')}")
        return reply

    def _observation(self, reply: dict[str, Any]) -> np.ndarray:
        try:
            obs = np.asarray(reply["obs"], dtype=np.float32)
        except (KeyError, TypeError, ValueError) as exc:
            raise RuntimeError("rl-env response is missing a valid observation") from exc
        if obs.shape != self.observation_space.shape or not np.isfinite(obs).all():
            raise RuntimeError(f"rl-env observation must be 11 finite values, got shape {obs.shape}")
        return obs

    @staticmethod
    def _info(reply: dict[str, Any]) -> dict[str, Any]:
        info = reply.get("info", {})
        if not isinstance(info, dict):
            raise RuntimeError("rl-env response info must be an object")
        return info

    def _next_seed(self) -> int:
        seed = self._seed_pool[self._pool_idx % len(self._seed_pool)]
        self._pool_idx += 1
        return seed

    def reset(self, *, seed: Optional[int] = None, options: Optional[dict] = None):
        super().reset(seed=seed)
        chosen = int(seed) if seed is not None else self._next_seed()
        reply = self._send({"op": "reset", "seed": chosen})
        if reply.get("type") != "reset":
            raise RuntimeError(f"expected reset response, got {reply.get('type')!r}")
        obs = self._observation(reply)
        self._last_info = self._info(reply)
        return obs, {"seed": chosen, **self._last_info}

    def step(self, action):
        action_array = np.asarray(action, dtype=np.float32)
        if action_array.shape != (2,) or not np.isfinite(action_array).all():
            raise ValueError("action must contain two finite values")
        v = float(np.clip(action_array[0], -1.0, 1.0))
        w = float(np.clip(action_array[1], -1.0, 1.0)) * 2.0
        reply = self._send({"op": "step", "v": v, "w": w})
        if reply.get("type") != "step":
            raise RuntimeError(f"expected step response, got {reply.get('type')!r}")
        try:
            reward = float(reply["reward"])
            terminated = reply["terminated"]
            truncated = reply["truncated"]
        except (KeyError, TypeError, ValueError) as exc:
            raise RuntimeError("rl-env step response is incomplete") from exc
        if not np.isfinite(reward) or not isinstance(terminated, bool) or not isinstance(truncated, bool):
            raise RuntimeError("rl-env returned invalid reward or termination flags")
        self._last_info = self._info(reply)
        return self._observation(reply), reward, terminated, truncated, self._last_info

    def close(self):
        if self._closed:
            return
        self._closed = True
        try:
            if self._proc.poll() is None and self._proc.stdin is not None:
                self._proc.stdin.write('{"op":"close"}\n')
                self._proc.stdin.flush()
                self._proc.wait(timeout=30)
        except (BrokenPipeError, OSError, ValueError, subprocess.TimeoutExpired):
            if self._proc.poll() is None:
                self._proc.terminate()
                try:
                    self._proc.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self._proc.kill()
                    self._proc.wait()
        finally:
            for stream in (self._proc.stdin, self._proc.stdout):
                if stream is not None:
                    stream.close()

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, traceback):
        self.close()
