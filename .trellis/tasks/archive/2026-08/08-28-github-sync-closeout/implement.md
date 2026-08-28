# GitHub 同步执行计划

## Ordered Checklist

1. Confirm the first three child tasks have evidence and the final working tree is clean.
2. Read the remote ref and compare it with local HEAD; stop on divergence.
3. Push main only when the remote is a strict ancestor.
4. Re-read the remote ref and verify origin/main equals HEAD, zero ahead/behind, and a clean working tree.

## Validation Commands

    git status --short --branch
    git rev-list --left-right --count origin/main...HEAD
    git push origin main
    git rev-list --left-right --count origin/main...HEAD
    git status --short --branch

## Rollback Points

- If push approval or network access fails, keep the local commits and report the exact failure.
- Never use git push --force for this task.
