# MuJoCo Windows x64 dependency lock

Checked on 2026-09-24 against the [official release](https://github.com/google-deepmind/mujoco/releases/tag/3.14.0) and its [GitHub release API](https://api.github.com/repos/google-deepmind/mujoco/releases/latest).

| Item | Locked value |
|---|---|
| Release | `3.14.0`, published 2026-09-22 |
| Target archive | `mujoco-3.14.0-windows-x86_64.zip` |
| Download | `https://github.com/google-deepmind/mujoco/releases/download/3.14.0/mujoco-3.14.0-windows-x86_64.zip` |
| Archive size | 22,109,742 bytes |
| Archive SHA-256 | `d2a786e0ea48667cee60b7eb3c50cc3addd7ae0f22137d71d66ca52888e4aaf0` (release API `assets[].digest`) |
| `bin/mujoco.dll` SHA-256 | `da487aed0d534fc52b1612a09571f9f2836b8abb9f46567ab5868620ca0e7418` (extracted from the verified archive) |
| Engine license | [Apache-2.0](https://github.com/google-deepmind/mujoco/blob/3.14.0/LICENSE) |

The `.zip.sha256` release asset has its **own** SHA-256 digest; it is not the archive digest above. The archive was downloaded and hashed locally on 2026-09-25. `bin/mujoco.dll`, `LICENSE`, and `THIRD_PARTY_NOTICES.txt` were extracted into `src/Sim.Mujoco/runtimes/win-x64/native/` and copied into the CLI output by project reference. A clean-machine publish/load check is still required.

The adapter will target the official [MuJoCo C API](https://mujoco.readthedocs.io/en/stable/programming/index.html) through a narrow version-pinned interop layer. The upstream C# package is for Unity integration; this project does not need a Unity dependency.
