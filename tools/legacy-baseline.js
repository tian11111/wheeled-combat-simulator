#!/usr/bin/env node
/* ============================================================================
 * legacy-baseline.js — regenerate regression baselines from the OLD prototype.
 *
 * The old prototype at D:/project/robocup/robot-simulator is a read-only
 * behavior reference. This script loads its deterministic CORE (the first
 * <script> block of wushu_ring_sim.html, exactly like sim_selftest.js does)
 * and runs a fixed set of scenarios, writing JSON baselines that the .NET
 * kernel (Sim.Core) must reproduce.
 *
 * Usage:
 *   node tools/legacy-baseline.js [path-to-old-wushu_ring_sim.html]
 *
 * Baselines are written to src/Sim.Tests/fixtures/. Never edit them by hand —
 * regenerate with this script only, and only when an intentional rule change
 * was reviewed (see PORTING_NOTES.md).
 * ========================================================================== */
'use strict';
const fs = require('fs');
const path = require('path');

const htmlPath = path.resolve(
  process.argv[2] || 'D:/project/robocup/robot-simulator/wushu_ring_sim.html'
);
const html = fs.readFileSync(htmlPath, 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
if (!script.includes('CORE-BEGIN')) { console.error('CORE block not found'); process.exit(2); }

global.document = { getElementById: () => ({ addEventListener: () => {} }), addEventListener: () => {} };
global.window = { addEventListener: () => {} };
try { global.navigator = {}; } catch (e) { /* node >=21 exposes a readonly navigator */ }

eval(script + `
;global.__T = {
  params, US, THEM, robots, blocks, buffs, deb,
  arm, resetAll, stepSim, stepSimExt, getState, getLog,
  setPoseFor, scenePreset, onPlatform, onStage, hangOn,
  restartFor, pauseMatch, resumeMatch, beginPreparation,
};`);
const T = global.__T;

const outDir = path.resolve(__dirname, '..', 'src', 'Sim.Tests', 'fixtures');
fs.mkdirSync(outDir, { recursive: true });

// ---------- helpers ----------
function eventRecorder() {
  const events = [];
  let cursor = 0;
  return {
    take() {
      for (const e of T.getLog()) {
        if (Number(e.seq) > cursor) { events.push({ seq: e.seq, t: e.t, cls: e.cls, msg: e.msg }); cursor = Number(e.seq); }
      }
    },
    events,
  };
}
function compactFinalState() {
  const st = T.getState();
  return {
    scores: st.scores,
    done: st.done,
    doneReason: st.doneReason,
    match: { phase: st.match.phase, restartPenalties: st.match.restartPenalties },
    robots: {
      us: pick(st.robots.us),
      them: pick(st.robots.them),
    },
    sensors: st.sensors,
    rawSensors: st.rawSensors,
    objects: st.objects,
  };
  function pick(r) {
    return {
      x: r.x, y: r.y, th: r.th, v: r.v, w: r.w, vx: r.vx, vy: r.vy,
      state: r.state, action: r.action, onPlatform: r.onPlatform, hang: r.hang,
      isStalled: r.isStalled, wedgedFront: r.wedgedFront,
      timer: r.timer, simT: r.simT,
    };
  }
}
function trackStageTransitions(steps, dt, acts) {
  // Derived structured events (mount/drop) that the old core only exposes as
  // FSM log lines for FSM-driven robots; for scripted robots we poll the
  // onStage() predicate, which is exactly what the new referee events use.
  const transitions = [];
  const rec = eventRecorder();
  let wasUs = T.onStage(T.US), wasThem = T.onStage(T.THEM);
  for (let i = 0; i < steps; i++) {
    T.stepSimExt(dt, acts);
    const nowUs = T.onStage(T.US), nowThem = T.onStage(T.THEM);
    if (!wasUs && nowUs) transitions.push({ step: i + 1, role: 'us', kind: 'mount' });
    if (wasUs && !nowUs) transitions.push({ step: i + 1, role: 'us', kind: 'drop' });
    if (!wasThem && nowThem) transitions.push({ step: i + 1, role: 'them', kind: 'mount' });
    if (wasThem && !nowThem) transitions.push({ step: i + 1, role: 'them', kind: 'drop' });
    wasUs = nowUs; wasThem = nowThem;
    rec.take();
  }
  return { transitions, events: rec.events };
}

// ---------- baseline A: scripted push-off / drop / inactivity (seed 42) ----------
function baselinePushOff() {
  T.resetAll({ seed: 42 });
  T.buffs[0].x = 2.5; T.buffs[0].y = 1.9; T.buffs[0].vx = 0; T.buffs[0].vy = 0;
  T.buffs[1].x = 1.35; T.buffs[1].y = 1.35; T.buffs[1].vx = 0; T.buffs[1].vy = 0;
  T.deb.x = 1.6; T.deb.y = 2.4; T.deb.vx = 0; T.deb.vy = 0;
  T.setPoseFor(T.THEM, 1.9, 2.5, 0);
  T.setPoseFor(T.US, 2.1, 1.9, 0);
  const steps = 300; // 15 s
  const { transitions, events } = trackStageTransitions(steps, 0.05, { us: { v: 1, w: 0 }, them: { v: 0, w: 0 } });
  return {
    scenario: 'legacy-pushoff-seed42',
    seed: 42,
    steps,
    dt: 0.05,
    scriptedActions: { us: { v: 1, w: 0 }, them: { v: 0, w: 0 } },
    stageTransitions: transitions,
    events,
    final: compactFinalState(),
  };
}

// ---------- baseline B: full FSM battle, seed 21 (selftest scenario 33 family) ----------
function baselineFsmBattle() {
  T.resetAll({ seed: 21 });
  T.arm();
  const rec = eventRecorder();
  const transitions = [];
  let wasUs = T.onStage(T.US), wasThem = T.onStage(T.THEM);
  let steps = 0;
  for (let i = 0; i < 2400; i++) {
    T.stepSimExt(0.05, { us: null, them: null });
    steps = i + 1;
    const nowUs = T.onStage(T.US), nowThem = T.onStage(T.THEM);
    if (!wasUs && nowUs) transitions.push({ step: steps, role: 'us', kind: 'mount' });
    if (wasUs && !nowUs) transitions.push({ step: steps, role: 'us', kind: 'drop' });
    if (!wasThem && nowThem) transitions.push({ step: steps, role: 'them', kind: 'mount' });
    if (wasThem && !nowThem) transitions.push({ step: steps, role: 'them', kind: 'drop' });
    wasUs = nowUs; wasThem = nowThem;
    rec.take();
    if (T.getState().done) break;
  }
  return {
    scenario: 'legacy-fsm-seed21',
    seed: 21,
    steps,
    dt: 0.05,
    stageTransitions: transitions,
    // FSM battles are chaotic under 1-ulp trig differences between JS and .NET;
    // the strict assertions are final scores + score-class event sequence.
    events: rec.events,
    final: compactFinalState(),
  };
}

// ---------- baseline C: referee commands (restart penalties, pause/resume) ----------
function baselineReferee() {
  T.resetAll({ seed: 7 });
  T.arm();
  T.stepSimExt(0.05, { us: null, them: null });
  T.restartFor('us', 'debug');
  T.restartFor('them', 'restart');
  const before = T.getState().match.timer;
  T.pauseMatch('baseline');
  T.stepSimExt(1.0, { us: null, them: null });
  const frozen = T.getState().match.timer === before && T.getState().match.phase === 'PAUSED';
  T.resumeMatch();
  T.stepSimExt(0.05, { us: null, them: null });
  const rec = eventRecorder(); rec.take();
  return {
    scenario: 'legacy-referee-seed7',
    seed: 7,
    frozenWhilePaused: frozen,
    events: rec.events,
    final: compactFinalState(),
  };
}

// ---------- run + determinism sanity (each baseline twice, must be identical) ----------
function stable(fn) {
  const a = JSON.stringify(fn());
  const b = JSON.stringify(fn());
  if (a !== b) { console.error('BASELINE NOT DETERMINISTIC IN OLD CORE'); process.exit(3); }
  return JSON.parse(a);
}

const baselines = {
  'legacy-pushoff-seed42.json': stable(baselinePushOff),
  'legacy-fsm-seed21.json': stable(baselineFsmBattle),
  'legacy-referee-seed7.json': stable(baselineReferee),
};
for (const [name, data] of Object.entries(baselines)) {
  fs.writeFileSync(path.join(outDir, name), JSON.stringify(data, null, 1) + '\n');
  console.log(`wrote ${name}: steps=${data.steps} scores=${JSON.stringify(data.final.scores)} events=${(data.events || []).length}`);
}
console.log('done');
