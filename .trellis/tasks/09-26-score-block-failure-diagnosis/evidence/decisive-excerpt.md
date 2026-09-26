# 决定性原始轨迹摘录

全量逐 tick 轨迹（120 个 `.jsonl.gz`，24.6 MB）不入 Git；下列摘录直接来自这些文件，
可由 `diagnose.py excerpts` 重跑复现。

## seed 6037 / policy / tick 317

单机器人多点接触被误判为 simultaneous：我方距块 0.218 m，对手距块 1.782 m

- 轨迹文件: `traces/final_holdout_v2-policy-6037.jsonl.gz`（不入 Git）

```json
{
 "tick": 317,
 "action": [
  -1.0,
  0.15941540896892548
 ],
 "us": {
  "x": 0.8857779534203334,
  "y": 1.77698931347127,
  "th": -0.33906776789422494,
  "v": -1,
  "w": 0.31883081793785095,
  "vx": -0.7155428046019544,
  "vy": 0.25778038971336237,
  "on_stage": true,
  "edge_distance": 0.18577795342033343
 },
 "them": {
  "x": 2.3085861940383015,
  "y": 2.55533328143976,
  "th": -2.336643645292422,
  "v": 0.9,
  "w": 0.8183555111425018,
  "vx": -0.4247850296341523,
  "vy": -0.45296981333441505,
  "on_stage": true,
  "edge_distance": 0.5446667185602401
 },
 "blocks": [
  {
   "name": "增益块",
   "kind": "Buff",
   "x": 0.6771,
   "y": 1.839,
   "out": true,
   "last_contact_role": "simultaneous",
   "contacts": [
    {
     "r": "us",
     "t": 0.01
    },
    {
     "r": "us",
     "t": 0.025
    },
    {
     "r": "us",
     "t": 0.03
    },
    {
     "r": "us",
     "t": 0.035
    },
    {
     "r": "us",
     "t": 0.045
    },
    {
     "r": "us",
     "t": 0.045
    },
    {
     "r": "us",
     "t": 0.05
    },
    {
     "r": "us",
     "t": 0.05
    }
   ]
  },
  {
   "name": "增益块",
   "kind": "Buff",
   "x": 2.1429,
   "y": 2.2949,
   "out": false,
   "last_contact_role": "simultaneous",
   "contacts": []
  },
  {
   "name": "减益块",
   "kind": "Debuff",
   "x": 1.6,
   "y": 2.4,
   "out": false,
   "last_contact_role": "",
   "contacts": []
  }
 ],
 "events": [
  {
   "seq": 38,
   "tick": 317,
   "kind": "BlockOff",
   "role": "us",
   "is_us": true,
   "neutral": false,
   "block": "增益块",
   "reason": "simultaneous"
  }
 ]
}
```

## seed 6048 / policy / tick 260

同一缺陷：我方距块 0.223 m，对手距块 2.164 m，max 接触时刻 4 条记录全为 us

- 轨迹文件: `traces/final_holdout_v2-policy-6048.jsonl.gz`（不入 Git）

```json
{
 "tick": 260,
 "action": [
  -1.0,
  0.18402476608753204
 ],
 "us": {
  "x": 0.8726583623959434,
  "y": 1.648463640981288,
  "th": -0.5850087822073868,
  "v": -1,
  "w": 0.3680495321750641,
  "vx": -0.5492962838341635,
  "vy": 0.3065903593399347,
  "on_stage": false,
  "edge_distance": 0.17265836239594345
 },
 "them": {
  "x": 2.6343642019387414,
  "y": 2.7335058229888785,
  "th": -2.578966094139855,
  "v": 0.9,
  "w": 0.7259832123227201,
  "vx": -0.06738960462688902,
  "vy": -0.044944195008766015,
  "on_stage": true,
  "edge_distance": 0.36649417701112164
 },
 "blocks": [
  {
   "name": "增益块",
   "kind": "Buff",
   "x": 0.6922,
   "y": 1.779,
   "out": true,
   "last_contact_role": "simultaneous",
   "contacts": [
    {
     "r": "us",
     "t": 0.005
    },
    {
     "r": "us",
     "t": 0.01
    },
    {
     "r": "us",
     "t": 0.015
    },
    {
     "r": "us",
     "t": 0.015
    },
    {
     "r": "us",
     "t": 0.02
    },
    {
     "r": "us",
     "t": 0.025
    },
    {
     "r": "us",
     "t": 0.025
    },
    {
     "r": "us",
     "t": 0.03
    },
    {
     "r": "us",
     "t": 0.035
    },
    {
     "r": "us",
     "t": 0.04
    },
    {
     "r": "us",
     "t": 0.045
    },
    {
     "r": "us",
     "t": 0.045
    },
    {
     "r": "us",
     "t": 0.045
    },
    {
     "r": "us",
     "t": 0.05
    },
    {
     "r": "us",
     "t": 0.05
    },
    {
     "r": "us",
     "t": 0.05
    },
    {
     "r": "us",
     "t": 0.05
    }
   ]
  },
  {
   "name": "增益块",
   "kind": "Buff",
   "x": 2.3647,
   "y": 2.4675,
   "out": false,
   "last_contact_role": "them",
   "contacts": []
  },
  {
   "name": "减益块",
   "kind": "Debuff",
   "x": 1.6,
   "y": 2.4,
   "out": false,
   "last_contact_role": "",
   "contacts": []
  }
 ],
 "events": [
  {
   "seq": 19,
   "tick": 260,
   "kind": "BlockOff",
   "role": "us",
   "is_us": true,
   "neutral": false,
   "block": "增益块",
   "reason": "simultaneous"
  },
  {
   "seq": 20,
   "tick": 260,
   "kind": "Drop",
   "role": "us",
   "is_us": true,
   "neutral": false,
   "block": "",
   "reason": ""
  }
 ]
}
```

## seed 4005 / fsm / tick 349

第一轮 FSM 基线的锁定目标被我方推出界，同样记为 simultaneous → FSM 丢失 +3

- 轨迹文件: `traces/legacy_round1-fsm-4005.jsonl.gz`（不入 Git）

```json
{
 "tick": 349,
 "action": null,
 "us": {
  "x": 2.8153513855932366,
  "y": 2.652950301717676,
  "th": 0.5754240398343129,
  "v": 0.35,
  "w": 0.07324484999132141,
  "vx": 0.31908455201803604,
  "vy": 0.2076685063664037,
  "on_stage": true,
  "edge_distance": 0.2846486144067635
 },
 "them": {
  "x": 1.0046244322594207,
  "y": 1.6323059981824573,
  "th": -2.2929869848922424,
  "v": 0.35,
  "w": 0.0295493137241587,
  "vx": -0.0898169990939787,
  "vy": -0.10210859572940535,
  "on_stage": true,
  "edge_distance": 0.3046244322594207
 },
 "blocks": [
  {
   "name": "增益块",
   "kind": "Buff",
   "x": 3.1073,
   "y": 2.8514,
   "out": true,
   "last_contact_role": "simultaneous",
   "contacts": []
  },
  {
   "name": "增益块",
   "kind": "Buff",
   "x": 0.7625,
   "y": 1.3528,
   "out": false,
   "last_contact_role": "them",
   "contacts": []
  },
  {
   "name": "减益块",
   "kind": "Debuff",
   "x": 1.5384,
   "y": 2.4069,
   "out": false,
   "last_contact_role": "them",
   "contacts": []
  }
 ],
 "events": [
  {
   "seq": 19,
   "tick": 349,
   "kind": "BlockOff",
   "role": "us",
   "is_us": true,
   "neutral": false,
   "block": "增益块",
   "reason": "simultaneous"
  }
 ]
}
```

## seed 6001 / policy / tick 292

对照组：本 tick 共 10 条接触记录，但 max 接触时刻只有 1 条 → 归属成功并计分

- 轨迹文件: `traces/final_holdout_v2-policy-6001.jsonl.gz`（不入 Git）

```json
{
 "tick": 292,
 "action": [
  -1.0,
  0.08431270718574524
 ],
 "us": {
  "x": 0.9080291920869805,
  "y": 1.44352962433437,
  "th": -0.07943063218649288,
  "v": -1,
  "w": 0.16862541437149048,
  "vx": -0.5568415347996117,
  "vy": 0.10519071333070752,
  "on_stage": true,
  "edge_distance": 0.20802919208698056
 },
 "them": {
  "x": 1.417141187649921,
  "y": 1.6309774718345182,
  "th": -2.4150372094733066,
  "v": 0.9,
  "w": 0.22891521600921028,
  "vx": -0.6156381113994444,
  "vy": -0.546929775001372,
  "on_stage": true,
  "edge_distance": 0.717141187649921
 },
 "blocks": [
  {
   "name": "增益块",
   "kind": "Buff",
   "x": 0.691,
   "y": 1.4743,
   "out": true,
   "last_contact_role": "us",
   "contacts": [
    {
     "r": "us",
     "t": 0.005
    },
    {
     "r": "us",
     "t": 0.005
    },
    {
     "r": "us",
     "t": 0.01
    },
    {
     "r": "us",
     "t": 0.015
    },
    {
     "r": "us",
     "t": 0.02
    },
    {
     "r": "us",
     "t": 0.03
    },
    {
     "r": "us",
     "t": 0.035
    },
    {
     "r": "us",
     "t": 0.04
    },
    {
     "r": "us",
     "t": 0.045
    },
    {
     "r": "us",
     "t": 0.05
    }
   ]
  },
  {
   "name": "增益块",
   "kind": "Buff",
   "x": 1.2461,
   "y": 1.4586,
   "out": false,
   "last_contact_role": "simultaneous",
   "contacts": [
    {
     "r": "them",
     "t": 0.005
    },
    {
     "r": "them",
     "t": 0.005
    },
    {
     "r": "them",
     "t": 0.01
    },
    {
     "r": "them",
     "t": 0.01
    },
    {
     "r": "them",
     "t": 0.015
    },
    {
     "r": "them",
     "t": 0.02
    },
    {
     "r": "them",
     "t": 0.025
    },
    {
     "r": "them",
     "t": 0.025
    },
    {
     "r": "them",
     "t": 0.03
    },
    {
     "r": "them",
     "t": 0.03
    },
    {
     "r": "them",
     "t": 0.035
    },
    {
     "r": "them",
     "t": 0.035
    },
    {
     "r": "them",
     "t": 0.04
    },
    {
     "r": "them",
     "t": 0.04
    },
    {
     "r": "them",
     "t": 0.045
    },
    {
     "r": "them",
     "t": 0.045
    },
    {
     "r": "them",
     "t": 0.05
    },
    {
     "r": "them",
     "t": 0.05
    }
   ]
  },
  {
   "name": "减益块",
   "kind": "Debuff",
   "x": 1.6,
   "y": 2.4,
   "out": false,
   "last_contact_role": "",
   "contacts": []
  }
 ],
 "events": [
  {
   "seq": 19,
   "tick": 292,
   "kind": "BlockScore",
   "role": "us",
   "is_us": true,
   "neutral": false,
   "block": "增益块",
   "reason": ""
  }
 ]
}
```

