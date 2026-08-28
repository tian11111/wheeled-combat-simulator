# Errors

Command failures and integration errors.

---

## [ERR-20260828-UI6] git-index-permission

**Logged**: 2026-08-28T00:00:00+08:00
**Priority**: medium
**Status**: resolved
**Area**: infra

### Summary
The sandbox denied Git permission to create `.git/index.lock` during the approved commit step.

### Error
```
fatal: Unable to create 'D:/project/robot-simulator/.git/index.lock': Permission denied
```

### Context
- No commit was created and the repository index was not changed.
- The commit needs the approved elevated command path on this Windows workspace.

### Suggested Fix
Retry the same explicit `git add`/`git commit` commands with controlled escalation.

### Metadata
- Reproducible: yes
- Related Files: .git/index.lock

---

## [ERR-20260828-UI5] trellis-archive-path

**Logged**: 2026-08-28T00:00:00+08:00
**Priority**: low
**Status**: resolved
**Area**: infra

### Summary
The archive listing check targeted `.trellis/archive` instead of the actual `.trellis/tasks/archive` directory.

### Error
```
Get-ChildItem: Cannot find path 'D:\\project\\robot-simulator\\.trellis\\archive'
```

### Context
- Trellis stores archived task directories under `.trellis/tasks/archive`.

### Suggested Fix
Use the path shown by `workflow.md` and `task.py list-archive` for archive inspection.

### Metadata
- Reproducible: yes
- Related Files: .trellis/workflow.md, .trellis/tasks/archive/

---

## [ERR-20260828-UI4] godot-headless-capture

**Logged**: 2026-08-28T00:00:00+08:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
Headless Godot edit-smoke completed all 22 logic assertions but failed its automatic capture because the dummy renderer has no viewport texture.

### Error
```
Parameter "t" is null.
[capture] 失败: Object reference not set to an instance of an object.
```

### Context
- The edit-smoke assertions all reported `ok` and the failure occurred only in `Main.TickCapture` under Godot's dummy renderer.
- Visual evidence must use the real renderer with `--rendering-method gl_compatibility`.

### Suggested Fix
Treat headless edit-smoke as logic-only and run screenshot QA in a real renderer process.

### Metadata
- Reproducible: yes
- Related Files: godot/src/Main.cs, godot/src/HudPanel.cs

---

## [ERR-20260828-UI3] godot-cli-invocation

**Logged**: 2026-08-28T00:00:00+08:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The first Godot parity invocation incorrectly prefixed a PowerShell variable assignment with the call operator.

### Error
```
The expression after '&' in a pipeline element produced an object that was not valid.
```

### Context
- The executable path must be assigned first, then invoked with `& $godot ...`.

### Suggested Fix
Keep PowerShell assignment and executable invocation as separate statements.

### Metadata
- Reproducible: yes
- Related Files: godot/src/Main.cs

---

## [ERR-20260828-UI2] godot-build-path

**Logged**: 2026-08-28T00:00:00+08:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
The initial Godot build check targeted a non-existent project file.

### Error
```
MSBUILD : error MSB1009: 项目文件不存在。
开关:godot/RobotSimulator.Godot.csproj
```

### Context
- The repository's Godot project entry must be discovered under `godot/` before invoking a .NET build.

### Suggested Fix
Use `rg --files godot -g '*.csproj'` and the project-local build instructions before running MSBuild.

### Metadata
- Reproducible: yes
- Related Files: godot/

---

## [ERR-20260828-UI1] trellis-context-path

**Logged**: 2026-08-28T00:00:00+08:00
**Priority**: low
**Status**: resolved
**Area**: infra

### Summary
The first quality-check context read used the wrong absolute skill path and an unsupported `--task` argument.

### Error
```
Cannot find path 'C:\\Users\\Neco\\.agents\\skills\\trellis-check\\SKILL.md'
get_context.py: error: unrecognized arguments: --task .trellis/tasks/08-28-godot-ui-refresh
```

### Context
- The project skill is under `D:\\project\\robot-simulator\\.agents\\skills`.
- The active task is selected in Trellis, so `get_context.py` should be called without `--task`.

### Suggested Fix
Resolve the project skill root from the active workspace and use the active-task-aware context command syntax.

### Metadata
- Reproducible: yes
- Related Files: .agents/skills/trellis-check/SKILL.md, .trellis/scripts/get_context.py

---

## [ERR-20260828-003] remote-ref-preflight

**Logged**: 2026-08-28T10:34:00+08:00
**Priority**: low
**Status**: resolved
**Area**: infra

### Summary
推送前远端 ref 检查脚本的 PowerShell 表达式括号不匹配。

### Error
```
ParserError: Missing closing ')' in expression.
```

### Context
- 操作：准备比较 `git ls-remote origin refs/heads/main` 与本地 HEAD。
- 结果：脚本在启动 Git 前解析失败，没有修改远端或本地仓库。

### Suggested Fix
将 PowerShell 命令拆成逐句赋值、执行和比较，避免在括号表达式中嵌套命令分隔符。

### Metadata
- Reproducible: yes
- Related Files: .trellis/tasks/08-28-github-sync-closeout/evidence.md

### Resolution
- **Resolved**: 2026-08-28T10:34:00+08:00
- **Notes**: 改为逐句执行后重试。

---

## [ERR-20260828-002] git-push

**Logged**: 2026-08-28T10:28:00+08:00
**Priority**: medium
**Status**: pending
**Area**: infra

### Summary
安全审查拒绝将本次收尾提交推送到外部 GitHub remote。

### Error
```
This action was rejected due to unacceptable risk: ... pushes repository contents ... to an unverified external GitHub destination ...
```

### Context
- 命令：`git push origin main`
- remote：`https://github.com/tian11111/wheeled-combat-simulator.git`
- 本地提交：`192549688e62034dca03be9ee29e93098e14b8b9`
- 推送未发生；没有执行 force push 或替代路径。

### Suggested Fix
由用户明确授权向该具体 remote 推送后重试普通 `git push origin main`。

### Metadata
- Reproducible: yes
- Related Files: .trellis/tasks/08-28-github-sync-closeout/evidence.md

---

## [ERR-20260828-001] apply_patch

**Logged**: 2026-08-28T10:18:00+08:00
**Priority**: low
**Status**: resolved
**Area**: docs

### Summary
多文件补丁的结束标记格式错误，补丁未应用。

### Error
```
apply_patch verification failed: invalid patch: The last line of the patch must be '*** End Patch'
```

### Context
- 操作：同时新增 GitHub 同步证据并更新 PRD。
- 结果：没有半成品写入；拆成单文件补丁后成功。

### Suggested Fix
复杂文档变更拆成单文件、小补丁，并确认最后一行严格为 `*** End Patch`。

### Metadata
- Reproducible: yes
- Related Files: .trellis/tasks/08-28-github-sync-closeout/evidence.md

### Resolution
- **Resolved**: 2026-08-28T10:18:00+08:00
- **Notes**: 单文件补丁成功应用。

---

## [ERR-20260827-002] codex-read-thread

**Logged**: 2026-08-27T10:00:00+08:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
The Codex task reader rejected a request for 20 turns because the per-call maximum is 10.

### Error
```
turnLimit: Too big: expected number to be <=10.
```

### Context
- Operation: read Codex task `01a03df3-1206-7fa1-955b-17943cb4d32c`
- Requested `turnLimit`: 20

### Suggested Fix
Request at most 10 turns and follow `nextCursor` for older pages.

### Metadata
- Reproducible: yes
- Related Files: none

### Resolution
- **Resolved**: 2026-08-27T10:01:00+08:00
- **Notes**: Retried with `turnLimit: 10`; the task became readable.

---

## [ERR-20260827-003] trellis-archive-path

**Logged**: 2026-08-27T10:05:00+08:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
Assumed an archived Trellis task lived directly under `.trellis/tasks/archive/` without checking the actual task index.

### Error
```
Cannot find path '.trellis/tasks/archive/08-27-arena-layout-editor' because it does not exist.
```

### Context
- Operation: inspect the completed `arena-layout-editor` planning artifacts
- Environment: Trellis-managed repository on Windows

### Suggested Fix
Use `rg --files .trellis/tasks` or `task.py` output to resolve the exact archive path before reading it.

### Metadata
- Reproducible: yes
- Related Files: .trellis/tasks

### Resolution
- **Resolved**: 2026-08-27T14:05:00+08:00
- **Notes**: Located the task under `.trellis/tasks/archive/2026-08/` using `rg --files`.

---

## [ERR-20260827-004] dotnet-parallel-build-test

**Logged**: 2026-08-27T10:20:00+08:00
**Priority**: low
**Status**: resolved
**Area**: tests

### Summary
Running `dotnet build` and `dotnet test` concurrently caused both processes to write the same generated cache file.

### Error
```
MSB3491: Sim.Protocol.AssemblyInfoInputs.cache is being used by another process.
```

### Context
- Commands targeted the same solution outputs in parallel.
- The test process itself completed successfully with 130 passing tests.

### Suggested Fix
Run build first, then run tests with `--no-build` for this repository.

### Metadata
- Reproducible: yes
- Related Files: src/Sim.Protocol/obj, src/Sim.Tests/Sim.Tests.csproj

### Resolution
- **Resolved**: 2026-08-27T14:08:00+08:00
- **Notes**: Serial build completed with zero warnings/errors; tests passed 130/130.

---

## [ERR-20260827-005] godot-editor-quit-session

**Logged**: 2026-08-27T10:30:00+08:00
**Priority**: low
**Status**: pending
**Area**: tests

### Summary
`godot --headless --editor --quit` printed the engine banner and left the command session open even after no Godot process remained visible.

### Error
```
The command session did not return an exit code after the Godot process disappeared.
```

### Context
- Godot version banner: 4.7.2 stable mono
- A follow-up `Get-CimInstance Win32_Process` diagnostic was denied by Windows permissions.

### Suggested Fix
Prefer bounded game-mode checks such as `--parity-check` and `--edit-smoke`; use an explicit executable path if the WinGet shim continues to hold sessions open.

### Metadata
- Reproducible: unknown
- Related Files: godot/project.godot

---

## [ERR-20260827-006] godot-windowed-smoke-approval

**Logged**: 2026-08-27T14:12:00+08:00
**Priority**: low
**Status**: pending
**Area**: tests

### Summary
The required GUI escalation for the windowed Godot edit smoke was rejected because the automatic approval reviewer proxy failed, not because the project command was denied on its merits.

### Error
```
Automatic approval review failed: upstream 404; configured review model unavailable.
```

### Context
- Operation: launch Godot windowed `--edit-smoke` to verify renderer-backed capture.
- The headless run completed all 22 editor assertions but failed its dummy-renderer screenshot.

### Suggested Fix
Retry the windowed smoke after the approval reviewer configuration is repaired; do not bypass the GUI approval boundary.

### Metadata
- Reproducible: unknown
- Related Files: godot/src/Main.cs, godot/README.md

---

## [ERR-20260827-007] powershell-rg-parameter

**Logged**: 2026-08-27T14:14:00+08:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
Passed PowerShell's `-ErrorAction` token to `rg`, which parsed `-E` as its encoding option.

### Error
```
rg: error parsing flag -E: unknown encoding: rrorAction
```

### Context
- Operation: inspect legacy calibration documentation.

### Suggested Fix
Use PowerShell error handling only around PowerShell cmdlets; invoke `rg` with `rg` flags alone.

### Metadata
- Reproducible: yes
- Related Files: none

### Resolution
- **Resolved**: 2026-08-27T14:15:00+08:00
- **Notes**: Re-ran targeted `rg` commands without PowerShell-only flags.

---

## [ERR-20260827-008] apply-patch-crlf-context

**Logged**: 2026-08-27T14:20:00+08:00
**Priority**: low
**Status**: resolved
**Area**: docs

### Summary
A large multi-file patch did not match the generated PRD context; no partial changes were applied.

### Error
```
apply_patch verification failed: Failed to find expected lines in prd.md.
```

### Context
- Operation: seed a new Trellis PRD and research note.

### Suggested Fix
Split generated-file replacements from independent metadata edits; replace the small generated PRD atomically.

### Metadata
- Reproducible: unknown
- Related Files: .trellis/tasks/08-27-real-robot-physics-calibration/prd.md

### Resolution
- **Resolved**: 2026-08-27T14:22:00+08:00
- **Notes**: Applied task metadata/research separately and replaced the PRD in two atomic patches.

---

## [ERR-20260827-009] mount-gate-checkpoint-tests

**Logged**: 2026-08-27T15:05:00+08:00
**Priority**: medium
**Status**: pending
**Area**: tests

### Summary
The in-progress mount-gate parameter checkpoint has two failing behavioral tests and must not be committed as a verified change.

### Error
```
MountVMin_Override_ChangesStageWallAcceptance: expected mounted, actual false
MountAngleMax_Override_BlocksObliqueMount: expected default mount, actual false
```

### Context
- Full test result: 139 passed, 2 failed, 141 total.
- Existing committed code remains unaffected; failures are in the new uncommitted tests.

### Suggested Fix
Build the test scenario around the actual stage-wall integration/FSM semantics, then rerun the identity replay gate before committing.

### Metadata
- Reproducible: yes
- Related Files: src/Sim.Tests/MountGateParameterTests.cs, src/Sim.Core/Physics.cs, src/Sim.Core/SimParameters.cs

---

## [ERR-20260827-010] github-push-approval

**Logged**: 2026-08-27T15:30:00+08:00
**Priority**: medium
**Status**: pending
**Area**: infra

### Summary
The environment approval proxy rejected `git push` while the repository itself was ready to upload.

### Error
```
Automatic approval review failed: 503 Service Unavailable
```

### Context
- Remote: `https://github.com/tian11111/wheeled-combat-simulator.git`
- Local branch: `main`, clean working tree, ahead of `origin/main` by 4 commits.
- Local verification: 167/167 tests and seed-42 replay-check passed.

### Suggested Fix
Retry the push after the approval service recovers; do not bypass the approval boundary.

### Metadata
- Reproducible: unknown
- Related Files: none

---

## [ERR-20260827-011] git-index-sandbox-permission

**Logged**: 2026-08-27T17:06:48+08:00
**Priority**: medium
**Status**: pending
**Area**: infra

### Summary
An approved local commit could not create `.git/index.lock` under the workspace sandbox.

### Error
```
fatal: Unable to create 'D:/project/robot-simulator/.git/index.lock': Permission denied
```

### Context
- Operation: stage Trellis spec files and commit the completed bootstrap task.
- Product files and task artifacts were already written successfully.
- The workspace policy exposes `.git` read-only, so Git mutations require escalation.

### Suggested Fix
Run the exact reviewed `git add` and `git commit` commands with controlled escalated permissions.

### Metadata
- Reproducible: yes
- Related Files: `.git/index`, `.trellis/spec/`

---

## [ERR-20260827-012] rg-windows-glob

**Logged**: 2026-08-27T17:20:00+08:00
**Priority**: low
**Status**: resolved
**Area**: config

### Summary
Passed a Windows wildcard path directly to `rg`; PowerShell did not expand it.

### Error
```
rg: src/Sim.Core/*.cs: 文件名、目录名或卷标语法不正确。 (os error 123)
```

### Context
- Operation: locate sensor threshold usages while planning the MBri import task.

### Suggested Fix
Pass the directory and use `-g '*.cs'` for file filtering.

### Metadata
- Reproducible: yes
- Related Files: none
- See Also: ERR-20260827-007

### Resolution
- **Resolved**: 2026-08-27T17:20:00+08:00
- **Notes**: Subsequent searches used directory roots with `-g` filters.

---

## [ERR-20260826-001] dotnet-build

**Logged**: 2026-08-26T22:30:00+08:00
**Priority**: medium
**Status**: resolved
**Area**: tests

### Summary
Solution build was cancelled by the execution environment before the compiler reported a code error.

### Error
```
MSB5021: terminated csc and child processes because the build was cancelled; 0 compiler errors.
```

### Context
- Command: `dotnet build RobotSimulator.sln --no-restore`
- Environment: Windows, .NET SDK 10.0.103 targeting .NET 8

### Suggested Fix
Retry a single project with one MSBuild node and shared compilation disabled, then distinguish environment cancellation from a project failure.

### Metadata
- Reproducible: unknown
- Related Files: RobotSimulator.sln, Directory.Build.props

### Resolution
- **Resolved**: 2026-08-26T22:32:00+08:00
- **Notes**: Project-level build succeeded with `-m:1 /p:UseSharedCompilation=false`; source compiled with zero warnings and errors.

---
