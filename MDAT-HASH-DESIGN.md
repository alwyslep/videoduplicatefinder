# mdat-hash relink — Design

> Fork feature for `alwyslep/videoduplicatefinder` (branch `add-korean-localization`).
> Sibling of `CONTENT-TABLE-DESIGN.md` and `TOMBSTONE-DESIGN.md`.
> Status: **DESIGN — agreed to build (2026-07-24).** This reverses the earlier "measure-first → likely
> WONTFIX" call, which was wrong: measurement showed a **real, large loss** (see below).

## Why this exists — the incident

VDF's move/rename reuse (`TryRelinkMovedFile`) is keyed on **whole-file oshash** (`size + first/last 64 KiB
checksum`). That key is **container-sensitive**: any byte change — including a **metadata-only re-embed** —
changes it.

On 2026-07-22/23 a metadata re-tag batch (the DTI canonicalize add-on, `fast_embed` appending a moov)
grew ~2,400 files on drive F by a **median of ~1,068 bytes** (99.8 % under 16 MB, **zero re-encodes**).
Every one of those files:

1. changed size → oshash changed,
2. was also renamed (quality-suffix), so the **path** changed too,
3. → relink's `(size, oshash)` lookup missed (both parts of the key moved),
4. → the old analysed entry was pruned as a relocated-orphan and the fresh entry had **no analysis**,
5. → VDF re-decoded from scratch: **2,451 files (F: 2,404, D: 47) lost their frame + audio fingerprints.**

The fingerprints were **still valid** the whole time — audio/frame fingerprints are computed from the
**decoded stream**, which a container/metadata change does not touch. They were discarded purely because
the *identity key* (oshash) is coupled to the container. (One-time recovery from a pre-loss backup by
filename-stem match already restored them via `dbprobe remap` — see `RemapCmd.cs`. This design prevents
recurrence.)

**The invariant we want:** *reuse analysis whenever the media streams are unchanged, regardless of the
container* — moov edits, tag writes, cover embed, faststart relocation, rename, or move.

## The three keys, and where mdat-hash sits

`CONTENT-TABLE-DESIGN.md` established the non-collapsible key model. mdat-hash **refines the content key
for MP4**; it does not add a tier.

| Key | Question | Stable across… |
|-----|----------|----------------|
| **path** | which file / where | — (changes on move/rename; that's what relink repairs) |
| **oshash** (whole-file) | same exact *bytes* | rename/move; **NOT** metadata edits (this is the hole) |
| **mdat-hash** (this doc) | same *media stream* (MP4) | rename/move **and** metadata/container edits |
| **phash / grayBytes** | perceptually same (re-encodes) | re-encodes; the actual duplicate-detection key |

mdat-hash is strictly stronger than oshash **for relink of MP4-family files**: it survives everything
oshash survives, plus container edits. It is **not** a replacement for phash (a re-encode changes the
mdat too — correctly forcing re-analysis) and **not** a content-dedup tier (that was measured to zero
payoff and rejected in `CONTENT-TABLE-DESIGN.md`).

## What mdat-hash is

The `mdat` box holds the raw (still-compressed) audio+video sample data. A metadata write only touches
`moov`/`udta`/`free`; the `mdat` **payload bytes are byte-identical**. So hash the payload, not the file:

```
mdat-hash = mdat_payload_length
          + LE-u64 checksum of the first 64 KiB of the mdat PAYLOAD
          + LE-u64 checksum of the last  64 KiB of the mdat PAYLOAD
```

Same shape/cost as oshash (~128 KiB I/O + a few KiB of header parse), but taken from the mdat byte range
instead of the whole file. Found by a **real top-level box walk** — not "last 64 KiB of the file" —
because our library has both layouts (measured):

- `fast_embed` output: `ftyp → free → mdat → moov(end)` — **mdat is NOT the last box**.
- faststart output: `ftyp → moov(front) → mdat` — mdat last, and **>4 GB uses 64-bit `largesize`** (hdr=16).

`MdatHashUtils.TryCompute(path)` walks boxes (handling 32-bit `size`, `size==1` 64-bit largesize,
`size==0` extends-to-EOF), locates the **first** `mdat`, and hashes its payload range. Returns **null**
(→ caller falls back to oshash, never a wrong match) when: not MP4-family, no `mdat`, more than one
`mdat` (fragmented/unusual), payload < 64 KiB, or any parse/IO error.

## FileEntry change

Additive, MemoryPack `VersionTolerant`, nullable — same pattern that introduced `OsHash`:

```
[MemoryPackOrder(13)] public string? MdatHash;   // null = not computed / non-MP4 / unparseable
```

Old DBs load unchanged (new field defaults null). No migration/rewrite. Backfilled during scan.

## The relink change (the core win)

Today (`BuildFileList`): a path-miss is checked against a `Dictionary<long,List<FileEntry>>` **keyed by
FileSize**, then disambiguated by oshash (`TryRelinkMovedFile`). A metadata edit changes the size, so the
file lands in the wrong size-bucket → miss. **Both** the size bucket and the oshash must become
container-invariant.

**New:** build a second index for MP4 gone-candidates **keyed by MdatHash**:

```
relinkByMdat : Dictionary<string /*MdatHash*/, List<FileEntry>>
```

For a path-miss on an MP4 file:
1. compute the new file's MdatHash;
2. look up gone entries (recorded path no longer exists) with the same MdatHash;
3. **exactly one** candidate → relink (re-key path, refresh size/date/oshash/mdathash, keep
   grayBytes/mediaInfo/PHashes/AudioFingerprint);
4. 0 or >1 → fall through to the existing size+oshash relink, then to "new file" (never guess — same
   *ambiguous ⇒ don't merge* rule the fork already follows).

Non-MP4 files (MdatHash null) use the unchanged size+oshash path. So this is purely additive: MP4 gains
metadata-resilience; everything else behaves exactly as before.

## The same-path guard (`ScanEngine.cs:915`)

The re-embed-in-place case (path unchanged, size changed) currently discards analysis unconditionally —
oshash isn't even consulted. Add a guard **before** the discard:

```
else if (fEntry.FileSize != dbEntry.FileSize) {
    string? md = MdatHashUtils.TryCompute(fEntry.Path);
    if (md != null && dbEntry.MdatHash != null && md == dbEntry.MdatHash) {
        // container grew/shrank but the media stream is identical -> keep analysis, refresh identity
        dbEntry.FileSize = fEntry.FileSize; dbEntry.DateModified = fEntry.DateModified;
        dbEntry.DateCreated = fEntry.DateCreated; dbEntry.OsHash = OsHashUtils.TryCompute(fEntry.Path);
        dbEntry.MdatHash = md;
    } else { /* genuine change or non-MP4/parse-fail -> existing discard+re-decode */ }
}
```

This is the branch that the (now-understood-as-insufficient) `[REDECODE-MEASURE]` counter was watching;
it can be removed or folded into a "reused via mdat-hash" log line once this ships.

## Backfill

In `GatherInfos`, where oshash is computed/backfilled for in-scope entries, also compute `MdatHash` for
MP4-family entries whose `MdatHash == null` (one ~128 KiB read; piggybacks on the same file open where
practical). Same drive-ready / offline-skip rules as the oshash backfill. Existing entries (incl. the
just-recovered F set, which have analysis but null MdatHash) gain it on the next scan — no re-decode.

## Hazards

- **H1 box-walk robustness.** 64-bit largesize, mdat-not-last, extends-to-EOF, fragmented (multiple mdat),
  `ftyp/free/moov` orderings. Handle the common cases; **any** uncertainty → return null → oshash
  fallback. A wrong mdat range must never produce a *matching* hash for different content.
- **H2 collision (weak hash).** Two different streams sharing one mdat-hash would attach the wrong
  analysis. Same exposure oshash already has, mitigated the same way: the hash **includes mdat length**,
  and relink treats **>1 candidate as ambiguous → no merge**. (Optional hardening: verify one sampled
  frame before binding — the CONTENT-TABLE H2 guard — if ever needed.)
- **H3 non-MP4.** mkv/avi/ts/wmv have no mdat → MdatHash null → whole-file oshash relink (unchanged). MP4
  is the container that our embed pipeline mutates, so this covers the actual failure population.
- **H4 mdat rewrite.** faststart relocation moves moov but leaves mdat payload bytes intact → hash stable
  (good, reuse). A real re-encode rewrites mdat → hash changes → re-decode (correct — the streams *did*
  change; phash catches perceptual dupes separately).
- **H5 migration.** None. Additive nullable field, backfilled during scan; old DB loads as-is; backup on
  first rewrite as usual.

## Stages (each independently shippable)

0. **This doc + agreement.** ← here.
1. **`MdatHashUtils` + `FileEntry.MdatHash` + backfill (read-mostly).** Nothing depends on it yet. Ship,
   then verify on real data: re-embed a scanned file, confirm its MdatHash is unchanged while oshash and
   size changed. Lowest risk.
2. **Wire into relink (`relinkByMdat`).** The real win — rename + metadata edit now reuses analysis.
   Requires the H1 walker + H2 ambiguity guard. Regression test = the F scenario (analyse → embed →
   rename → scan ⇒ 0 re-decode).
3. **Same-path `:915` guard.** In-place re-embed keeps analysis. Remove/repurpose `[REDECODE-MEASURE]`.
4. **Cleanup.** Consolidate logging ("reused via mdat-hash: N"); document the MP4-vs-fallback split.

Stop after any stage; each leaves the app correct. Stage 1 is pure addition; stages 2–3 are where the
relink behavior changes and the hazards live.

## Non-goals (scope fences)

- **Not** replacing oshash (kept for non-MP4 and same-exact-copy questions).
- **Not** a content-dedup / oshash-keyed storage tier (measured 0 payoff — `CONTENT-TABLE-DESIGN.md`).
- **Not** replacing phash duplicate detection (re-encodes legitimately change the mdat).
- **Not** changing the DTI/canonicalize add-on — embedding *should* change the container; the fix is
  making VDF's identity key respect the media stream, exactly as the user's scripts already assume.

## Rollback

Additive field + additive relink index. To disable: skip the `relinkByMdat` lookup and the `:915` guard;
MdatHash becomes dead but harmless. No on-disk format change to revert.
