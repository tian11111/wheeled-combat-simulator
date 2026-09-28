"""从 SolidWorks 导出的整车 glb 提取 MuJoCo 物理车体的关键结构件(可复现)。

来源资产
--------
  装配.glb  (SolidWorks 导出, 41 件)
  sha256 9538408e7a7e1cc039f30c49b9a56519382133dc9028eb6693bd0cd2a58b88a0
  2881660 B
本脚本即 "哪次命令" 记录在 README.md 的 "复现" 一节; 换 glb 必须同步更新
EXPECTED_GLB_SHA256, 否则脚本拒绝运行(资产与物理身份 ModelSha256 绑定)。

坐标映射(已验证 det=+1, 非镜像)
-------------------------------
  bodyX = -modelZ   (车头方向, 模型里车头朝 -Z)
  bodyY = -modelX
  bodyZ =  modelY   (上)
原点取模型原点 = 轮轴平面(轮心), 因此 bodyZ=0 与轮心共面, spawn 高度 = 轮半径。

输出
----
  robot_chassis.stl      底板.stp-1 + 上板.stp-1 合并为单一凸体(两板间隙 0.03 m,
                         分开做 geom 只增加接触对而无行为收益)
  robot_rear_shovel.stl  后铲.stp-1(唯一能勾台沿的凹角几何, 必须与车体分开)
  robot_front_arm.stl    铲臂(2)(物理 v2 未使用, 入库供渲染/后续版本比对)

用法
----
  py -3.12 tools/mesh/extract_vehicle_mesh.py --glb "C:/path/装配.glb" --out src/Sim.Mujoco/assets
  py -3.12 tools/mesh/extract_vehicle_mesh.py --check       # 只比对, 不写文件

依赖: numpy(仅用于矩阵/包围盒运算; 顶点去重与凸包交给 MuJoCo 编译期, 见 README)。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

import numpy as np

EXPECTED_GLB_SHA256 = "9538408e7a7e1cc039f30c49b9a56519382133dc9028eb6693bd0cd2a58b88a0"
DEFAULT_GLB = r"C:\Users\Neco\Downloads\装配.glb"
DEFAULT_OUT = Path(__file__).resolve().parents[2] / "src" / "Sim.Mujoco" / "assets"

# 部件名 -> 输出组。名字来自 glb 节点名(SolidWorks 装配体零件名)。
GROUPS = {
    "robot_chassis": ["底板.stp-1", "上板.stp-1"],
    "robot_rear_shovel": ["后铲.stp-1"],
    "robot_front_arm": ["铲臂（2）.00.stp-1", "铲臂（2）.00_MIR.stp-1"],
}

DT = {5126: "<f4", 5125: "<u4", 5123: "<u2", 5121: "<u1"}
NC = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}


def load_glb(path: Path):
    raw = path.read_bytes()
    _, _, total = struct.unpack_from("<4sII", raw, 0)
    off, chunks = 12, []
    while off < total:
        clen, ctype = struct.unpack_from("<II", raw, off)
        off += 8
        chunks.append((ctype, raw[off:off + clen]))
        off += clen
    if not chunks or chunks[0][0] != 0x4E4F534A:  # 'JSON'
        raise SystemExit(f"{path}: first chunk is not the glTF JSON chunk")
    gltf = json.loads(chunks[0][1].decode("utf-8").rstrip("\x00"))
    return gltf, [c[1] for c in chunks[1:]]


def read_accessor(gltf, bins, idx):
    acc, bv = gltf["accessors"][idx], gltf["bufferViews"][gltf["accessors"][idx]["bufferView"]]
    dtype, ncomp = DT[acc["componentType"]], NC[acc["type"]]
    start = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    itemsize = np.dtype(dtype).itemsize * ncomp
    stride = bv.get("byteStride") or itemsize
    buf = bins[bv["buffer"]]
    if stride == itemsize:
        return np.frombuffer(buf, dtype=dtype, count=acc["count"] * ncomp, offset=start).reshape(-1, ncomp)
    out = np.empty((acc["count"], ncomp), dtype=dtype)
    for i in range(acc["count"]):
        out[i] = np.frombuffer(buf, dtype=dtype, count=ncomp, offset=start + i * stride)
    return out


def trs(node):
    """glTF 节点局部矩阵。matrix 优先; 否则 T * R * S —— scale 乘在基向量列上
    (直接把旋转矩阵对角线替换成 scale 是错的, 会破坏带缩放的父节点)。"""
    if "matrix" in node:
        return np.array(node["matrix"], dtype=float).reshape(4, 4).T
    m = np.eye(4)
    if "rotation" in node:
        x, y, z, w = node["rotation"]
        m[:3, :3] = np.array([
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
    if "scale" in node:
        m[:3, 0] *= node["scale"][0]
        m[:3, 1] *= node["scale"][1]
        m[:3, 2] *= node["scale"][2]
    if "translation" in node:
        m[:3, 3] = node["translation"]
    return m


def collect_parts(gltf, bins):
    """节点名 -> 模型空间三角面 (n,3,3)。"""
    nodes, meshes = gltf["nodes"], gltf["meshes"]
    parts: dict[str, list[np.ndarray]] = {}

    def visit(idx, parent):
        node = nodes[idx]
        world = parent @ trs(node)
        if "mesh" in node:
            name = node.get("name") or meshes[node["mesh"]].get("name") or f"node{idx}"
            tris = []
            for prim in meshes[node["mesh"]].get("primitives", []):
                pos = read_accessor(gltf, bins, prim["attributes"]["POSITION"]).astype(np.float64)
                ph = np.hstack([pos, np.ones((len(pos), 1))])
                world_pos = (world @ ph.T).T[:, :3]
                if "indices" in prim:
                    order = read_accessor(gltf, bins, prim["indices"]).astype(np.int64).ravel()
                else:
                    order = np.arange(len(pos))
                tris.append(world_pos[order].reshape(-1, 3, 3))
            if tris:
                parts.setdefault(name, []).append(np.vstack(tris))
        for child in node.get("children", []):
            visit(child, world)

    for root in gltf["scenes"][gltf.get("scene", 0)]["nodes"]:
        visit(root, np.eye(4))
    return parts


def to_body(points: np.ndarray) -> np.ndarray:
    """model (Y-up, 车头 -Z) -> body (Z-up, 车头 +X)。"""
    x, y, z = points[..., 0], points[..., 1], points[..., 2]
    return np.stack([-z, -x, y], axis=-1)


def stl_bytes(tris: np.ndarray) -> bytes:
    """二进制 STL 字节(80 B 说明 + 三角面数 + 每面 12 个 float32 + 2 B 属性)。"""
    out = bytearray(b"MJCF body collider (from SW export)".ljust(80, b" "))
    out += struct.pack("<I", len(tris))
    for t in tris:
        n = np.cross(t[1] - t[0], t[2] - t[0])
        ln = float(np.linalg.norm(n))
        n = n / ln if ln > 1e-12 else np.array([0.0, 0.0, 1.0])
        out += struct.pack("<3f", *n.astype(np.float32))
        for v in t:
            out += struct.pack("<3f", *v.astype(np.float32))
        out += struct.pack("<H", 0)
    return bytes(out)


def write_stl(path: Path, tris: np.ndarray) -> None:
    path.write_bytes(stl_bytes(tris))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--glb", default=DEFAULT_GLB)
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--check", action="store_true", help="只比对 --out 下已有文件, 不写入")
    args = ap.parse_args()

    glb = Path(args.glb)
    if not glb.exists():
        print(f"glb not found: {glb}", file=sys.stderr)
        return 2
    digest = hashlib.sha256(glb.read_bytes()).hexdigest()
    if digest != EXPECTED_GLB_SHA256:
        print(f"glb sha256 mismatch: expected {EXPECTED_GLB_SHA256}, got {digest}", file=sys.stderr)
        return 2
    print(f"glb {glb} sha256={digest}")

    gltf, bins = load_glb(glb)
    parts = collect_parts(gltf, bins)
    out = Path(args.out)
    failures = 0
    for group, names in GROUPS.items():
        tris = []
        for name in names:
            if name not in parts:
                print(f"  !! missing part: {name}")
                continue
            tris.append(to_body(parts[name][0]))
        if not tris:
            failures += 1
            continue
        allt = np.vstack(tris)
        pts = allt.reshape(-1, 3)
        lo, hi = pts.min(axis=0), pts.max(axis=0)
        target = out / f"{group}.stl"
        print(f"{group:20s} parts={len(names)} tris={len(allt):6d} "
              f"bbox={np.round(lo, 6)} .. {np.round(hi, 6)} size={np.round(hi - lo, 6)}")
        if args.check:
            if not target.exists():
                print(f"  !! missing output {target}")
                failures += 1
                continue
            same = target.read_bytes() == stl_bytes(allt)
            print(f"  {'OK  ' if same else 'DIFF'} {target}")
            failures += 0 if same else 1
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            write_stl(target, allt)
            print(f"  wrote {target} ({target.stat().st_size} B)")

    # 轮胎实测(物理 v2 的轮径/轮位常量来源; 轮子不导出, 物理用 cylinder)
    print("\n轮胎实测(model 空间, 供 v2 常量核对):")
    for name, t in sorted(parts.items()):
        if "胎皮" not in name:
            continue
        pts = to_body(t[0]).reshape(-1, 3)
        lo, hi = pts.min(axis=0), pts.max(axis=0)
        size = np.sort(hi - lo)
        print(f"  {name:34s} center={np.round((lo + hi) / 2, 6)} "
              f"diameter={size[1:].mean():.6f} width={size[0]:.6f}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
