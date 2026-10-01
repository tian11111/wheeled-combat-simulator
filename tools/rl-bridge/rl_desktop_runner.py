#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""SCORE_BLOCK RL 策略外部控制器进程（桌面 / CLI JSONL stdio 桥的 Python 端）。

角色
----
本进程是 ``--controller-us "<命令>"`` 指向的外部控制器，由
``src/Sim.Controller/ExternalControllerBridge.cs:40-103`` 拉起：stdin 逐行收观测
JSON，stdout 逐行回动作。它**不带仿真、不做预推进、不重算观测**：

* SCORE_BLOCK 入口预推进、目标锁定、11 维观测投影全部由 C# 侧唯一构造
  （``src/Sim.Cli/RlEnvCommand.cs:241-253`` 预推进、``:406-429`` 目标锁定、
  ``:481-512`` 与 ``:514-529`` 11 维投影；批 1 起改由
  ``Sim.Hosting.ScoreBlockExhibition`` 承载、rl-env 委托）。展演 runner/driver 把
  该帧注入观测的加性字段 ``rlObservation``（design 决策③）。本进程只消费，
  任何"从 robot/objects 重算 11 维"的做法都是被禁止的第二实现。
* Python 侧只做两件事，且都 *import 训练侧现有实现*（不复制）：
  ``controllers/score_block_rl/gym_env.py:123-130``（解析 + 11 维/有限校验，
  失败 RuntimeError）与 ``:162-168``（动作映射 ``v=clip(a0,-1,1)``、
  ``w=clip(a1,-1,1)*2.0``）。
* 推理：``PPO.load(checkpoint, device="cpu")`` +
  ``predict(..., deterministic=True)``（与 ``controllers/score_block_rl/evaluate.py:141``
  同口径）；维度门与 ``evaluate.py:387-393`` 同话术（旧 9 维模型必须重训）。
  进程内禁用采样与 RNG：不 import ``random``/``np.random``，CPU 单线程。

契约（逐帧一比一）
------------------
stdin 一行 → stdout **恰一行**，先写后 flush。``ExternalControllerBridge.Decide``
（``ExternalControllerBridge.cs:62-103``）逐帧同步读取；缺行会让后续帧读到过期动作、
多行会占缓冲，故响应数必须与输入行数恒等。stdout **只有**动作 JSONL；诊断、运行
身份与 fault 计数一律 stderr。坏行不炸流：不合法/维度不符的帧回零动作并计 fault，
进程继续服务。

观测载体（按优先级）::

    {"requestId": 12, "rlObservation": [11 个数], ...}   # 展演路径（SCORE_BLOCK runner/driver 填充）
    {"type": "reset"|"step", "obs": [11 个数], ...}      # rl-env 响应兼容（同一 11 维契约，便于链路联调）

两者都没有（预推进期间或 ``ControllerPreflight`` 的残帧
``Observation{RequestId=1}``，见 ``godot/src/ControllerPreflight.cs:26-47``）
→ 立即零动作、**不计** fault。载体存在但维度/有限性不符 → 零动作 + fault。

退出码
------
``0`` stdin EOF / Ctrl-C / stdout 管道断裂（收尾后正常退出，永不自行超时退出）；
``2`` 命令行或 checkpoint 路径错误（零 stdout）；``3`` 模型/环境不可用，含
"维度不符必须重训"硬门（零 stdout，原因在 stderr）。

用法
----
``py -3.12 -X utf8 tools/rl-bridge/rl_desktop_runner.py --checkpoint <zip|目录>``
``py -3.12 tools/rl-bridge/rl_desktop_runner.py --stub``（协议链路自测，不需要 torch）
"""

from __future__ import annotations

import argparse
import contextlib
import errno
import importlib.util
import json
import os
import sys
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[1]
SCORE_BLOCK_RL_DIR = REPO_ROOT / "controllers" / "score_block_rl"

#: 协议维度契约：C# ``RlEnvCommand.cs:25-26``（BaseObservationSize=9 /
#: ObservationSize=11）与 Python 消费方 ``gym_env.py:84``
#: （Box(-1, 1, (11,), float32)）。11 个数本身只由 C# 构造，本进程不重算。
OBSERVATION_SIZE = 11
ACTION_SIZE = 2

#: 退出码（README.md 与 selftest.py 同源引用）。
EXIT_OK = 0
EXIT_USAGE = 2
EXIT_MODEL = 3

#: stderr 前缀，便于在桌面 HUD / CLI 日志里辨认本进程。
TAG = "[rl-runner]"


class CheckpointError(Exception):
    """checkpoint 路径不可用（命令行错误，退出码 2）。"""


class ModelError(Exception):
    """torch/SB3 不可用、加载失败或观测维度不符（退出码 3）。"""


class StdoutClosed(Exception):
    """stdout 管道已断开：收尾并退出 0（不把断管道当故障帧继续刷）。

    注意：Windows 上 flush 到已关闭的管道得到的是 ``OSError [Errno 22]``
    （ERROR_BROKEN_PIPE/ERROR_NO_DATA 的 errno 映射），**不是** ``BrokenPipeError``，
    只捕 BrokenPipeError 会让进程继续空转 —— 已由自测的真实断管道腿钉住。
    """


def _is_closed_pipe_error(exc: BaseException) -> bool:
    if isinstance(exc, BrokenPipeError):
        return True
    if isinstance(exc, ValueError):  # "I/O operation on closed file"
        return "closed" in str(exc)
    if isinstance(exc, OSError):
        if getattr(exc, "errno", None) in (errno.EPIPE, errno.EINVAL):
            return True
        if getattr(exc, "winerror", None) in (109, 232):  # BROKEN_PIPE / NO_DATA
            return True
    return False


def _detach_stdout() -> None:
    """把 stdout 换成 devnull，避免解释器退出时对断管道二次 flush 报错。"""
    try:
        devnull = os.open(os.devnull, os.O_WRONLY)
        os.dup2(devnull, sys.stdout.fileno())
    except (OSError, ValueError):
        pass


def _err(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def force_utf8_streams() -> None:
    """stdin/stdout 强制 UTF-8 且 stdout 不写 CRLF，与桥的管道契约一致。

    训练侧（``train.py:220-221`` 检查 ``sys.flags.utf8_mode``）与协议都要求 UTF-8；
    依赖调用方加 ``-X utf8`` 不够稳，这里显式重配置。stdin 用 ``errors="replace"``：
    编码坏字节应记一条坏行，而不是让进程抛 UnicodeDecodeError 退出。
    """
    settings = (
        (sys.stdin, {"encoding": "utf-8", "errors": "replace"}),
        (sys.stdout, {"encoding": "utf-8", "errors": "strict", "newline": "\n"}),
        (sys.stderr, {"encoding": "utf-8", "errors": "replace", "newline": "\n"}),
    )
    for stream, kwargs in settings:
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(**kwargs)
        except (ValueError, OSError):
            # 流已被重定向成不可重配置对象时保持系统默认，不因此退出。
            pass


def _load_module(name: str):
    """按文件路径加载 ``controllers/score_block_rl/<name>.py``。

    用路径加载而非 ``sys.path`` 注入，避免把训练目录的通用模块名
    （``gym_env``）暴露到全局；模块名带前缀保证不与他人冲突。
    """
    path = SCORE_BLOCK_RL_DIR / f"{name}.py"
    if not path.is_file():
        raise ModelError(f"{path} is missing (训练侧 Python 契约的唯一来源)")
    spec = importlib.util.spec_from_file_location(f"_score_block_rl_{name}", path)
    if spec is None or spec.loader is None:
        raise ModelError(f"cannot import {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class _ContractProbe:
    """让 ``gym_env.ScoreBlockEnv`` 的契约代码脱离仿真进程运行。

    ``_send`` / ``_unpack_step`` 是 ``ScoreBlockEnv`` 里唯一需要子进程的两处；
    替换成"记下发往 rl-env 的请求、原样交回"即可复用其解析与映射，而**不复制**
    任何公式（design 决策③：Python 侧重算一律禁止）。
    """

    def __init__(self, observation_space: Any):
        self.observation_space = observation_space
        self._closed = False
        self._outbound: dict[str, Any] = {}

    def _send(self, payload: dict[str, Any]) -> dict[str, Any]:
        self._outbound = payload
        return {}

    def _unpack_step(self, reply: Any) -> dict[str, Any]:
        return dict(self._outbound)


class ScoreBlockContract:
    """Python 侧契约唯一入口：解析/校验与动作映射都来自 score_block_rl。"""

    def __init__(self, gym_env, artifacts):
        self._gym_env = gym_env
        self._artifacts = artifacts
        self.observation_size = OBSERVATION_SIZE
        self.action_size = ACTION_SIZE
        box = gym_env.gym.spaces.Box(low=-1.0, high=1.0, shape=(OBSERVATION_SIZE,),
                                     dtype=gym_env.np.float32)
        self._probe = _ContractProbe(box)

    @classmethod
    def load(cls) -> "ScoreBlockContract":
        return cls(_load_module("gym_env"), _load_module("train_artifacts"))

    @property
    def numpy(self):
        return self._gym_env.np

    @property
    def artifacts(self):
        return self._artifacts

    def parse_observation(self, raw: Any):
        """``gym_env.py:123-130`` 原样复用：11 个有限值，否则 RuntimeError。"""
        return self._gym_env.ScoreBlockEnv._observation(self._probe, {"obs": raw})

    def action_to_vw(self, action: Any) -> tuple[float, float]:
        """``gym_env.py:162-168`` 原样复用：形状/有限校验 + clip 映射。"""
        frame = self._gym_env.ScoreBlockEnv.step(self._probe, action)
        return float(frame["v"]), float(frame["w"])


def resolve_checkpoint(raw_path: str, contract: ScoreBlockContract) -> tuple[Path, str]:
    """把 ``--checkpoint``（SB3 zip 或目录）解析成唯一 zip 路径。

    SB3 产物是 zip 归档，``policy.pth``/``policy.optimizer.pth``/
    ``pytorch_variables.pth`` 是**zip 内成员**，仓库内没有裸 ``policy.pth``
    （``research/facts.md`` §3）。传入 ``.pth`` 等非 zip 路径必须显式报错，
    不能当作"缺文件"含糊过去。
    """
    path = Path(raw_path).expanduser()
    if not path.exists() and not path.is_absolute():
        # 桌面/桥可能以别的 cwd 启动本进程（如 godot/）：相对路径先按仓库根再试一次，
        # 命中就显式说明来源，避免"我 shell 里能跑、Godot 里找不到模型"。
        candidate = REPO_ROOT / path
        if candidate.exists():
            _err(f"{TAG} --checkpoint {raw_path} not found under cwd "
                 f"({Path.cwd()}); using repo-root path {candidate}")
            path = candidate
    if path.is_file():
        if path.suffix.lower() != ".zip":
            raise CheckpointError(
                f"{path} is not a stable-baselines3 checkpoint archive. The SCORE_BLOCK PPO "
                "artifacts are SB3 .zip files (policy.pth / policy.optimizer.pth / "
                "pytorch_variables.pth live *inside* the zip; no bare policy.pth is written "
                "to disk). Pass e.g. "
                ".../seed-20260929/ppo_score_block.zip or the exhibition checkpoint "
                ".../seed-20260929/checkpoints/rl_model_204800_steps.zip.")
        return path, "file"
    if not path.is_dir():
        raise CheckpointError(
            f"{path} does not exist. Pass an SB3 checkpoint .zip or a run directory "
            "(<dir>/ppo_score_block.zip or <dir>/checkpoints/rl_model_*_steps.zip).")
    final = path / contract.artifacts.FINAL_MODEL_NAME
    if final.is_file():
        return final, "dir/final-model"
    rows = contract.artifacts.discover_checkpoints(path / contract.artifacts.CHECKPOINT_DIR_NAME)
    if rows:
        # 同一目录多个 checkpoint：取训练步数最大者，并在 stderr 显式说明（不静默）。
        return Path(rows[-1]["path"]), f"dir/checkpoints(max steps={rows[-1]['training_steps']})"
    zips = sorted(path.glob("*.zip"))
    if len(zips) == 1:
        return zips[0], "dir/sole-zip"
    if zips:
        raise CheckpointError(
            f"{path} contains {len(zips)} zips {[z.name for z in zips]}; pass the exact "
            "checkpoint file instead of the directory.")
    raise CheckpointError(
        f"{path} contains no {contract.artifacts.FINAL_MODEL_NAME} and no "
        f"{contract.artifacts.CHECKPOINT_DIR_NAME}/"
        f"{contract.artifacts.CHECKPOINT_NAME_PREFIX}_*_steps.zip.")


class Sb3Policy:
    """SB3 确定性策略（``evaluate.py:141`` 同口径：deterministic=True）。"""

    def __init__(self, model: Any, path: Path, sha256: str | None, numpy):
        self._model = model
        self._np = numpy
        self.path = path
        self.sha256 = sha256
        self.num_timesteps = int(model.num_timesteps)
        self.observation_shape = tuple(int(v) for v in model.observation_space.shape)
        self.action_shape = tuple(int(v) for v in model.action_space.shape)

    def act(self, observation):
        action, _ = self._model.predict(observation, deterministic=True)
        return self._np.asarray(action, dtype=self._np.float32)

    def identity_line(self) -> str:
        return (f"mode=real checkpoint={self.path} sha256={self.sha256} "
                f"num_timesteps={self.num_timesteps} observation={self.observation_shape} "
                f"action={self.action_shape} deterministic=True")


class StubPolicy:
    """合成确定性策略：只用于协议链路测试，不需要 torch/权重。

    刻意只用观测的前三项（相对方位/块坐标），保证同输入必同输出；动作仍走
    ``gym_env`` 的映射，不绕过契约。
    """

    def __init__(self, numpy):
        self._np = numpy

    def act(self, observation):
        a0 = float(self._np.clip(0.5 * observation[0] + 0.25 * observation[1], -1.0, 1.0))
        a1 = float(self._np.clip(-0.5 * observation[1] + 0.25 * observation[2], -1.0, 1.0))
        return self._np.array([a0, a1], dtype=self._np.float32)

    def identity_line(self) -> str:
        return "mode=stub policy=linear-deterministic (no torch, no checkpoint loaded)"


def load_sb3_policy(checkpoint: Path, contract: ScoreBlockContract) -> Sb3Policy:
    """加载 checkpoint 并执行维度硬门（零 stdout，失败即 ModelError）。"""
    try:
        import torch  # noqa: PLC0415 —— 仅真模式需要 torch
        from stable_baselines3 import PPO  # noqa: PLC0415
    except ImportError as exc:  # pragma: no cover - 取决于运行环境
        raise ModelError(
            f"torch/stable-baselines3 is unavailable in {sys.executable}: {exc}. "
            "Use --stub for protocol-link tests.") from exc
    # 确定性纪律（design 决策⑥）：CPU 单线程、无采样、无 RNG。
    torch.set_num_threads(1)
    try:
        # SB3 的 logger 偶尔直接 print；stdout 只允许动作 JSONL，一律改道 stderr。
        with contextlib.redirect_stdout(sys.stderr):
            model = PPO.load(str(checkpoint), device="cpu")
    except Exception as exc:  # SB3 用多种异常类型，这里统一转成启动失败
        raise ModelError(f"failed to load {checkpoint}: {type(exc).__name__}: {exc}") from exc

    shape = tuple(int(v) for v in getattr(model.observation_space, "shape", ()) or ())
    expected = (OBSERVATION_SIZE,)
    if shape != expected:
        raise ModelError(
            f"{checkpoint.name}: observation shape {shape} is incompatible; expected "
            f"{expected}. Models trained with the previous 9-value observation must be "
            "retrained (controllers/score_block_rl/evaluate.py:387-393, "
            ".trellis/spec/sim/index.md:50-51).")
    action_shape = tuple(int(v) for v in getattr(model.action_space, "shape", ()) or ())
    if action_shape != (ACTION_SIZE,):
        raise ModelError(
            f"{checkpoint.name}: action shape {action_shape} is incompatible; expected "
            f"{(ACTION_SIZE,)} (Box(-1, 1, (2,), float32), gym_env.py:83).")
    sha = contract.artifacts.sha256_file(checkpoint)
    return Sb3Policy(model, checkpoint, sha, contract.numpy)


def _carrier_of(frame: dict[str, Any]) -> tuple[str | None, Any]:
    """返回 (载体名, 原始数组)；无合法载体 → (None, None)。"""
    if "rlObservation" in frame:
        return "rlObservation", frame["rlObservation"]
    if frame.get("type") in ("reset", "step") and "obs" in frame:
        return "obs", frame["obs"]
    return None, None


def _echoable_request_id(frame: dict[str, Any]) -> Any:
    """requestId 原样回显；非数字/字符串（桥会拒整行）或非有限值按缺失处理。"""
    value = frame.get("requestId")
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return value if value == value and value not in (float("inf"), float("-inf")) else None
    if isinstance(value, str):
        return value
    return None


def _response(v: float, w: float, request_id: Any) -> dict[str, Any]:
    payload: dict[str, Any] = {"v": float(v), "w": float(w)}
    if request_id is not None:
        payload["requestId"] = request_id
    return payload


def _write_line(line: str) -> None:
    """一行一帧写到 stdout 并立即 flush（桥逐帧同步读取，缓冲会错位）。"""
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def _render_action(payload: dict[str, Any]) -> str:
    # allow_nan=False：绝不让 NaN/Inf 以非法 JSON 形式漏到桥上。
    return json.dumps(payload, ensure_ascii=True, separators=(",", ":"), allow_nan=False)


def serve(policy, contract: ScoreBlockContract) -> dict[str, int]:
    """逐帧服务，直到 stdin EOF。返回统计（json 行数恒等于输入行数）。"""
    stats = {"frames": 0, "answered": 0, "zero": 0, "faults": 0}
    pre_handoff_noted = False
    for raw in sys.stdin:
        stats["frames"] += 1
        index = stats["frames"]
        text = raw.strip()
        request_id: Any = None
        observation = None
        reason: str | None = None

        if not text:
            reason = "empty line"
        else:
            try:
                frame = json.loads(text)
            except ValueError as exc:  # JSONDecodeError ⊂ ValueError
                frame = None
                reason = f"bad json: {exc}"
            if isinstance(frame, dict):
                request_id = _echoable_request_id(frame)
                carrier, raw_obs = _carrier_of(frame)
                if carrier is None:
                    # 预检残帧 / 预推进期间的帧：合法帧，只是还没有 RL 观测。
                    reason = "no rlObservation (pre-handoff frame)"
                    stats["zero"] += 1
                else:
                    try:
                        observation = contract.parse_observation(raw_obs)
                    except Exception as exc:  # RuntimeError（gym_env 校验口径）
                        reason = f"invalid {carrier}: {exc}"
            elif frame is None:
                pass  # 上面已记因
            else:
                reason = f"frame must be a JSON object, got {type(frame).__name__}"

        if observation is not None:
            try:
                action = policy.act(observation)
                v, w = contract.action_to_vw(action)
            except Exception as exc:
                reason = f"policy failed: {type(exc).__name__}: {exc}"
                v, w = 0.0, 0.0
        else:
            v, w = 0.0, 0.0

        if reason is None:
            stats["answered"] += 1
        else:
            if reason.startswith("no rlObservation"):
                # 预检残帧/预推进期间的帧是正常时序，不是故障；只报第一次，避免刷屏。
                if not pre_handoff_noted:
                    pre_handoff_noted = True
                    _err(f"{TAG} frame={index} zero-action: {reason} "
                         f"(further pre-handoff frames counted, not logged)")
            else:
                stats["faults"] += 1
                _err(f"{TAG} frame={index} zero-action: {reason}")
        try:
            line = _render_action(_response(v, w, request_id))
        except ValueError as exc:  # 非 JSON 合法浮点（正常路径不可达，保险）
            stats["faults"] += 1
            _err(f"{TAG} frame={index} action serialization failed: {exc}")
            line = '{"v":0.0,"w":0.0}'
        try:
            _write_line(line)
        except Exception as exc:  # 写入失败不能炸流
            if _is_closed_pipe_error(exc):
                raise StdoutClosed(str(exc)) from exc
            stats["faults"] += 1
            _err(f"{TAG} frame={index} stdout write failed: {exc}")
    return stats


def main(argv: list[str] | None = None) -> int:
    force_utf8_streams()
    parser = argparse.ArgumentParser(
        prog="rl_desktop_runner.py",
        description="SCORE_BLOCK RL policy external controller (JSONL stdio); "
                    "consumes the C#-built 11-value rlObservation, never re-derives it.",
    )
    parser.add_argument("--checkpoint", metavar="PATH",
                        help="SB3 checkpoint .zip, or a directory containing "
                             "ppo_score_block.zip / checkpoints/rl_model_*_steps.zip. "
                             "Required in real mode.")
    parser.add_argument("--stub", action="store_true",
                        help="synthetic deterministic policy for protocol-link tests "
                             "(no torch/SB3, no weights)")
    args = parser.parse_args(argv)

    if not args.stub and not args.checkpoint:
        parser.error("--checkpoint is required unless --stub is given")
    if args.stub and args.checkpoint:
        _err(f"{TAG} WARNING: --stub ignores --checkpoint {args.checkpoint} "
             f"(no model is loaded; policies are synthetic)")

    try:
        contract = ScoreBlockContract.load()
    except ModelError as exc:
        _err(f"{TAG} fatal: {exc}")
        return EXIT_MODEL

    _err(f"{TAG} contract=controllers/score_block_rl/gym_env.py:123-130,162-168 "
         f"observation={contract.observation_size} action={contract.action_size} "
         f"stdout=jsonl flush=per-frame")

    if args.stub:
        policy = StubPolicy(contract.numpy)
        _err(f"{TAG} {policy.identity_line()}")
    else:
        try:
            checkpoint, how = resolve_checkpoint(args.checkpoint, contract)
        except CheckpointError as exc:
            _err(f"{TAG} fatal: {exc}")
            return EXIT_USAGE
        _err(f"{TAG} checkpoint resolved ({how}): {checkpoint}")
        try:
            policy = load_sb3_policy(checkpoint, contract)
        except ModelError as exc:
            _err(f"{TAG} fatal: {exc}")
            return EXIT_MODEL
        _err(f"{TAG} {policy.identity_line()}")

    try:
        stats = serve(policy, contract)
    except KeyboardInterrupt:
        _err(f"{TAG} interrupted; shutting down")
        return EXIT_OK
    except StdoutClosed as exc:
        # 管道断裂（桥被杀/消费者退出）：收尾，exit 0，不再尝试写 stdout。
        _detach_stdout()
        _err(f"{TAG} stdout pipe closed ({exc}); shutting down")
        return EXIT_OK
    _err(f"{TAG} exit reason=eof frames={stats['frames']} answered={stats['answered']} "
         f"zero={stats['zero']} faults={stats['faults']}")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
