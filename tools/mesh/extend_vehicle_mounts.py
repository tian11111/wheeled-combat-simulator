"""从整车 glb 枚举"光电/支架"类传感器挂点节点, 输出车体系挂点表(数值可溯源)。

分工与数据来源
--------------
  extract_vehicle_mesh.py  提取物理碰撞几何(STL); 本脚本解析同一份 glb,
                           load_glb/trs/to_body 直接 import 复用, 不复制实现。
  源模型: 装配.glb (SolidWorks 导出, 41 件)。glb 不存在或 sha256 不符即退出,
  不生成任何编造数据。

位置方法(硬性约束)
------------------
  严格 glTF TRS 世界矩阵合成: world = parent_world @ trs(node), 挂点位置取
  世界矩阵的平移分量。禁止 mesh AABB 中心/八角点近似(本项目吃过"轮径虚高"
  的亏, 见 tools/mesh/README.md "轮胎实测"一节)。

坐标映射(det=+1, 非镜像, 与 extract_vehicle_mesh.to_body 一致)
--------------------------------------------------------------
  bodyX = -modelZ   (车头 +X, 模型里车头朝 -Z)
  bodyY = -modelX   (左 +Y)
  bodyZ =  modelY   (上)
  单位: 米。

输出
----
  tools/mesh/sensor_mounts.json
    nodes          关键词("光电"/"支架")命中节点 -> 车体系 XYZ + model 系世界
                   矩阵(证据) + 局部基轴在车系的方向
    mounts         11 路通道挂点建议(id/label/node/x/y/z/note)
    defaults_used  采用了工程默认的通道及依据
    warnings       数量对不上/无法确认之处

用法
----
  py -3.12 tools/mesh/extend_vehicle_mounts.py --list      # 只打印命中节点, 不写文件
  py -3.12 tools/mesh/extend_vehicle_mounts.py             # 写 sensor_mounts.json
  py -3.12 tools/mesh/extend_vehicle_mounts.py --glb "C:/path/装配.glb"
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

import numpy as np

from extract_vehicle_mesh import (
    DEFAULT_GLB,
    EXPECTED_GLB_SHA256,
    load_glb,
    to_body,
    trs,
)

DEFAULT_OUT = Path(__file__).resolve().parent / "sensor_mounts.json"
KEYWORDS = ("光电", "支架")

# model -> body 线性映射(bodyX=-modelZ, bodyY=-modelX, bodyZ=modelY), det=+1 非镜像。
B_MODEL_TO_BODY = np.array([
    [0.0, 0.0, -1.0],
    [-1.0, 0.0, 0.0],
    [0.0, 1.0, 0.0],
])

# 底盘灰度 4 路的工程默认(仅当参考节点与"前后左右"布局对不上时使用):
#   探点在真车投影内贴地; 横向/纵向偏移 <= 0.09 m;
#   z 取离地 2.5 mm —— 车体原点=轮轴平面, 平地地面在 bodyZ=-0.0325(轮半径
#   r=0.0325, 见 tools/mesh/README.md 轮胎实测), 探点略高于地面避免入地。
GRAY_DEFAULTS = {
    "chassis_gray_front": [0.060, 0.000, -0.030],
    "chassis_gray_back": [-0.060, 0.000, -0.030],
    "chassis_gray_left": [0.000, 0.060, -0.030],
    "chassis_gray_right": [0.000, -0.060, -0.030],
}


def collect_node_frames(gltf):
    """节点索引 -> (name, world_model 4x4, parent_chain 名字列表)。严格 TRS 合成。"""
    nodes = gltf["nodes"]
    frames: dict[int, tuple[str, np.ndarray, list[str]]] = {}

    def visit(idx: int, parent_world: np.ndarray, chain: list[str]) -> None:
        node = nodes[idx]
        world = parent_world @ trs(node)
        name = node.get("name") or f"node{idx}"
        frames[idx] = (name, world, chain)
        for child in node.get("children", []):
            visit(child, world, chain + [name])

    for root in gltf["scenes"][gltf.get("scene", 0)]["nodes"]:
        visit(root, np.eye(4), [])
    return frames


def display_names(frames) -> dict[int, str]:
    """重名实例加 #k 后缀, 保证 JSON 里节点名可唯一定位。"""
    seen: dict[str, int] = {}
    out: dict[int, str] = {}
    for idx in sorted(frames):
        name = frames[idx][0]
        seen[name] = seen.get(name, 0) + 1
        out[idx] = name if seen[name] == 1 else f"{name}#{seen[name]}"
    return out


def body_frame(world_model: np.ndarray):
    """世界矩阵(model 系) -> (model 位置, body 位置, body 旋转)。"""
    p_model = world_model[:3, 3]
    p_body = to_body(p_model[None, :])[0]
    R_body = B_MODEL_TO_BODY @ world_model[:3, :3] @ B_MODEL_TO_BODY.T
    return p_model, p_body, R_body


def classify(name: str) -> str:
    if "屁股光电" in name:
        return "shovel_front"
    if "巡台光电" in name:
        return "diag"
    if "侧底光电支座" in name:
        return "shovel_under"
    if "矮光电座" in name:
        return "gray_low"
    if "光电" in name:
        return "gray_photo"
    return "bracket"  # 只含"支架", 不参与通道


def assign_channels(hits: list[dict]):
    """按任务映射表把命中节点分配到 11 路通道; 对不上的落工程默认并记录。"""
    mounts: list[dict] = []
    defaults_used: list[dict] = []
    warnings: list[str] = []
    by_cat: dict[str, list[dict]] = {}
    for h in hits:
        by_cat.setdefault(h["category"], []).append(h)
    channel_of: dict[int, list[str]] = {}

    def mount(cid: str, label: str, h: dict, note: str) -> None:
        p = h["body"]
        mounts.append({"id": cid, "label": label, "node": h["display"],
                       "x": round(float(p[0]), 6), "y": round(float(p[1]), 6),
                       "z": round(float(p[2]), 6), "note": note})
        channel_of.setdefault(h["index"], []).append(cid)

    # --- shovel_front(铲前红外) <- 屁股光电.stp-1 ---
    fs = by_cat.get("shovel_front", [])
    if len(fs) == 1:
        mount("shovel_front", "铲前红外", fs[0],
              "任务映射表: shovel_front <- 屁股光电.stp-1; 位置=该节点世界矩阵平移分量过 to_body")
    else:
        warnings.append(f"shovel_front: 期望 1 个'屁股光电'节点, 实际 {len(fs)}")

    # --- diag 四路对角数字 IR <- 巡台光电 x4, 车头两舷外八朝向 ---
    # 实测(--list): 4 节点全在车头两舷(bodyX>=0), 左舷(bodyY>0) 2 个、右舷 2 个,
    # 每舷一前一后 —— 与"车头两舷外八"吻合; 按 舷 x 前后位 分配, 不按四角象限。
    diag = by_cat.get("diag", [])
    if len(diag) == 4:
        port = sorted((h for h in diag if float(h["body"][1]) >= 0),
                      key=lambda h: -float(h["body"][0]))
        stbd = sorted((h for h in diag if float(h["body"][1]) < 0),
                      key=lambda h: -float(h["body"][0]))
        if len(port) == 2 and len(stbd) == 2:
            plan = [(port[0], "diag_fl", "前位"), (stbd[0], "diag_fr", "前位"),
                    (port[1], "diag_rl", "后位"), (stbd[1], "diag_rr", "后位")]
            for h, cid, pos in plan:
                mount(cid, "对角数字IR", h,
                      f"任务映射表: diag 对角数字IR <- 巡台光电 x4; 实测 4 节点全在车头两舷, "
                      f"左右舷各 2 个按 bodyX 降序取前/后位(本节点={pos}); "
                      "朝向要求=车头两舷外八, 具体偏角由 profile 侧工程定义, "
                      "节点旋转证据见 nodes.*.body_rotation_axes")
        else:
            warnings.append(f"diag: 期望左右舷各 2 个巡台光电, 实际左舷 {len(port)}/右舷 {len(stbd)}")
    else:
        warnings.append(f"diag: 期望 4 个'巡台光电'节点, 实际 {len(diag)}")

    # --- shovel_under 左右 <- 侧底光电支座 x2 ---
    us = by_cat.get("shovel_under", [])
    if len(us) == 2:
        for h in us:
            lr = "left" if float(h["body"][1]) >= 0 else "right"
            mount(f"shovel_under_{lr}", "铲下数字IR", h,
                  "任务映射表: shovel_under 左右 <- 侧底光电支座 x2; 左右按 bodyY 符号")
    else:
        warnings.append(f"shovel_under: 期望 2 个'侧底光电支座'节点, 实际 {len(us)}")

    # --- 底盘灰度 4 路 <- 光电 x2 + 矮光电座 x2 仅作参考; 对不上用工程默认 ---
    # 实测(--list): 4 个参考节点全在左右舷(|bodyY|=0.101~0.115, 超 0.09 横向约束),
    # z=+0.028~+0.046 在轮轴平面上方(非贴地探点), 且无前/后路候选 -> 4 路全用工程默认,
    # 参考节点原始位置保留在 nodes.* 供 profile 溯源。
    refs = by_cat.get("gray_low", []) + by_cat.get("gray_photo", [])
    off_axis = [h for h in refs if abs(float(h["body"][1])) > 0.09]
    pattern_ok = (len(refs) == 4 and not off_axis
                  and any(abs(float(h["body"][1])) <= 0.035 for h in refs)
                  and any(abs(float(h["body"][0])) <= 0.035 for h in refs))
    if pattern_ok:
        for h in refs:
            if abs(float(h["body"][1])) <= 0.035:   # 近 bodyY 中线 -> 前/后路
                fb = "front" if float(h["body"][0]) >= 0 else "back"
                mount(f"chassis_gray_{fb}", "底盘灰度", h,
                      "任务映射表: 底盘灰度 <- 光电/矮光电座仅作参考; 节点近 bodyY 中线, "
                      "按 bodyX 符号归前/后路")
            else:                                    # 近 bodyX 中线 -> 左/右路
                lr = "left" if float(h["body"][1]) >= 0 else "right"
                mount(f"chassis_gray_{lr}", "底盘灰度", h,
                      "任务映射表: 底盘灰度 <- 光电/矮光电座仅作参考; 节点近 bodyX 中线, "
                      "按 bodyY 符号归左/右路")
    else:
        detail = "; ".join(f"{h['display']} body=({float(h['body'][0]):+.4f}, "
                           f"{float(h['body'][1]):+.4f}, {float(h['body'][2]):+.4f})"
                           for h in refs)
        warnings.append(
            "底盘灰度: 参考节点与前后左右贴地布局对不上 -> 4 路用工程默认。"
            f"对不上的依据: {len(off_axis)}/4 节点横向偏移>0.09; "
            "全部节点在轮轴平面上方(非贴地); 无前/后路中线候选。参考位置: " + detail)
        for cid, p in GRAY_DEFAULTS.items():
            fb = {"front": "前", "back": "后", "left": "左", "right": "右"}[cid.split("_")[-1]]
            mounts.append({"id": cid, "label": "底盘灰度", "node": None,
                           "x": p[0], "y": p[1], "z": p[2],
                           "note": f"工程默认({fb}路): 真车投影内贴地, 横向/纵向偏移<=0.09, "
                                   "z=-0.030 离地 2.5mm(原点=轮轴平面, 地面 bodyZ=-0.0325)"})
            defaults_used.append({"id": cid, "value": p,
                                  "reason": "参考节点与前后左右贴地布局对不上"
                                            "(横向超 0.09 / 非贴地 / 无前后候选)"})

    for h in hits:
        h["channels"] = channel_of.get(h["index"], [])
    mounts.sort(key=lambda m: m["id"])
    return mounts, defaults_used, warnings


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--glb", default=DEFAULT_GLB)
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--list", action="store_true", help="只打印关键词命中节点, 不写 JSON")
    args = ap.parse_args()

    glb = Path(args.glb)
    if not glb.exists():
        print(f"glb not found: {glb}", file=sys.stderr)
        return 2
    digest = hashlib.sha256(glb.read_bytes()).hexdigest()
    if digest != EXPECTED_GLB_SHA256:
        print(f"glb sha256 mismatch: expected {EXPECTED_GLB_SHA256}, got {digest}", file=sys.stderr)
        return 2

    gltf, _bins = load_glb(glb)
    frames = collect_node_frames(gltf)
    names = display_names(frames)
    nodes = gltf["nodes"]

    if args.list:
        print(f"glb {glb} sha256 OK; 节点总数 {len(frames)}")
        print("\n-- 全部节点名 --")
        uniq = sorted({frames[i][0] for i in frames})
        for n in uniq:
            print(f"  {n}")
        print("\n-- 关键词命中(光电/支架) --")
        for idx in sorted(frames):
            name = frames[idx][0]
            if not any(k in name for k in KEYWORDS):
                continue
            node = nodes[idx]
            _, p_body, _ = body_frame(frames[idx][1])
            print(f"  [{idx:3d}] {names[idx]:30s} cat={classify(name):12s} "
                  f"mesh={'Y' if 'mesh' in node else 'N'} "
                  f"body=({float(p_body[0]):+.4f}, {float(p_body[1]):+.4f}, {float(p_body[2]):+.4f}) "
                  f"chain={' < '.join(frames[idx][2]) or '(root)'}")
        return 0

    hits = []
    for idx in sorted(frames):
        name = frames[idx][0]
        if not any(k in name for k in KEYWORDS):
            continue
        p_model, p_body, R_body = body_frame(frames[idx][1])
        world = frames[idx][1]
        hits.append({
            "index": idx,
            "display": names[idx],
            "raw_name": name,
            "category": classify(name),
            "model": p_model,
            "body": p_body,
            "R_body": R_body,
            "world": world,
            "has_mesh": "mesh" in nodes[idx],
            "chain": frames[idx][2],
        })

    mounts, defaults_used, warnings = assign_channels(hits)

    nodes_json = []
    for h in hits:
        axes = {"local_x": np.round(h["R_body"][:, 0], 6).tolist(),
                "local_y": np.round(h["R_body"][:, 1], 6).tolist(),
                "local_z": np.round(h["R_body"][:, 2], 6).tolist()}
        nodes_json.append({
            "node": h["display"],
            "index": h["index"],
            "parent_chain": h["chain"],
            "has_mesh": h["has_mesh"],
            "category": h["category"],
            "model_position_m": np.round(h["model"], 6).tolist(),
            "body_position_m": np.round(h["body"], 6).tolist(),
            "world_matrix_model_rowmajor": np.round(h["world"], 8).tolist(),
            "body_rotation_axes": axes,
            "channels": h["channels"],
        })

    doc = {
        "generated_by": "tools/mesh/extend_vehicle_mounts.py",
        "source_glb": {"path": str(glb).replace("\\", "/"), "sha256": digest,
                       "size_bytes": glb.stat().st_size},
        "method": {
            "position": "严格 glTF TRS 世界矩阵合成 world = parent @ trs(node), "
                        "位置取世界矩阵平移分量; 无 mesh AABB/八角点近似",
            "coordinate_mapping": "bodyX=-modelZ, bodyY=-modelX, bodyZ=modelY (米); det=+1 非镜像",
        },
        "nodes": nodes_json,
        "mounts": mounts,
        "defaults_used": defaults_used,
        "warnings": warnings,
    }
    out = Path(args.out)
    out.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {out} ({out.stat().st_size} B)")
    print(f"  nodes={len(nodes_json)} mounts={len(mounts)} defaults={len(defaults_used)} warnings={len(warnings)}")
    for w in warnings:
        print(f"  !! {w}")
    for m in mounts:
        print(f"  {m['id']:22s} node={str(m['node']):26s} "
              f"xyz=({m['x']:+.4f}, {m['y']:+.4f}, {m['z']:+.4f})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
