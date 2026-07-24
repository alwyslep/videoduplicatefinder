// Drives the REAL wwwroot/index.html in a browser with a STUBBED Photino bridge (records every
// outgoing message, injects C# replies through the existing window.__rx QA hook), so the wiring
// itself is under test — not a replica of it like keeper-test.js.
//   node bridge-test.js
// Covers: triage save gating (startup + DB switch), mock-sample suppression in the real app,
// and the busy guards on rename / CSV export.
const path = require("path");
let pw; try { pw = require("playwright"); } catch { pw = require(path.join(process.env.APPDATA, "npm/node_modules/playwright")); }

const PAGE = "file:///" + path.join(__dirname, "wwwroot", "index.html").replace(/\\/g, "/");
let pass = 0, fail = 0;
const check = (name, cond) => { cond ? pass++ : fail++; console.log((cond ? "PASS" : "FAIL"), name); };

(async () => {
  let browser;
  try { browser = await pw.chromium.launch({ channel: "chrome" }); }
  catch { browser = await pw.chromium.launch(); }
  const page = await browser.newPage();
  // The bridge as Photino presents it: sendMessage collects, receiveMessage hands us the dispatcher.
  await page.addInitScript(() => {
    window.__sent = [];
    window.external = {
      sendMessage: (s) => window.__sent.push(JSON.parse(s)),
      receiveMessage: (f) => { window.__rxReal = f; },
    };
  });
  await page.goto(PAGE);
  await page.waitForFunction(() => window.__sent && window.__sent.length > 0);

  const sentCmds = () => page.evaluate(() => window.__sent.map(m => m.cmd));
  const saves = () => page.evaluate(() => window.__sent.filter(m => m.cmd === "saveTriage").map(m => m.payload.cut));
  const rx = (cmd, data) => page.evaluate(([c, d]) => window.__rx(JSON.stringify({ cmd: c, data: d })), [cmd, data]);

  // 1) real app never shows the prototype's sample rows (CSV/중복아님/정리 all read window.GROUPS)
  check("bridge present → mock GROUPS cleared", (await page.evaluate(() => (window.GROUPS || []).length)) === 0);
  check("startup asked for the saved triage", (await sentCmds()).includes("getTriage"));
  // scan screen must not show fabricated drive/tally numbers — the mock paint is gated on bridge-absence,
  // and navigating to ③ pulls the real driveInfo.
  check("bridge present → scan drives/tallies not pre-painted", (await page.evaluate(() =>
    !document.getElementById("drives").innerHTML && !document.getElementById("tallies").innerHTML)) === true);
  await page.evaluate(() => { window.__sent.length = 0; go("scan"); });
  check("navigating to ③ requests real driveInfo", (await sentCmds()).includes("driveInfo"));
  await page.evaluate(() => go("review"));

  // 2) nothing may be SAVED before the getTriage round-trip answers — the file is resolved from the
  //    live DB folder, so an early save writes an empty/foreign triage over the real one.
  await page.evaluate(() => {
    window.GROUPS = [{ sim: "99%", files: [
      { p: "T:\\keep.mkv", t: "1:00", res: "1080p", keep: 1, bytes: 100, m: ["1080p", "1 GB", "1k", "24fps", "SDR"] },
      { p: "T:\\dup.mkv", t: "1:00", res: "1080p", bytes: 90, m: ["=", "=", "=", "=", "="] }] }];
    renderGroups();
  });
  await page.waitForTimeout(800);
  check("no saveTriage before the triage reply", (await saves()).length === 0);

  // 3) after the reply, saving is armed and carries the user's state
  await rx("triage", { cut: { "T:\\dup.mkv": false } });
  await page.waitForTimeout(800);
  const afterLoad = await saves();
  check("saveTriage armed after the reply", afterLoad.length > 0 && afterLoad[afterLoad.length - 1]["T:\\dup.mkv"] === false);
  check("loaded triage applied (dup protected)", (await page.evaluate(() => isCut({ p: "T:\\dup.mkv", keep: 0 }))) === false);

  // 4) DB switch: the old DB's check state must be dropped AND saving disarmed until the new DB answers,
  //    or the previous DB's triage lands in the new DB's file (a user-protected file re-marked for 정리).
  await page.evaluate(() => window.__sent.length = 0);
  await rx("dbModeChanged", { activeReal: true });
  await page.waitForTimeout(800);
  check("DB switch: no saveTriage with the old DB's state", (await saves()).length === 0);
  check("DB switch: old check state dropped", (await page.evaluate(() => isCut({ p: "T:\\dup.mkv", keep: 0 }))) === true);
  await rx("triage", { cut: {} });
  await page.waitForTimeout(800);
  check("DB switch: saving re-arms after the new DB answers", (await saves()).length > 0);

  // 5) busy guards — C# runs rename under the engine lock a scan holds for hours, and the CSV save
  //    dialog blocks the message thread (스캔 중지 would queue behind it).
  await page.evaluate(() => { window.GROUPS = [{ sim: "99%", files: [
      { p: "T:\\keep.mkv", t: "1:00", res: "1080p", keep: 1, bytes: 100, m: ["1080p", "1 GB", "1k", "24fps", "SDR"] },
      { p: "T:\\dup.mkv", t: "1:00", res: "1080p", bytes: 90, m: ["=", "=", "=", "=", "="] }] }];
    renderGroups(); window.doStartScan(); window.__sent.length = 0; });
  check("scan latched the busy gate", (await page.evaluate(() => window.__busy())) === true);
  await page.evaluate(() => { const el = document.querySelector("#groups .fop[title='이름 변경']"); el.click(); });
  check("rename refused while busy (no input opened)", (await page.evaluate(() => !document.querySelector("#groups input.rn"))) === true);
  await page.evaluate(() => window.exportCsv());
  check("CSV export refused while busy", !(await sentCmds()).includes("exportCsv"));

  // 6) DB-target toggle must be refused while busy (it wedges the message thread on the C# side).
  await page.evaluate(() => window.__sent.length = 0);
  await page.evaluate(() => { const sw = document.getElementById("swRealDb"); window.toggleRealDb(sw); });
  check("realDb toggle refused while busy (no saveSetting sent)", !(await sentCmds()).includes("saveSetting"));
  check("realDb toggle did not visually flip while busy", (await page.evaluate(() => !document.getElementById("swRealDb").classList.contains("on"))) === true);
  await page.evaluate(() => window.browseRealDb());
  check("browseRealDb refused while busy", !(await sentCmds()).includes("browseRealDb"));

  // …CSV allowed again once the scan ends
  await rx("scanAborted", {});
  await page.evaluate(() => window.exportCsv());
  check("CSV export allowed after the scan ends", (await sentCmds()).includes("exportCsv"));

  // 7) 순회비교 uses the SAME filter as the render: a "drive:f" facet that shows F: groups must send
  //    those groups (the old hand-rolled substring match sent the literal "drive:f" and found nothing).
  await page.evaluate(() => {
    window.GROUPS = [
      { sim: "99%", files: [
        { p: "F:\\a.mkv", res: "1080p", keep: 1, bytes: 10, m: ["1080p","1G","1k","24fps","SDR"] },
        { p: "F:\\b.mkv", res: "1080p", bytes: 9, m: ["=","=","=","=","="] }] },
      { sim: "98%", files: [
        { p: "D:\\c.mkv", res: "2160p", keep: 1, bytes: 10, m: ["2160p","1G","1k","24fps","SDR"] },
        { p: "D:\\d.mkv", res: "2160p", bytes: 9, m: ["=","=","=","=","="] }] }];
    document.getElementById("q").value = "drive:f"; renderGroups();
    window.__sent.length = 0; window.doMpvCompare();   // exercises collectVisibleGroups through the real path
  });
  const cvg = (await page.evaluate(() => window.__sent.find(m => m.cmd === "compareInMpv"))).payload.groups;
  check("순회비교 honors drive: facet (F group sent)", cvg.length === 1 && cvg[0].every(p => p.toLowerCase()[0] === "f"));
  check("순회비교 excludes the non-matching D group", !cvg.some(g => g.some(p => p.toLowerCase()[0] === "d")));

  await browser.close();
  console.log(fail === 0 ? `\nALL ${pass} PASS` : `\n${fail} FAILED`);
  process.exit(fail === 0 ? 0 : 1);
})();
