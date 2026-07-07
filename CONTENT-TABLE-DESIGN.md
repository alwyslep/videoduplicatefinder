# Content-Table (oshash overlay) Design

> Fork feature for `alwyslep/videoduplicatefinder` (branch `add-korean-localization`).
> Sibling of `TOMBSTONE-DESIGN.md` — read that first; tombstones become one column here.
> Status: **NOT IMPLEMENTED — closed after measurement (2026-07-07).** Before writing any schema,
> `dbprobe --content` simulated this tier on the live 19,611-row DB. The payoff is **zero**:
> **0 identical-copy groups, ~0 MB frame de-dup, 0 oshash collisions, 5 live null-oshash files**
> (18,957 distinct oshashes / 18,957 hashed rows — every content is unique). VDF's find-and-delete
> workflow already keeps the DB at ~1 row per unique content, so a content tier has nothing to
> de-duplicate and Stage 3 (its only net-new value, and its only real risk) buys nothing. The ad-hoc
> oshash overlay the fork already has — relink, missing/orphan accounting, survivor archive, and the
> not-a-match blacklist — is the right and sufficient amount of content-addressing for this workload.
> The one remaining path-fragile "same content" spot, the thumbnail-strip cache, was switched to an
> oshash key separately (`DuplicateItemVM`). **The design below is retained as the record of *why not*.**
>
> Original intent (for context): agree the model, schema, migration, and collision/null hazards
> before any implementation.

## Why this exists

"Should everything be keyed on oshash instead of path?" — No, and the precise reason drives the
whole design. There are **three keys answering three different questions**, and they cannot be
collapsed:

| Key | Question | Nature | Can it be dropped? |
|-----|----------|--------|--------------------|
| **path** | "which file / where" | 1:1 to a file, always present | No — you must name a location to open/delete/play a file, and to tell GridPlayer which file to launch. |
| **oshash** | "same exact bytes / did it move" | 1:**N** to files, nullable, weak hash | No — but this is the right key for every "same content?" question. |
| **phash / grayBytes** | "perceptually the same (re-encodes)" | perceptual fingerprint | No — oshash **cannot** do this; a re-encode has a different oshash. Duplicate detection lives here. |

oshash (`OsHashUtils.TryCompute`: file size + LE-u64 checksum of the first & last 64 KiB) is a cheap
**exact-copy / move** identity. It is `null` for files < 64 KiB, locked, or out of the include
scope, and it is **not unique** — every byte-identical copy shares one oshash (that is the point).
So oshash **cannot be a primary key** for a per-file row, and it **cannot replace phash** for
duplicate detection. What it *can* do is anchor a **content overlay**: a second tier, keyed by
content, that the file tier references.

Today that overlay already exists but is scattered ad-hoc across four stores. This design makes it
one explicit, consistently oshash-keyed tier.

## Current state — content identity is scattered

| Store | Shape | What it holds about *content* |
|-------|-------|-------------------------------|
| `ScannedFiles.db` (MemoryPack, **path-keyed** `HashSet<FileEntry>`) | per-file rows | `OsHash` as a nullable field; `grayBytes`/`PHashes`/`mediaInfo` **duplicated on every copy** |
| `BlacklistedGroups.json` (`List<HashSet<string>>`) | not-a-match sets | paths **and** `oshash:` tokens intermixed (the hybrid just shipped) |
| `SurvivorThumbs.sqlite` (**oshash PK**) | survivor thumbnails | one JPEG per unique content |
| scan-time transient (`HashSet<string> liveOsHashes`) | rebuilt each scan | live-content set for missing-count & orphan prune |

The same question — *"have I seen / deleted / decided-on this content?"* — is answered in four
different places with four different encodings of oshash (field, token, PK, transient set).

## Target model — two tiers

Keep MemoryPack for the core DB (no SQLite migration of the main store). Extend the wrapper:

```
DatabaseWrapper {
    HashSet<FileEntry> Files;              // Tier 1, path-keyed
    Dictionary<string, ContentRecord> Contents;   // Tier 2, oshash-keyed
}
```

**Tier 1 — File (path key):** only what is genuinely per-file.
```
FileEntry { Path (PK), FileSize, DateCreated, Flags, ContentKey (oshash | null) }
```

**Tier 2 — Content (oshash key):** what is really about the bytes, deduped across copies.
```
ContentRecord {
    OsHash (PK), mediaInfo, grayBytes, PHashes,   // same bytes ⇒ same media & frames (see collision hazard)
    Rejected (bool),                              // tombstone flag (replaces the drive-heuristic-per-path judgement of "deleted content")
    FirstSeen, LastSeen
}
```
Survivor thumbnails **stay** in `SurvivorThumbs.sqlite` (already oshash-keyed) rather than inlining
the JPEG blob into the in-memory `Contents` dict — inlining would load every thumbnail into RAM on
every launch, which is exactly what that separate lazy store avoids. So the physical stores go
**4 → 3**, but all three are now consistently oshash-keyed; the win is conceptual consistency +
structural move-resilience, not fewer files.

not-a-match blacklist becomes `List<HashSet<oshash>>` (content relations), with a path-set fallback
sub-entry only for null-oshash members (today's hybrid, but now the exception rather than the rule).

## Feature-by-feature: before → after

| Feature | Today (ad-hoc) | With content tier |
|---------|----------------|-------------------|
| Move / rename | relink logic + oshash re-bolted onto each store | Tier-1 `Path` changes; the `ContentRecord` and everything hanging off it (Rejected, survivor, blacklist membership) is untouched — **move-resilience is structural, not per-feature code** |
| Identical-copy dedup | `grayBytes`/`PHashes` stored on every copy | N file rows → 1 content row; **frames computed & stored once** |
| Tombstone | file-gone path row + drive heuristic | zero file rows + `Content.Rejected` — explicit, not inferred |
| not-a-match blacklist | path + `oshash:` token JSON | `HashSet<oshash>` relation, path only for the null bucket |
| Survivor thumbnail | separate sqlite | referenced by the content row's oshash (store unchanged) |
| missing-count / orphan-prune | rebuild `liveOsHashes` each scan | a query: "does this content have any file row on a mounted drive?" |

## ★ Hazards and how each is handled (the crux of the decision)

**H1 — Live-DB migration (560 MB MemoryPack).**
Version-tolerant loader detects old (`HashSet<FileEntry>` with inline fields) vs new (two-tier).
One-time upgrade: group non-null `OsHash` → build `ContentRecord`s (take frames/media from one
representative, set `Rejected` from current tombstone state, collapse identical copies); null-oshash
files become self-contained (see H3). Old file preserved as `ScannedFiles.db.pre-content-bak`.
Reversible. Never runs mid-scan.

**H2 — oshash is not truly unique; a collision now has teeth.**
Today a collision (two different contents, same oshash) is harmless because `grayBytes` lives on the
path row, so each keeps its own frames. **Moving frames onto a shared content row means a collision
attaches the wrong frames and merges two contents.** Mitigation, matching the existing relink
philosophy (*ambiguous → don't merge*): before binding a file to an existing content row, verify a
cheap secondary discriminator (exact byte length is already in oshash; add e.g. one sampled-frame
hash compare). On mismatch, treat as a distinct content (path-singleton), never a silent merge.
**This guard is mandatory and is the riskiest code in the project.**

**H3 — null-oshash / sub-64 KiB / out-of-scope files need a home.**
These cannot join a content row. Represented as **self-contained files**: the `FileEntry` keeps an
inline fallback copy of `mediaInfo`/`grayBytes`/`PHashes`, used only when `ContentKey == null`. Ugly
but bounded, and it keeps every downstream read uniform (resolve content → row, else inline).

**H4 — exact vs perceptual boundary (explicit non-goal).**
The content tier is an **exact-content ledger**. It does **not** subsume perceptual matching:
duplicate detection (`CheckIfDuplicate`, `ScanEngine.cs:2000`) stays on phash, and a **re-encoded**
re-download of rejected content is still caught only by the phash path forming a group with the
tombstone's frames — not by an oshash content-row hit. The unification covers exact copies, moves,
survivor archive, not-a-match, and missing/orphan accounting. It does not touch VDF's core job.

## Explicit non-goals (scope fences)

- **Not** replacing phash/grayBytes duplicate detection.
- **Not** changing the GridPlayer↔VDF sidecar IPC — it stays path-based (the player must be told
  which file to open; a content key can't name a file).
- **Not** changing hardlink identity — that is OS file-object (inode / file-id), neither path nor
  content.
- **Not** changing the tombstone/offline *drive-presence* heuristic — it needs a path's drive root.
- **Not** migrating the core DB to SQLite.

## Implementation stages (each independently shippable & testable)

0. **This design doc + agreement.** ← we are here.
1. **Additive content index (read-mostly).** Introduce `ContentRecord` + `FileEntry.ContentKey`;
   build/maintain `Contents` as a *derived* index during scan. Nothing depends on it yet. Lowest
   risk — pure addition, old reads untouched. Ship + verify the index matches reality.
2. **Route content-questions to the tier.** Tombstone flag, missing-count, orphan-prune, and the
   blacklist read from `Contents`; blacklist store → `HashSet<oshash>`. Still dual-storing frames.
3. **Move frame/media ownership to Content (the real migration).** Files reference; identical copies
   dedup their frames. Requires H1 migration + H2 collision guard + H3 null bucket. Only after 1–2
   are proven in daily use.
4. **Cleanup.** Fold transient `liveOsHashes` into content queries; tidy survivor-thumb reference.

Stop after any stage: each leaves the app correct. Stage 1–2 deliver most of the *consistency* win
with none of H1–H3 risk; stage 3 is where the storage-dedup payoff and the danger both live.

## Open decisions (need your call before stage 3)

1. **Stop line.** Is the goal stages 1–2 (consistency + oshash-keyed blacklist, low risk) or the full
   1–4 including frame-dedup migration (H1–H3)? Stage 3's benefit is mostly disk/RAM savings on
   identical copies.
2. **H2 secondary discriminator.** Accept the collision risk as negligible for real videos (simpler),
   or add the mandatory sampled-frame verify before any content merge (safer, more code)?
3. **H3 null-bucket shape.** Inline-fallback fields on `FileEntry` (chosen above) vs a degenerate
   path-keyed content record. Cosmetic but pins the read path.

## Rollback

Every stage keeps the previous on-disk format loadable and backs up before rewrite
(`*.pre-content-bak`). Stage 3's migration is the only irreversible-in-place step and is gated on a
verified backup + a dry-run count that reconciles file-rows ↔ content-rows before committing.
