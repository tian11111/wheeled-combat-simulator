# 同步收尾提交到 GitHub

## Goal

核对本地 main 超前提交、远端状态和最终验证结果，按项目规则完成安全同步。

## Confirmed Facts

- 当前分支是 `main`，工作区干净，本地比 `origin/main` 超前 7 个提交。
- 项目约定：若确需新分支，名称必须使用 `test/` 前缀；本任务默认不新建分支。

## Requirements

- R1：同步前检查工作区、HEAD、origin URL、ahead/behind 数量和前三项验收结果。
- R2：优先将已确认的 `main` 提交推送到 `origin/main`；若远端发生分叉，停止并报告，不强制覆盖远端。
- R3：推送后用远端 ref 和本地 HEAD 做一致性校验，并确认工作区仍干净。
- R4：推送失败时记录具体原因，不绕过审批、不执行 force push。

## Acceptance Criteria

- [x] `origin/main` 已快进到已验收提交，且推送后 ref 与本地 HEAD 一致、ahead/behind 为 `0 0`。
- [x] 推送后工作区检查为干净（收尾记录提交完成后再复核）。
- [x] 推送和校验命令结果已记录在任务日志中。

## Out Of Scope

- 不修改提交历史、不 force push、不删除远端分支。
- 不在同步任务中混入未验收的产品代码。
