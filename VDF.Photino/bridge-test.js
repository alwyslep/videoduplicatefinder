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

  // 1b) '실시간 집계' must actually move. It used to be painted ONLY by driveInfo — a one-shot DB
  //     snapshot pulled on go("scan") — while scanProgress repainted just the drive cards, so the box
  //     sat frozen on the pre-scan numbers for the whole scan and read as "nothing ever changes".
  const tallyText = () => page.evaluate(() => document.getElementById("tallies").innerText);
  const tallyLbl = () => page.evaluate(() => document.getElementById("talliesLbl").textContent);
  await rx("driveInfo", {
    drives: [{ root: "T:\\", doneFiles: 1, totalFiles: 10, pct: 10 }],
    tallies: [["전체 파일", "10"], ["분석 완료", "1"]],
  });
  const snapshot = await tallyText();
  check("집계: DB 스냅샷이 그려지고 제목이 그렇다고 말한다", snapshot.includes("전체 파일") && (await tallyLbl()).includes("DB 현황"));
  await rx("scanProgress", { pct: 30, tallies: [["처리 속도", "42 f/s"], ["실제 분석", "1,234"]] });
  const live = await tallyText();
  check("집계가 scanProgress 로 갱신된다", live !== snapshot && live.includes("42 f/s"));
  check("집계 제목이 스캔 중으로 바뀐다", (await tallyLbl()).includes("스캔 중"));
  // 단계 전환 알림처럼 tallies 없는 프레임이 직전 숫자를 지워버리면 안 된다
  await rx("scanProgress", { pct: 0, stage: "파일 목록 작성 중…" });
  check("tallies 없는 프레임은 집계를 지우지 않는다", (await tallyText()).includes("42 f/s"));
  // 비교만 다시(progress) 도 같은 박스를 살려둔다 — Drives 가 null 인 단계라 C# 이 전역 카운터로 채워 보낸다
  await rx("progress", { pct: 55, tallies: [["진행", "55%"], ["처리", "9,001 / 16,364"]] });
  check("비교 진행도 집계를 갱신한다", (await tallyText()).includes("9,001 / 16,364") && (await tallyLbl()).includes("비교 중"));

  // 1c) 작업이 도는 동안 "시작" 버튼은 눈에 보이게 잠겨야 한다. _busy 가드가 토스트로만 튕기던 시절엔
  //     버튼이 멀쩡히 눌리는 모습이라, 스캔이 도는 줄 모르고 계속 누르게 됐다. 반대로 멈춤/일시정지는
  //     그때만 떠야 하고, 레일(①②③)은 절대 잠기면 안 된다 — 진행 화면으로 가는 길이다.
  const locked = () => page.evaluate(() =>
    [].slice.call(document.querySelectorAll("[data-busy]")).filter(b => b.classList.contains("busy-off")).length);
  const ctlShown = () => page.evaluate(() => document.getElementById("scanCtl").style.display !== "none");
  const nBusy = await page.evaluate(() => document.querySelectorAll("[data-busy]").length);
  check("잠금 대상 버튼이 표시돼 있다 (시작/비교/정리)", nBusy >= 6);
  check("대기 중엔 아무 버튼도 잠겨 있지 않다", (await locked()) === 0);
  await page.evaluate(() => doStartScan());
  check("스캔 시작 → 시작 계열 버튼이 전부 잠긴다", (await locked()) === nBusy);
  await rx("scanProgress", { pct: 5, tallies: [["진행", "5%"]] });
  check("스캔 중 → 멈춤/일시정지가 나타난다", (await ctlShown()) === true);
  check("레일은 잠기지 않는다 (진행 화면으로 갈 길)", (await page.evaluate(() =>
    !!document.querySelector('#rail .chip') && !document.querySelector('#rail .busy-off'))) === true);
  // 잠긴 상태에서 또 눌러도 중복 요청이 나가면 안 된다
  await page.evaluate(() => { window.__sent.length = 0; doStartScan(); doCompare(); });
  check("잠긴 동안 재클릭은 명령을 보내지 않는다", (await sentCmds()).length === 0);
  await rx("scanAborted", {});
  check("스캔 종료 → 잠금이 풀린다", (await locked()) === 0);
  check("스캔 종료 → 멈춤/일시정지가 사라진다", (await ctlShown()) === false);
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
