# GitHub 同步技术设计

## Boundary

This is a final release operation over the already reviewed local history. It does not alter commits or mix in unverified product changes.

## Protocol

Compare HEAD, origin/main, and ahead/behind counts. If the remote is a strict ancestor, push the current main; if it has diverged, stop for review. Verify the remote ref equals local HEAD after a successful push.

## Safety

Do not force-push, rewrite history, delete branches, or create a new branch unless required. If a branch is required, use the project-mandated test/ prefix.
