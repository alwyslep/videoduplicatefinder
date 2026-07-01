# Rejected-Fingerprint (Tombstone) Design

> Fork feature for `alwyslep/videoduplicatefinder` (branch `add-korean-localization`).
> Goal: the DB is a **permanent memory of every unique video content the user has curated**,
> so a re-downloaded copy of something already deleted is recognized and flagged — the user
> never re-accumulates junk they already threw away.

## Core model (agreed)

The DB accumulates **one fingerprint per unique content**. Two distinct deletion cases:

| Case | What is deleted | DB fingerprint | Action |
|------|-----------------|----------------|--------|
| **Redundant duplicate** (a surviving copy remains) | the video file | survivor keeps it | **remove** the deleted copy's entry (already done: commit `f8e3541`, GridPlayer; and `#2` checked-delete) |
| **Unique / last copy** | the video file | **must be kept** as a *tombstone* | keep fingerprint, only the file goes |

Deleting a duplicate is fine because the unique fingerprint survives via the kept copy.
Deleting the last copy keeps the fingerprint so a future re-download still matches.

## Tombstone detection — drive-presence heuristic (agreed)

A tombstone is **not** a stored flag driven by intercepting deletes (external Explorer/Opus
deletes can't be intercepted). It is **computed at scan/compare time** from the file's state:

```
if (!File.Exists(entry.Path)) {
    root = Path.GetPathRoot(entry.Path);          // "H:\"
    if (DriveInfo(root).IsReady)                  // drive mounted, file gone
        -> TOMBSTONE  (intentionally deleted): keep fingerprint, badge, auto-check matches
    else                                          // drive absent (USB unplugged, letter changed)
        -> OFFLINE    (preserve entry, NO auto-check, "offline" state)
}
```

This distinguishes an intentional external delete (drive present, file gone) from a
temporarily unmounted drive (H:/I: USB pulled) **without** a flag and without intercepting
external deletes. UNC / unresolvable root -> treat as OFFLINE (conservative: no auto-check).

Rationale: this machine's H:/I: are external USB (see memory `usb-disk-fleet-health`); a
File.Exists-only rule would mislabel a whole unplugged drive as "deleted" and auto-target its
still-valid re-downloads for deletion. The drive check prevents that.

## Retained tombstone data (agreed)

**phash + one representative thumbnail.** Matching is done by phash (exact enough, tiny —
~25 ulong per content). One thumbnail is kept so the compare list can *show* "this is what you
deleted." Full grayBytes frame set is trimmed to 1 on tombstone-ification to bound DB growth.

- Matching a tombstone therefore relies on pHash comparison -> `UsePHashing` must be effective
  for tombstones even if the visual (grayBytes) path is the user's default.

## Automation on match (agreed)

When a compare group contains a tombstone member:
- the **live** members of that group (the fresh re-downloads) are **auto-checked** for deletion,
- the tombstone row shows an **"이미 삭제함 / already deleted"** badge,
- **actual deletion still requires the user's confirm button** (never auto-delete — the drive
  heuristic is not infallible; an offline-window false positive must be catchable at confirm).

## Settings changes (agreed)

- `IncludeNonExistingFiles` default **ON** (tombstones must participate in comparison).
- **"사라진 항목 정리" (CleanupDatabase) disabled/hidden** — it deletes non-existent entries,
  which directly destroys tombstones and contradicts the append-only-of-fingerprints goal.
  (It was already only a manual menu action.)

## Implementation stages

1. **Core detection + retention**
   - `IsTombstone(entry)` / `IsOffline(entry)` helpers (drive-presence heuristic).
   - BuildFileList already never removes non-existent entries — keep it that way.
   - Compare path (`InvalidEntryForDuplicateCheck`, `InvalidEntry`) must keep tombstones valid
     and included; offline entries preserved but excluded from auto-check.
   - Neuter `CleanupDatabase` so it never removes tombstones.
2. **GUI**
   - `DuplicateItemVM.IsTombstone` / `IsOffline`.
   - "이미 삭제함" badge in the compare list.
   - Auto-check live members of any group containing a tombstone.
3. **Bloat control**
   - Trim grayBytes to a single representative thumbnail when a tombstone is first detected;
     keep phash. Ensure pHash matching stays effective.
4. **Settings defaults**
   - `IncludeNonExistingFiles` default ON; hide "사라진 항목 정리".

## Already shipped (this line of work)

- `faefdde` — append-only #1: same-size timestamp change keeps phash (oshash-verified).
- `f8e3541` — GridPlayer redundant-duplicate delete drops the deleted copy's entry
  (correct per the model above; the surviving copy keeps the unique fingerprint).

## Open / deferred

- Dedupe multiple tombstones of the same content into one (minor; noise not correctness).
- Deploy (VDF.Core.dll / VDF.GUI.dll swap) is intentionally deferred until all DB-related work
  lands; VDF must be closed to swap the locked DLLs.
