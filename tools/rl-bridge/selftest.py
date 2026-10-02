#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""rl_desktop_runner.py 自测：协议契约 / 逐帧 flush / 维度门 / 确定性 / stub 模式。

运行：``py -3.12 tools/rl-bridge/selftest.py``（退出码 0 = 全绿；1 = 有失败）。

不依赖真权重：stub 腿不需要 torch；真模式腿用**现场生成的微型 PPO**
（11 维与 9 维各一个，8×8 MLP）验证加载、动作一致性与旧维度拒载。
torch/SB3 不可用时真模式腿整体 SKIP，stub 协议链路仍必须全绿（与任务口径一致）。

可选腿：仓库里存在已构建的 ``src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll`` 时，
跑一次真实 ``rl-env`` 并把 11 维观测喂给本 runner（跨语言契约钉桩）；缺 dll 时 SKIP。
"""

from __future__ import annotations

import importlib.util
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[1]
RUNNER_PATH = HERE / "rl_desktop_runner.py"
CLI_DLL = REPO_ROOT / "src" / "Sim.Cli" / "bin" / "Debug" / "net8.0" / "Sim.Cli.dll"
SCENARIO = REPO_ROOT / "scenarios" / "wushu-ring-2026-mujoco.json"

RESULTS: list[tuple[str, bool, str]] = []
SKIPS: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def load_runner_module():
    spec = importlib.util.spec_from_file_location("rl_desktop_runner_under_test", RUNNER_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


runner = load_runner_module()


def run_runner(args: list[str], stdin_text: str = "", timeout: float = 300.0):
    return subprocess.run(
        [sys.executable, str(RUNNER_PATH), *args],
        input=stdin_text, capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=timeout, cwd=str(REPO_ROOT))


def spawn_runner(args: list[str]):
    return subprocess.Popen(
        [sys.executable, str(RUNNER_PATH), *args],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", errors="replace", bufsize=1, cwd=str(REPO_ROOT))


def send(proc, frame) -> None:
    proc.stdin.write((frame if isinstance(frame, str) else json.dumps(frame)) + "\n")
    proc.stdin.flush()


def read_line(proc, timeout: float = 60.0) -> str:
    """带超时读一行：不关闭 stdin 也能拿到 ⇒ 证明 runner 是逐帧 flush 的。"""
    inbox: queue.Queue = queue.Queue()

    def _read() -> None:
        inbox.put(proc.stdout.readline())

    thread = threading.Thread(target=_read, daemon=True)
    thread.start()
    try:
        line = inbox.get(timeout=timeout)
    except queue.Empty as exc:
        raise AssertionError(f"stdout 在 {timeout}s 内没有整行输出（flush 契约破坏）") from exc
    if line == "":
        raise AssertionError(f"stdout 提前 EOF（exit={proc.poll()}）；stderr={proc.stderr.read()}")
    return line


def parse_action(line: str) -> dict:
    payload = json.loads(line)
    check(isinstance(payload, dict), f"动作行必须是 JSON 对象: {line!r}")
    check(set(payload) <= {"v", "w", "requestId"}, f"动作行出现未契约字段: {sorted(payload)}")
    for key in ("v", "w"):
        check(key in payload and isinstance(payload[key], (int, float))
              and float(payload[key]) == float(payload[key])
              and abs(float(payload[key])) != float("inf"),
              f"动作行 {key} 必须是有限数值: {line!r}")
    return payload


def finish(proc, timeout: float = 60.0):
    out, err = proc.communicate(timeout=timeout)
    return out, err


# --------------------------------------------------------------------------- #
# 1. CLI 契约
# --------------------------------------------------------------------------- #
def test_cli_contract() -> None:
    help_run = run_runner(["--help"])
    check(help_run.returncode == 0, f"--help 退出码应为 0: {help_run.returncode}")
    check("--checkpoint" in help_run.stdout and "--stub" in help_run.stdout,
          "--help 必须说明 --checkpoint 与 --stub")

    missing = run_runner([])
    check(missing.returncode == runner.EXIT_USAGE,
          f"缺 --checkpoint 应退出 {runner.EXIT_USAGE}: {missing.returncode}")
    check(missing.stdout == "", "参数错误时不得写 stdout")

    stub_with_checkpoint = run_runner(["--stub", "--checkpoint", "whatever.zip"], "")
    check(stub_with_checkpoint.returncode == 0, stub_with_checkpoint.stderr)
    check("WARNING" in stub_with_checkpoint.stderr and "ignores --checkpoint" in stub_with_checkpoint.stderr,
          "stub 模式带 --checkpoint 必须显式警告未加载模型（不静默）")
    print("OK cli contract (help / missing checkpoint -> 2 / stub ignores checkpoint loudly)")


# --------------------------------------------------------------------------- #
# 2. 契约单元：解析与映射来自 score_block_rl（同一实现，不复制）
# --------------------------------------------------------------------------- #
def test_contract_unit() -> None:
    contract = runner.ScoreBlockContract.load()
    np = contract.numpy
    check(contract.observation_size == 11, contract.observation_size)

    sample = [0.173, 0.0865, 0.5625, 0.5625, 0.0, -0.0003, 1.0, 1.0, 0.8829, -0.7476, -0.7151]
    parsed = contract.parse_observation(sample)
    check(parsed.shape == (11,) and parsed.dtype == np.float32,
          f"解析结果应为 (11,) float32: {parsed.shape} {parsed.dtype}")
    check(bool(np.isfinite(parsed).all()), "解析结果必须有限")
    check(abs(float(parsed[0]) - 0.173) < 1e-6, parsed[0])
    check(float(parsed[5]) < 0.0, "第 6 项（Omega/MaxTurnRate）应为负 —— 顺序契约")

    for bad, why in (([0.0] * 10, "10 维"), ([0.0] * 12, "12 维"), ("abc", "非数值"),
                     ([float("inf")] * 11, "非有限")):
        try:
            contract.parse_observation(bad)
        except RuntimeError:
            pass
        else:
            raise AssertionError(f"非法观测（{why}）必须抛 RuntimeError")

    # 动作映射 = gym_env.py:162-168：v=clip(a0,-1,1)，w=clip(a1,-1,1)*2.0。
    check(contract.action_to_vw(np.array([2.0, -2.0], np.float32)) == (1.0, -2.0),
          "映射必须对称钳位")
    v, w = contract.action_to_vw(np.array([0.25, -0.5], np.float32))
    check(abs(v - 0.25) < 1e-6 and abs(w + 1.0) < 1e-6, (v, w))
    for bad in (np.array([0.1], np.float32), np.array([float("nan"), 0.0], np.float32)):
        try:
            contract.action_to_vw(bad)
        except ValueError:
            pass
        else:
            raise AssertionError("非法动作必须抛 ValueError（gym_env 的既有校验）")
    print("OK contract unit (parse/validate 11 finite values; clip mapping 1:1 with gym_env)")


# --------------------------------------------------------------------------- #
# 3. stub 协议流：逐帧 flush、逐行一比一、坏行不炸流
# --------------------------------------------------------------------------- #
def test_stub_protocol_stream() -> None:
    contract = runner.ScoreBlockContract.load()
    np = contract.numpy
    stub = runner.StubPolicy(np)
    proc = spawn_runner(["--stub"])
    try:
        received: list[str] = []
        expected_lines = 0

        def take() -> dict:
            line = read_line(proc)
            received.append(line)
            return parse_action(line)

        # ① 预检残帧（ControllerPreflight 的 Observation{RequestId=1}）：立即零动作。
        send(proc, {"protocolVersion": "sim-cli-v1", "requestId": 1, "tick": 0, "role": "us"})
        expected_lines += 1
        action = take()
        check(action == {"v": 0.0, "w": 0.0, "requestId": 1}, action)

        # ② 合法 11 维观测：动作 = 合成策略经契约映射（同一实现）。
        obs = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.1, 0.2]
        send(proc, {"requestId": 2, "rlObservation": obs, "note": "诊断字段-UTF8"})
        expected_lines += 1
        action = take()
        ev, ew = contract.action_to_vw(stub.act(np.asarray(obs, np.float32)))
        check(action["requestId"] == 2, action)
        check(abs(action["v"] - ev) < 1e-6 and abs(action["w"] - ew) < 1e-6,
              f"stub 动作应与契约映射一致: {action} vs {(ev, ew)}")

        # ③ rl-env 响应兼容载体（type+obs）。
        send(proc, {"type": "reset", "obs": obs, "requestId": "req-3"})
        expected_lines += 1
        action = take()
        check(action["requestId"] == "req-3", "字符串 requestId 必须原样回显")
        check(abs(action["v"] - ev) < 1e-6, action)

        # ④ 坏行：零动作、无 requestId、不炸流。
        send(proc, "this is not json")
        expected_lines += 1
        action = take()
        check(action == {"v": 0.0, "w": 0.0}, action)

        # ⑤ 维度不符 / 非有限：零动作 + 回显 requestId（绝不补齐或重算）。
        send(proc, {"requestId": 5, "rlObservation": [0.0] * 10})
        expected_lines += 1
        action = take()
        check(action == {"v": 0.0, "w": 0.0, "requestId": 5}, action)
        send(proc, {"requestId": 6, "rlObservation": [0.0] * 10 + [1e400]})
        expected_lines += 1
        action = take()
        check(action == {"v": 0.0, "w": 0.0, "requestId": 6}, action)

        # ⑥ 非标量 requestId（桥会拒整行）：不回显，行仍合法且观测照常推理。
        send(proc, {"requestId": {"nested": 1}, "rlObservation": obs})
        expected_lines += 1
        action = take()
        check("requestId" not in action, action)
        check(abs(action["v"] - ev) < 1e-6, action)

        # ⑦ 坏帧之后流仍然可用（坏行不炸流）。
        send(proc, {"requestId": 8, "rlObservation": obs})
        expected_lines += 1
        action = take()
        check(action["requestId"] == 8 and abs(action["w"] - ew) < 1e-6, action)

        proc.stdin.close()
        out, err = finish(proc)
        check(proc.returncode == 0, f"stdin EOF 后应 exit 0: {proc.returncode} / {err}")
        check(len(received) == expected_lines,
              f"stdout 行数必须与输入行数恒等: {len(received)} != {expected_lines}")
        check(out == "", f"EOF 后不得再有输出: {out!r}")
        check("faults=3" in err, f"stderr 应报 3 次 fault（坏行×1、10 维×1、非有限×1；"
                                f"嵌套 requestId 与残帧不算）: {err}")
        check("exit reason=eof" in err, err)
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.communicate()
    print("OK stub protocol stream (pre-flight frame, UTF-8, bad lines, 1:1 lines, "
          "per-frame flush while stdin stays open)")


def test_broken_pipe_shutdown() -> None:
    """stdout 管道断裂必须收尾且 exit 0。

    回归用例：Windows 上 flush 到已关闭的管道报 ``OSError [Errno 22]``（EINVAL），
    不是 ``BrokenPipeError``；只捕后者时 runner 会继续空转写故障帧、永不收尾。
    """
    proc = spawn_runner(["--stub"])
    try:
        send(proc, {"requestId": 1, "rlObservation": [0.1] * 11})
        parse_action(read_line(proc))
        proc.stdout.close()          # 关闭读端：之后的写入必然失败
        time.sleep(0.3)
        try:
            for i in range(2, 400):
                send(proc, {"requestId": i, "rlObservation": [0.1] * 11})
        except (BrokenPipeError, OSError):
            pass                     # 子进程收尾后本端写入也会失败，属预期
        try:
            returncode = proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
            raise AssertionError("stdout 断裂后 runner 未在 30s 内收尾（空转）")
        check(returncode == 0, f"断管道应 exit 0: {returncode}")
        err = proc.stderr.read()
        check("stdout pipe closed" in err, f"必须留下断管道诊断: {err}")
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.communicate()
    print("OK broken stdout pipe -> clean shutdown, exit 0")


def test_stub_determinism() -> None:
    payload = "".join([
        json.dumps({"requestId": 1}) + "\n",
        json.dumps({"requestId": 2, "rlObservation": [0.1, 0.2, 0.3, 0.4, 0.5, 0.6,
                                                      0.7, 0.8, 0.9, 0.1, 0.2]}) + "\n",
        "not json\n",
        json.dumps({"requestId": 4, "rlObservation": [0.5] * 11}) + "\n",
    ])
    first = run_runner(["--stub"], payload)
    second = run_runner(["--stub"], payload)
    check(first.returncode == 0 and second.returncode == 0, (first.returncode, second.returncode))
    check(first.stdout == second.stdout, "同输入两次运行的 stdout 必须逐字节一致")
    check(len(first.stdout.splitlines()) == 4, first.stdout)
    print("OK stub determinism (identical input -> byte-identical stdout)")


# --------------------------------------------------------------------------- #
# 4. 真模式：微型 11 维 PPO 加载/一致性 + 9 维拒载 + 路径错误
# --------------------------------------------------------------------------- #
class TinyEnv:
    """极简 gymnasium 环境（只为让 SB3 能构造/保存微型策略）。"""

    @staticmethod
    def build(observation_size: int):
        import gymnasium as gym
        import numpy as np

        class _Env(gym.Env):
            metadata = {"render_modes": []}

            def __init__(self):
                self.observation_space = gym.spaces.Box(-1.0, 1.0, (observation_size,),
                                                        dtype=np.float32)
                self.action_space = gym.spaces.Box(-1.0, 1.0, (2,), dtype=np.float32)

            def reset(self, *, seed=None, options=None):
                super().reset(seed=seed)
                return np.zeros((observation_size,), dtype=np.float32), {}

            def step(self, action):
                return np.zeros((observation_size,), dtype=np.float32), 0.0, False, False, {}

        return _Env()


def make_tiny_ppo(path: Path, observation_size: int) -> None:
    from stable_baselines3 import PPO

    model = PPO("MlpPolicy", TinyEnv.build(observation_size), n_steps=8, batch_size=8,
                n_epochs=1, policy_kwargs={"net_arch": [8, 8]}, device="cpu", seed=0,
                verbose=0)
    model.save(str(path))


def test_real_mode() -> None:
    try:
        import torch  # noqa: F401
        from stable_baselines3 import PPO
    except ImportError as exc:
        SKIPS.append(f"real-mode legs skipped: torch/stable-baselines3 unavailable ({exc})")
        print(f"SKIP real-mode legs (torch/stable-baselines3 unavailable: {exc})")
        return

    contract = runner.ScoreBlockContract.load()
    np = contract.numpy
    observations = [
        [0.173, 0.0865, 0.5625, 0.5625, 0.0, -0.0003, 1.0, 1.0, 0.8829, -0.7476, -0.7151],
        [0.0, -0.25, 0.5, 0.5, 1.0, 0.5, 1.0, 0.0, 0.5, 0.25, -0.75],
    ]
    payload = "".join(
        json.dumps({"requestId": i, "rlObservation": obs}) + "\n"
        for i, obs in enumerate(observations, start=1))

    with tempfile.TemporaryDirectory() as work:
        workdir = Path(work)
        good = workdir / "ppo_score_block.zip"
        make_tiny_ppo(good, 11)
        model = PPO.load(str(good), device="cpu")
        expected = []
        for obs in observations:
            action, _ = model.predict(np.asarray(obs, np.float32), deterministic=True)
            expected.append(contract.action_to_vw(action))

        # 目录解析（<dir>/ppo_score_block.zip）+ 真模式动作与 SB3 预测逐一一致。
        first = run_runner(["--checkpoint", str(workdir)], payload)
        check(first.returncode == 0, f"真模式退出码应为 0: {first.returncode}\n{first.stderr}")
        actions = [parse_action(line) for line in first.stdout.splitlines()]
        check(len(actions) == len(observations), first.stdout)
        for i, (action, (ev, ew)) in enumerate(zip(actions, expected), start=1):
            check(action["requestId"] == i, action)
            check(abs(action["v"] - ev) < 1e-6 and abs(action["w"] - ew) < 1e-6,
                  f"第 {i} 帧动作与 PPO.predict(deterministic=True) 不一致: "
                  f"{action} vs {(ev, ew)}")
        check("observation=(11,)" in first.stderr and "deterministic=True" in first.stderr,
              first.stderr)

        # 真模式确定性（同输入同输出）。
        second = run_runner(["--checkpoint", str(good)], payload)
        check(second.stdout == first.stdout, "真模式两次运行 stdout 必须逐字节一致")

        # 旧 9 维模型：零 stdout、退出 3、给出重训指引。
        legacy = workdir / "legacy_9.zip"
        make_tiny_ppo(legacy, 9)
        legacy_run = run_runner(["--checkpoint", str(legacy)], payload)
        check(legacy_run.returncode == runner.EXIT_MODEL,
              f"9 维模型应退出 {runner.EXIT_MODEL}: {legacy_run.returncode}")
        check(legacy_run.stdout == "", "维度门失败不得写任何 stdout")
        check("(9,)" in legacy_run.stderr and "retrained" in legacy_run.stderr,
              f"维度门必须给出重训指引: {legacy_run.stderr}")

        # 路径错误：不存在 / 裸 policy.pth（SB3 产物是 zip，policy.pth 在 zip 内）。
        absent = run_runner(["--checkpoint", str(workdir / "nope.zip")], payload)
        check(absent.returncode == runner.EXIT_USAGE and absent.stdout == "", absent.stderr)
        check("does not exist" in absent.stderr, absent.stderr)

        pth = workdir / "policy.pth"
        pth.write_bytes(b"not a checkpoint")
        pth_run = run_runner(["--checkpoint", str(pth)], payload)
        check(pth_run.returncode == runner.EXIT_USAGE and pth_run.stdout == "", pth_run.stderr)
        check("policy.pth" in pth_run.stderr and "zip" in pth_run.stderr,
              f"裸 .pth 必须说明 SB3 归档契约: {pth_run.stderr}")
    print("OK real mode (11-dim load + predict 1:1, determinism, 9-dim gate -> exit 3 "
          "zero stdout, path errors -> exit 2)")


# --------------------------------------------------------------------------- #
# 5. 跨语言（可选）：真实 rl-env 的 11 维观测喂进 runner
# --------------------------------------------------------------------------- #
def resolve_dotnet() -> str | None:
    override = os.environ.get("RL_BRIDGE_DOTNET")
    if override:
        return override if Path(override).is_file() else None
    found = shutil.which("dotnet")
    if found:
        return found
    portable = Path.home() / "AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"
    return str(portable) if portable.is_file() else None


def test_rl_env_cross_language() -> None:
    if not CLI_DLL.is_file():
        SKIPS.append(f"rl-env cross-language leg skipped: {CLI_DLL} not built")
        print(f"SKIP rl-env cross-language leg ({CLI_DLL} missing)")
        return
    dotnet = resolve_dotnet()
    if dotnet is None:
        SKIPS.append("rl-env cross-language leg skipped: dotnet not found")
        print("SKIP rl-env cross-language leg (dotnet not found)")
        return
    smoke = subprocess.run(
        [dotnet, "exec", str(CLI_DLL), "rl-env", "--scenario", str(SCENARIO)],
        input='{"op":"reset","seed":42}\n{"op":"step","v":0.5,"w":0.0}\n{"op":"close"}\n',
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300,
        cwd=str(REPO_ROOT))
    check(smoke.returncode == 0, f"rl-env 冒烟失败: {smoke.returncode}\n{smoke.stderr}")
    replies = [json.loads(line) for line in smoke.stdout.splitlines() if line.strip()]
    obs_list = [r["obs"] for r in replies if r.get("type") in ("reset", "step")]
    check(len(obs_list) == 2, f"rl-env 应给出 reset+step 两个观测: {len(obs_list)}")
    for obs in obs_list:
        check(len(obs) == 11, f"C# 侧 11 维契约被破坏: {len(obs)}")

    runner_input = "".join(
        json.dumps({"requestId": i, "rlObservation": obs}) + "\n"
        for i, obs in enumerate(obs_list, start=1))
    result = run_runner(["--stub"], runner_input)
    check(result.returncode == 0, result.stderr)
    actions = [parse_action(line) for line in result.stdout.splitlines()]
    check(len(actions) == 2, result.stdout)
    check(actions[0]["requestId"] == 1 and actions[1]["requestId"] == 2, actions)
    print("OK rl-env cross-language leg (real C# 11-dim obs -> runner actions)")


TESTS = (
    ("cli contract", test_cli_contract),
    ("contract unit", test_contract_unit),
    ("stub protocol stream", test_stub_protocol_stream),
    ("broken pipe shutdown", test_broken_pipe_shutdown),
    ("stub determinism", test_stub_determinism),
    ("real mode", test_real_mode),
    ("rl-env cross-language", test_rl_env_cross_language),
)


def main() -> int:
    for name, test in TESTS:
        try:
            test()
            RESULTS.append((name, True, ""))
        except Exception as exc:  # noqa: BLE001 - 自测harness要收集全部失败
            RESULTS.append((name, False, f"{type(exc).__name__}: {exc}"))
            print(f"FAIL {name}: {type(exc).__name__}: {exc}")
    failed = [name for name, ok, _ in RESULTS if not ok]
    print("-" * 72)
    for name, ok, detail in RESULTS:
        print(f"{'PASS' if ok else 'FAIL'} {name}" + (f"  -- {detail}" if detail else ""))
    for note in SKIPS:
        print(f"SKIP {note}")
    if failed:
        print(f"RL-BRIDGE SELFTEST FAILED: {len(failed)}/{len(RESULTS)} leg(s) failed")
        return 1
    print("ALL RL-BRIDGE SELFTESTS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
