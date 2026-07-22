// Replicates the fixed keeper-protection logic (prototype isCut/tog/migrateCut + bridge doReclaim
// collection) and checks the exact catastrophic scenarios the attack workflow confirmed.
let cutState = {};
let _undo = [];
function isCut(f) { if (f.keep) return false; return (f.p in cutState) ? cutState[f.p] : true; }
function tog(f) { if (!f || f.keep) return; cutState[f.p] = !isCut(f); }
function migrateCut(oldP, newP) {   // matches index.html migrateCut (case-insensitive delete-all-then-assign)
  const lc = oldP.toLowerCase();
  function rekey(m) {
    let has = false, val;
    for (const k of Object.keys(m)) if (k.toLowerCase() === lc) { if (!has || k === oldP) { val = m[k]; has = true; } delete m[k]; }
    if (has) m[newP] = val;
  }
  rekey(cutState);
  _undo.forEach(rekey);
}
// doReclaim's collection (paths = checked non-keepers; keepers = all keeper paths)
function collect(GROUPS) {
  const paths = [], keepers = [];
  GROUPS.forEach(g => g.files.forEach(f => { if (f.keep) keepers.push(f.p); else if (isCut(f)) paths.push(f.p); }));
  return { paths, keepers };
}
let pass = 0, fail = 0;
function check(name, cond) { cond ? pass++ : fail++; console.log((cond ? "PASS" : "FAIL"), name); }

// Scenario finding#1/#4: priority swap leaves a stale cut on what becomes the keeper.
cutState = {};
let A = { p: "G:\\clip.2160p.mkv", keep: 1 }, B = { p: "G:\\clip.1080p.big.mkv", keep: 0 };
// user double-toggles B (deliberation) -> explicit cutState[B]=true
tog(B); tog(B);
check("B explicitly cut before swap", cutState[B.p] === true);
// re-compare: size priority promotes B to keeper, A demoted. cutState unchanged (stale).
A = { p: "G:\\clip.2160p.mkv", keep: 0 }; B = { p: "G:\\clip.1080p.big.mkv", keep: 1 };
let r = collect([{ files: [A, B] }]);
check("keeper B NOT in delete set despite stale cutState", r.paths.indexOf(B.p) < 0);
check("keeper B listed as keeper", r.keepers.indexOf(B.p) >= 0);
check("demoted A IS in delete set", r.paths.indexOf(A.p) >= 0);

// Scenario finding#5/#8: single click on a keeper checkbox must not stage it.
cutState = {};
let K = { p: "D:\\keep.mkv", keep: 1 };
tog(K);   // click keeper
check("tog on keeper is a no-op (isCut false)", isCut(K) === false);
check("keeper never collected after click", collect([{ files: [K, { p: "D:\\dup.mkv", keep: 0 }] }]).paths.indexOf(K.p) < 0);

// Scenario finding#10: mpvGrid rename must carry the user's uncheck with the file.
cutState = {};
let D = { p: "F:\\a_old.mkv", keep: 0 };
tog(D);   // user UNchecks D to keep it -> cutState[old]=false
check("D unchecked (protected) before rename", isCut(D) === false);
migrateCut("F:\\a_old.mkv", "F:\\a_new.mkv"); D.p = "F:\\a_new.mkv";
check("D still protected after rename (cut state migrated)", isCut(D) === false);

// Default: an untouched non-keeper is cut.
cutState = {};
check("untouched non-keeper defaults to cut", isCut({ p: "X:\\d.mkv", keep: 0 }) === true);

// ---- 선택 도구 (selBulk/selUndo) — replicates the prototype's bulk ops verbatim ----
function selSnap() { _undo.push(Object.assign({}, cutState)); if (_undo.length > 50) _undo.shift(); }
function selBulk(GROUPS, mode) {
  selSnap();
  if (mode === "auto") cutState = {};
  else GROUPS.forEach(g => g.files.forEach(f => { if (f.keep) return; cutState[f.p] = mode === "clear" ? false : !isCut(f); }));
}
function selUndo() { if (!_undo.length) return false; cutState = _undo.pop(); return true; }

cutState = {}; _undo = [];
let KP = { p: "S:\\keep.mkv", keep: 1 }, N1 = { p: "S:\\d1.mkv", keep: 0 }, N2 = { p: "S:\\d2.mkv", keep: 0 };
const G = [{ files: [KP, N1, N2] }];

// clear: every non-keeper unchecked -> delete set empty
selBulk(G, "clear");
check("clear: delete set empty", collect(G).paths.length === 0);
// invert after clear: all non-keepers checked, keeper untouched
selBulk(G, "invert");
check("invert: both non-keepers cut", collect(G).paths.length === 2);
check("invert: keeper NEVER cut", isCut(KP) === false && collect(G).paths.indexOf(KP.p) < 0);
// invert again: back to none
selBulk(G, "invert");
check("double invert: delete set empty again", collect(G).paths.length === 0);
// auto: reset to defaults = all non-keepers cut, keeper kept
selBulk(G, "auto");
check("auto: defaults restored (non-keepers cut)", collect(G).paths.length === 2 && isCut(KP) === false);
// undo walks back: auto -> invert2(empty) -> invert1(2 cut) -> clear(empty)
check("undo #1 -> pre-auto (empty)", selUndo() && collect(G).paths.length === 0);
check("undo #2 -> pre-invert2 (2 cut)", selUndo() && collect(G).paths.length === 2);
check("undo #3 -> pre-invert1 (empty)", selUndo() && collect(G).paths.length === 0);
// tog snapshots too: single toggle undoable
cutState = {}; _undo = [];
function togU(f) { if (!f || f.keep) return; selSnap(); cutState[f.p] = !isCut(f); }   // prototype tog calls selSnap before mutating
togU(N1);   // uncheck d1 (default was cut)
check("tog: d1 protected", isCut(N1) === false);
check("undo after tog restores default cut", selUndo() && isCut(N1) === true);
// bounded stack: 60 snapshots -> only 50 kept
_undo = [];
for (let i = 0; i < 60; i++) selSnap();
check("undo stack bounded at 50", _undo.length === 50);
// empty-stack undo is a safe no-op
_undo = [];
check("undo on empty stack: no-op", selUndo() === false);

// migrateCut must rekey UNDO SNAPSHOTS too (index.html:483): without it, undo after an mpvGrid
// rename restores the state under the OLD path, and the renamed file silently reverts to default-cut.
cutState = {}; _undo = [];
let R = { p: "F:\\r_old.mkv", keep: 0 };
togU(R);                     // snapshot #1 = {} ; cutState[old]=false (user protects R)
togU(N1);                    // snapshot #2 = {old:false} ; N1 toggled
migrateCut("F:\\r_old.mkv", "F:\\r_new.mkv"); R.p = "F:\\r_new.mkv";
check("rename: live state migrated", isCut(R) === false);
selUndo();                   // back to snapshot #2 — must have been rekeyed to new path
check("undo after rename keeps renamed file protected", isCut(R) === false);

// sidecar reports the old path with DIFFERENT CASING (lowercase drive letter) — the user's
// uncheck must still follow the rename (exact-key matching would orphan it -> default-cut).
cutState = {}; _undo = [];
let C1 = { p: "F:\\Arch\\Clip.mkv", keep: 0 };
togU(C1);                    // user protects it: cutState["F:\\Arch\\Clip.mkv"]=false
migrateCut("f:\\arch\\clip.mkv", "f:\\arch\\clip.merged.mkv"); C1.p = "f:\\arch\\clip.merged.mkv";
check("case-mismatched rename keeps the uncheck", isCut(C1) === false);

// two case-variant keys: the EXACT key's value must win and every variant must be swept
cutState = { "F:\\A.mkv": false, "f:\\a.mkv": true }; _undo = [];
migrateCut("F:\\A.mkv", "F:\\B.mkv");
check("variant sweep: exact value wins, no orphans", cutState["F:\\B.mkv"] === false && Object.keys(cutState).length === 1);

// rename onto the stored casing (k === newP): the entry must survive, not self-assign-then-delete
cutState = { "f:\\c.mkv": false }; _undo = [];
migrateCut("F:\\C.mkv", "f:\\c.mkv");
check("case-only rename onto stored key keeps the uncheck", cutState["f:\\c.mkv"] === false);

// selReset (new result set / DB switch) empties the stack -> undo is a no-op
cutState = {}; _undo = [];
togU(N1);
_undo = [];                  // selReset()
check("after selReset undo is a no-op", selUndo() === false);

console.log(fail === 0 ? `\nALL ${pass} PASS` : `\n${fail} FAILED`);
