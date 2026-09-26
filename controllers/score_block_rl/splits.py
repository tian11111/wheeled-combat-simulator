#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Pre-registered evaluation seed splits for the SCORE_BLOCK PPO pilot.

Single source of truth for `train.py`, `evaluate.py` and `selftest.py`, so the
seed lists and the mutual-exclusion rules cannot drift between scripts.

Split history (see the parent pilot report, which must not be rewritten):

* ``legacy_development`` (3001-3010) was already used for v1/v2 reward debugging.
* ``legacy_final_holdout`` (4001-4010) was the previous round's pre-registered
  blind holdout. It was revealed and its AC4 gate failed, so it can never serve
  as a blind set again. ``--final-holdout`` keeps pointing at *this* split only.
* ``development_v2`` (5001-5020) is this round's model-selection set.
* ``final_holdout_v2`` (6001-6050) is this round's one-shot blind set.

Every named split is disjoint from the training episode pool and from every
other split; :func:`assert_registry_is_disjoint` checks that invariant.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable, Optional, Sequence

SPLIT_VERSION = "score-block-split-v2"

LEGACY_DEVELOPMENT = "legacy_development"
LEGACY_FINAL_HOLDOUT = "legacy_final_holdout"
DEVELOPMENT_V2 = "development_v2"
FINAL_HOLDOUT_V2 = "final_holdout_v2"
EXPLORATORY = "exploratory"

#: Named splits and their exact, pre-registered seed lists.
SPLIT_SEEDS: dict[str, list[int]] = {
    LEGACY_DEVELOPMENT: list(range(3001, 3011)),
    LEGACY_FINAL_HOLDOUT: list(range(4001, 4011)),
    DEVELOPMENT_V2: list(range(5001, 5021)),
    FINAL_HOLDOUT_V2: list(range(6001, 6051)),
}
NAMED_SPLITS: tuple[str, ...] = tuple(SPLIT_SEEDS)
BLIND_SPLITS: tuple[str, ...] = (LEGACY_FINAL_HOLDOUT, FINAL_HOLDOUT_V2)

#: Episode seed pool used by ``train.py``: fixed seed 42 plus the inclusive
#: range 1000-1999. The SB3 RNG seed / first reset seed (20260925) is reserved
#: as well.
TRAIN_SEED_POOL_FIXED = [42]
TRAIN_SEED_POOL_RANGE = [1000, 1999]
TRAIN_EPISODE_SEEDS = frozenset({42, 20260925, *range(1000, 2000)})

#: Seeds already revealed by earlier rounds. Never usable as a blind set.
HISTORICAL_SPLIT_SEEDS = frozenset(
    SPLIT_SEEDS[LEGACY_DEVELOPMENT] + SPLIT_SEEDS[LEGACY_FINAL_HOLDOUT])
REVEALED_SEEDS = frozenset(TRAIN_EPISODE_SEEDS | HISTORICAL_SPLIT_SEEDS)

#: Custom exploratory lists must be at least this long and fully distinct.
MIN_CUSTOM_SEEDS = 10

#: Human-readable usage labels; they end up in every evaluation JSON.
SPLIT_USAGE: dict[str, str] = {
    LEGACY_DEVELOPMENT: "historical development set (already revealed); comparison only",
    LEGACY_FINAL_HOLDOUT: "historical final holdout (already revealed); --final-holdout semantics",
    DEVELOPMENT_V2: "current-round model-selection development set",
    FINAL_HOLDOUT_V2: "current-round one-shot blind holdout; requires a freeze record",
    EXPLORATORY: "caller-supplied exploratory seeds; never gate evidence",
}


class SplitError(ValueError):
    """Raised when a requested evaluation selection violates the split contract."""


@dataclass(frozen=True)
class Selection:
    """Resolved evaluation selection."""

    split: str
    seeds: list[int]
    split_version: str = SPLIT_VERSION

    @property
    def is_exploratory(self) -> bool:
        return self.split == EXPLORATORY

    @property
    def is_blind_holdout(self) -> bool:
        """True only for a pre-registered blind holdout, never for custom seeds."""
        return self.split in BLIND_SPLITS

    @property
    def usage(self) -> str:
        return SPLIT_USAGE[self.split]

    def manifest(self) -> dict:
        return {
            "split_version": self.split_version,
            "evaluation_split": self.split,
            "evaluation_split_usage": self.usage,
            "seed_count": len(self.seeds),
            "seeds": list(self.seeds),
            "is_exploratory": self.is_exploratory,
            "is_blind_holdout": self.is_blind_holdout,
        }


def seeds_for(split: str) -> list[int]:
    try:
        return list(SPLIT_SEEDS[split])
    except KeyError as exc:
        raise SplitError(
            f"unknown split {split!r}; expected one of {', '.join(NAMED_SPLITS)}, {EXPLORATORY}"
        ) from exc


def classify_seeds(seeds: Iterable[int]) -> str:
    """Return the named split matching ``seeds`` exactly, else ``exploratory``."""
    values = sorted(int(seed) for seed in seeds)
    for split, registered in SPLIT_SEEDS.items():
        if values == sorted(registered):
            return split
    return EXPLORATORY


def assert_registry_is_disjoint() -> None:
    """Validate the pre-registered disjointness invariant (used by selftest)."""
    seen: dict[int, str] = {}
    for split, seeds in SPLIT_SEEDS.items():
        if len(seeds) != len(set(seeds)):
            raise SplitError(f"split {split} contains duplicate seeds")
        if len(seeds) < MIN_CUSTOM_SEEDS:
            raise SplitError(f"split {split} has fewer than {MIN_CUSTOM_SEEDS} seeds")
        for seed in seeds:
            if seed in seen:
                raise SplitError(f"seed {seed} appears in both {seen[seed]} and {split}")
            seen[seed] = split
    collisions = sorted(set(seen) & TRAIN_EPISODE_SEEDS)
    if collisions:
        raise SplitError(f"named splits overlap the training episode pool: {collisions}")


def resolve_selection(
    split: Optional[str] = None,
    custom_seeds: Optional[Sequence[int]] = None,
    final_holdout: bool = False,
) -> Selection:
    """Route CLI arguments to a validated :class:`Selection`.

    Rejects split mixing (``--final-holdout`` with ``--split``/``--seeds``,
    ``--split`` with ``--seeds``) and any seed overlap with the training pool
    or with a named split.
    """
    if final_holdout and split is not None:
        raise SplitError("--final-holdout cannot be combined with --split")
    if final_holdout and custom_seeds is not None:
        raise SplitError("--final-holdout cannot be combined with custom --seeds")
    if final_holdout:
        split = LEGACY_FINAL_HOLDOUT

    if custom_seeds is not None:
        if split not in (None, EXPLORATORY):
            raise SplitError(f"--seeds cannot be combined with --split {split}")
        seeds = _parse_custom_seeds(custom_seeds)
        return Selection(split=EXPLORATORY, seeds=seeds)

    if split is None:
        # This round's default is the *new* development split. The previous
        # default (3001-3010) is still reachable via --split legacy_development.
        split = DEVELOPMENT_V2
    seeds = seeds_for(split)
    if len(seeds) < MIN_CUSTOM_SEEDS:
        raise SplitError(f"split {split} has fewer than {MIN_CUSTOM_SEEDS} seeds")
    if len(set(seeds)) != len(seeds):
        raise SplitError(f"split {split} contains duplicate seeds")
    return Selection(split=split, seeds=seeds)


def _parse_custom_seeds(custom_seeds: Sequence[int]) -> list[int]:
    seeds: list[int] = []
    for item in custom_seeds:
        try:
            seeds.append(int(item))
        except (TypeError, ValueError) as exc:
            raise SplitError(f"custom seeds must be integers: {item!r} ({exc})") from exc
    if len(seeds) < MIN_CUSTOM_SEEDS:
        raise SplitError(f"custom --seeds must contain at least {MIN_CUSTOM_SEEDS} distinct seeds")
    if len(set(seeds)) != len(seeds):
        raise SplitError("custom --seeds must not repeat a seed value")
    train_overlap = sorted(set(seeds) & TRAIN_EPISODE_SEEDS)
    if train_overlap:
        raise SplitError(f"evaluation seeds overlap the training episode pool: {train_overlap}")
    split_membership: dict[int, str] = {}
    for name, registered in SPLIT_SEEDS.items():
        for seed in registered:
            split_membership[seed] = name
    reused = sorted(seed for seed in seeds if seed in split_membership)
    if reused:
        owners = sorted({split_membership[seed] for seed in reused})
        raise SplitError(
            f"custom --seeds cannot reuse pre-registered split seeds {reused} "
            f"(owned by {', '.join(owners)}); custom results are exploratory only"
        )
    return seeds


def training_pool_manifest() -> dict:
    """JSON-friendly description of the training episode seed pool."""
    return {
        "split_version": SPLIT_VERSION,
        "fixed": list(TRAIN_SEED_POOL_FIXED),
        "inclusive_range": list(TRAIN_SEED_POOL_RANGE),
        "initial_sb3_reset_episode_seed": 20260925,
    }
