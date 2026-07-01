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

### Two deletion intents (must be handled differently)

- **(A) Duplicate deletion — happens ONLY inside VDF.** You delete redundant copies of a
  comparison group while keeping at least one (the survivor). The deleted copies' entries are
  **removed**; the survivor already carries the content's fingerprint, so no tombstone is made.
- **(B) Content rejection — everything else.** You reject the content itself: an external
  delete (Explorer/Opus), or a VDF delete that removes the WHOLE group (GridPlayer `Ctrl+DEL`,
  or checking every item in a group). Here **exactly one fingerprint is kept as a tombstone**,
  even though every video file is gone, so a re-download of that rejected content is caught.

Because duplicate-judgment only ever happens in VDF, any deletion that leaves no surviving copy
is treated as rejection. Whole-group VDF deletes keep the **last** member as the tombstone
(GridPlayer: `ApplyExternalRemoval`; checked-delete: `DeleteInternal` via `keepByGroup==null`);
partial deletes (a live survivor remains) drop every deleted entry. An unmounted drive is never
a deletion — it is offline (fingerprint kept, never auto-targeted).

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

1. **Core detection + retention** — DONE (`2d3ac31`)
   - `ScanEngine.IsDriveReady` / `PathIsTombstone` / `PathIsOffline` (drive-presence heuristic).
   - GatherInfos guards analysis with `File.Exists` — never ffprobe/ffmpeg a missing path. This
     is what makes `IncludeNonExistingFiles=ON` safe (no more ffprobe-on-missing errors).
   - `CleanupDatabase` neutered to a logged no-op so it never destroys tombstones.
2. **GUI** — DONE (`1348f00`)
   - `DuplicateItemVM.IsTombstone` / `IsOffline` (computed from the heuristic).
   - "이미 삭제함 / Already deleted" and "오프라인 / Offline" badges in the compare list Path column.
   - `AutoCheckTombstoneMatches` pre-checks live members of any group containing a tombstone.
3. **Bloat control** — SKIPPED (deliberate)
   - Trimming grayBytes would drop a tombstone below `InvalidEntryForDuplicateCheck`'s
     `grayBytes.Count >= ThumbnailCount` gate, so it would be excluded from comparison and never
     match a re-download — i.e. it breaks the feature. The saving is only a few KB per file, so
     tombstones keep their full grayBytes (already computed; matching stays robust). Known ceiling:
     revisit only if the DB genuinely bloats, and then also relax the compare gate for tombstones.
4. **Settings defaults** — DONE (`8857ba0`)
   - `IncludeNonExistingFiles` default ON; "사라진 항목 정리" menu item hidden (`IsVisible="False"`).

## Already shipped (this line of work)

- `faefdde` — append-only #1: same-size timestamp change keeps phash (oshash-verified).
- `f8e3541` — GridPlayer redundant-duplicate delete drops the deleted copy's entry
  (correct per the model above; the surviving copy keeps the unique fingerprint).

## Open / deferred

- Dedupe multiple tombstones of the same content into one (minor; noise not correctness).
- Deploy (VDF.Core.dll / VDF.GUI.dll swap) is intentionally deferred until all DB-related work
  lands; VDF must be closed to swap the locked DLLs.
