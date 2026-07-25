using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using Photino.NET;
using VDF.Core;
using VDF.Core.FFTools;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Photino;

// Photino window rendering the Deep Space HTML, talking to VDF.Core over a JS<->C# bridge.
//
// DB SAFETY: by DEFAULT VDF.Core is pointed at an isolated *copy* (CopyDbFolder) so scan/compare
// writes never touch the user's real ~700MB index. The user may OPT IN to 실 DB 모드 (환경설정, default
// off, with a warning) to point it at the real folder instead. NOTE: file DELETION (reclaim / mpvGrid)
// already hits REAL files in BOTH modes (the DB paths are real) — the keeper-protection + recycle-only
// guards are what protect files; the copy only protects the DB index from scan/compare writes.
static class Program {
	// The isolated copy lives under %LOCALAPPDATA% — NOT %TEMP%: Storage Sense / Disk Cleanup purge stale
	// %TEMP% content, which would take the 700MB index AND the user's 중복 아님/검토 curation (both stored
	// beside the active DB) with it. Migrated once from the old %TEMP% path on startup — see MigrateLegacyCopyDb.
	static readonly string CopyDbFolder = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VDF", "devdb");
	static readonly string LegacyCopyDbFolder = Path.Combine(Path.GetTempPath(), "vdf-devdb");
	// The DB folder VDF.Core actually reads/writes — real folder only when the opt-in is on AND it exists.
	static string ActiveDbFolder => _cfg.realDbMode && !string.IsNullOrEmpty(_cfg.realDbFolder) && Directory.Exists(_cfg.realDbFolder)
		? _cfg.realDbFolder : CopyDbFolder;
	// Serializes every engine/DB operation (compare/scan/stats/driveInfo + the mpvGrid sidecar's
	// RemoveFromDatabase) — VDF.Core keeps ONE static DatabaseUtils.Database, so overlapping ops
	// on separate ScanEngine instances would race enumerate/mutate/save and tear the copy DB.
	static readonly object _engineLock = new();
	// Last compare/scan results — source for on-demand thumbnails, reclaim keeper rebuild and keeper re-election.
	// PUBLISH BY SWAP ONLY: readers enumerate it from the message thread (Reclaim/GetThumbs) AND from the
	// sidecar watcher thread (ReElectKeeps). A structural mutation of the *published* set throws
	// 'Collection was modified' in a reader — on the watcher thread that is an unhandled pool-thread
	// exception, i.e. process death. Build a new set and assign the field instead (a single field read
	// per reader then enumerates a set nobody mutates).
	static volatile HashSet<DuplicateItem> _lastDupes = new();
	// Engine of the in-flight scan/compare — Pause/Stop are called from the message thread WITHOUT _engineLock
	// (the lock is held by the running scan task; ScanEngine.Pause/Stop are designed for cross-thread calls).
	static volatile ScanEngine? _activeScan;
	// User pressed 중지 — scoped to the ENGINE it was aimed at, not a process-global bool: a stale write
	// from the message thread can then only ever point at a dead engine, never poison the next queued op.
	// A flag of our own is needed because ScanAborted alone is racy: the search-only path fires
	// BuildingHashesDone BEFORE ScanAborted on abort (ScanEngine.cs:588-596).
	static volatile ScanEngine? _stopReqFor;
	const string U_MINUS = "−";   // − : the mock's metric-delta minus (JS colors on this char)
	const int MaxGroupsShown = 150;    // cap cards sent to the webview; report the true total

	// WinExe has no console — CLI test modes (selftest/scantest/…) attach the invoking terminal's
	// console so their Console.WriteLine output still lands there. No-op when launched from Explorer.
	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	static extern bool AttachConsole(int dwProcessId);
	const int ATTACH_PARENT_PROCESS = -1;

	[STAThread]
	static void Main(string[] args) {
		_cliMode = args.Length > 0;
		if (_cliMode) AttachConsole(ATTACH_PARENT_PROCESS);
		InstallCrashNet();          // 조용히 사라지는 죽음 금지 — 이유를 남기고 알린다
		EnsureNativeFFmpegOnPath();   // 어느 출력 폴더에서 실행해도 FFmpeg 공유 라이브러리를 찾게 한다
		// Guarantee the isolated copy folder EXISTS: if it is missing (fresh box / %TEMP% cleaned),
		// VDF.Core silently falls back to the exe dir for reads AND WRITES — breaking isolation. With
		// the folder present, ResolveDatabaseFolder always returns it, so every write lands on the copy.
		MigrateLegacyCopyDb();                      // one-time carry from the old %TEMP% location (no re-scan)
		Directory.CreateDirectory(CopyDbFolder);   // always ensure the copy exists as the safe fallback

		if (args.Length > 1 && args[0] == "fpdump") { FpDump(args[1]); return; }   // DB의 오디오 지문 덤프 (경로별 해시)
		if (args.Length > 0 && args[0] == "fpbench") { FpBench(args.Length > 1 ? int.Parse(args[1]) : 600); return; }   // 관리 지문 파이프라인 단독 비용
		if (args.Length > 0 && args[0] == "ffcheck") { FFCheck(); return; }        // 스캔 전 FFmpeg 준비 상태 진단
		if (args.Length > 0 && args[0] == "crashtest") { CrashTest(); return; }   // 크래시 기록 경로 검증
		if (args.Length > 0 && args[0] == "selftest") { SelfTest(); return; }
		if (args.Length > 1 && args[0] == "scantest") { ScanTest(args[1]); return; }
		if (args.Length > 1 && args[0] == "trashtest") { TrashTest(args[1]); return; }
		if (args.Length > 1 && args[0] == "stoptest") { StopTest(args[1]); return; }
		if (args.Length > 0 && args[0] == "blacklisttest") { BlacklistTest(); return; }
		if (args.Length > 0 && args[0] == "renametest") { RenameTombstoneTest(); return; }
		if (args.Length > 1 && args[0] == "recyclecheck") { Console.WriteLine($"[recyclecheck] canRecycle={CanRecycle(args[1])}  {args[1]}"); return; }
		if (args.Length > 1 && args[0] == "thumbtest") {
			var b = FfmpegEngine.ExtractThumbnailJpeg(args[1], TimeSpan.FromSeconds(1), 160, false);
			Console.WriteLine($"[thumbtest] jpeg bytes={(b?.Length ?? 0)} base64Len={(b != null ? Convert.ToBase64String(b).Length : 0)}");
			return;
		}
		if (args.Length > 0 && args[0] == "recycleselftest") {   // literal paths in source — no argv backslash mangling
			foreach (var (p, exp) in new[] { (@"\\NAS\media\clip.mkv", false), (@"\\?\UNC\srv\s\x.mkv", false), (@"C:\Windows\notepad.exe", true) })
				Console.WriteLine($"[recycleselftest] {(CanRecycle(p) == exp ? "PASS" : "FAIL")} canRecycle({p})={CanRecycle(p)} exp={exp}");
			return;
		}

		TryDeleteLog();   // clear any prior session's log BEFORE Core's first write (it appends private paths)

		string index = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
		var win = new PhotinoWindow()
			.SetTitle("VDF — Deep Space")
			.SetIconFile(Path.Combine(AppContext.BaseDirectory, "app.ico"))
			.SetUseOsDefaultSize(false)
			.SetSize(1440, 920)   // the restore-down size
			.Center()
			.SetContextMenuEnabled(false)
			.SetDevToolsEnabled(true)
			.RegisterWebMessageReceivedHandler(OnMessage)
			.Load(index);
		// Opens at the 1440×920 centered size set above — the user prefers a normal window over
		// maximized. The old intermittent 960×613 DPI-scale sizing bug is fixed by app.manifest
		// (PerMonitorV2), so SetSize is reliable without maximizing.
		win.WaitForClose();
		TryDeleteLog();   // wipe on normal exit — nothing with private paths persists after use (best-effort: a hard crash mid-session leaves it)
	}

	// One-time move of the isolated copy off %TEMP% to %LOCALAPPDATA% (see CopyDbFolder). Same-volume =
	// instant rename that carries the existing index, so NO re-scan. Cross-volume Move throws before
	// touching anything → fall back to copying the files (legacy left intact — the copy is the safety).
	// Runs only until CopyDbFolder exists; idempotent thereafter. Best-effort: any failure just leaves the
	// legacy folder in use for this run.
	static void MigrateLegacyCopyDb() {
		try {
			if (Directory.Exists(CopyDbFolder)) return;         // already migrated, or a fresh install on the new path
			if (!Directory.Exists(LegacyCopyDbFolder)) return;  // nothing to carry
			Directory.CreateDirectory(Path.GetDirectoryName(CopyDbFolder)!);
			Directory.Move(LegacyCopyDbFolder, CopyDbFolder);   // atomic rename on the same volume
		}
		catch {
			try {   // cross-volume (or a transient lock): copy what we can, keep the legacy as-is
				Directory.CreateDirectory(CopyDbFolder);
				foreach (var f in Directory.EnumerateFiles(LegacyCopyDbFolder))
					File.Copy(f, Path.Combine(CopyDbFolder, Path.GetFileName(f)), overwrite: false);
			}
			catch { }
		}
	}

	// ---------- 조용한 죽음 방지 (WinExe 는 콘솔이 없다) ----------
	// 백그라운드/풀 스레드의 미처리 예외는 창을 아무 말 없이 사라지게 만든다 — 실제로 그렇게 죽었다
	// (2026-07-25: FFmpeg 공유 라이브러리 부재 → async void StartSearch 의 동기 throw → 풀 스레드 미처리).
	// .NET 에선 종료 자체를 막을 수 없지만, 이유를 남기고 알릴 수는 있다: 앱 폴더 밖(log.txt 는 종료 시
	// 삭제된다)에 누적 기록 + 네이티브 MessageBox. 웹뷰를 거치지 않는다 — 죽어가는 프로세스에서 브리지를
	// 부르면 그대로 멈춰 서서 아무것도 못 남긴다.
	static readonly string CrashLogPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VDF", "crash.log");

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
	const uint MB_ICONERROR = 0x10;

	// CLI 테스트 모드(콘솔 연결됨)에서는 모달 대화상자를 띄우지 않는다 — 자동화에서 아무도 닫을 수
	// 없는 창이 떠서 그대로 매달린다. 콘솔에 찍는 편이 그 자리에서 더 유용하다.
	static bool _cliMode;

	static void InstallCrashNet() {
		AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash("UnhandledException", e.ExceptionObject as Exception, fatal: true);
		// 아무도 await 하지 않은 실패 Task: .NET Core 에선 프로세스를 죽이지 않지만 "엔진 작업이 조용히
		// 죽었다"는 신호다 — 기록해서 '스캔이 반응 없이 멈췄다'를 나중에 진단할 수 있게 한다.
		TaskScheduler.UnobservedTaskException += (_, e) => { ReportCrash("UnobservedTaskException", e.Exception, fatal: false); e.SetObserved(); };
	}

	static void ReportCrash(string kind, Exception? ex, bool fatal) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
			File.AppendAllText(CrashLogPath,
				$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{kind}] {ex}{Environment.NewLine}{new string('-', 70)}{Environment.NewLine}");
		}
		catch { }
		if (_cliMode) { try { Console.Error.WriteLine($"[crash] {kind}: {ex?.GetType().Name}: {ex?.Message} (기록: {CrashLogPath})"); } catch { } return; }
		if (!fatal) return;
		try {
			MessageBoxW(IntPtr.Zero,
				$"VDF가 예기치 않게 종료된다.\n\n{ex?.GetType().Name}: {ex?.Message}\n\n기록: {CrashLogPath}",
				"VDF — 치명적 오류", MB_ICONERROR);
		}
		catch { }
	}

	// ---------- FFmpeg 공유 라이브러리 위치 확보 ----------
	// VDF.Core 는 ffmpeg.exe 옆 · 자기 자신(출력 폴더) 옆 · PATH 에서 avcodec-62.dll 등을 찾는다. 이 PC 의
	// ffmpeg.exe 는 WinGet 심링크라 옆에 DLL 이 없고, DLL(~250MB)은 평면 설치 폴더에만 있다 — 그래서
	// bin\Debug\… 에서 실행하면 라이브러리를 못 찾고 스캔 첫 호출에서 죽었다. 전체 세트를 가진 폴더를
	// 찾아 프로세스 PATH 앞에 붙인다: Core 가 이미 하는 PATH 탐색이 성공하므로 어느 출력 폴더에서도
	// 동작하고, 빌드마다 250MB 를 복사하지 않는다.
	static void EnsureNativeFFmpegOnPath() {
		try {
			string[] want = ScanEngine.NativeFFmpegLibraryNames;
			if (want.Length == 0) return;
			bool Has(string dir) => dir.Length > 0 && Directory.Exists(dir) && want.All(f => File.Exists(Path.Combine(dir, f)));
			static void Prepend(string dir) =>
				Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));

			string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
			if (Has(baseDir)) return;                                    // 설치 레이아웃: DLL 이 exe 옆에 있다 (Core 가 그대로 찾는다)
			if (Has(_cfg.ffmpegLibFolder)) { Prepend(_cfg.ffmpegLibFolder); return; }   // 지난번에 찾아 기억해둔 폴더

			// 출력 폴더에서 위로 올라가며 흔한 위치를 시도한다 — 개발 트리는 DLL 을 bin\Debug\… 보다
			// 여러 단계 위의 설치/배포 폴더(…\dedup\VDF_Photino)에 두고 있다.
			var dir = new DirectoryInfo(baseDir);
			for (int up = 0; up < 7 && dir != null; up++, dir = dir.Parent) {
				foreach (var name in new[] { "", "bin", "lib", "ffmpeg", "VDF_Photino", "vdf-deploy" }) {
					string cand = name.Length == 0 ? dir.FullName : Path.Combine(dir.FullName, name);
					if (!Has(cand)) continue;
					Prepend(cand);
					if (!string.Equals(_cfg.ffmpegLibFolder, cand, StringComparison.OrdinalIgnoreCase)) {
						_cfg.ffmpegLibFolder = cand; SaveCfg();   // 다음 실행부터는 탐색 없이 바로
					}
					return;
				}
			}
		}
		catch { }   // 최선 노력: 못 찾으면 StartScan 의 사전 점검이 "무엇을 어디에 두라"고 알려준다
	}

	// FFmpeg 준비 상태 진단 (`VDF.Photino.exe ffcheck`). "스캔 시작하면 죽는다/막힌다"의 원인은 대개
	// 여기다 — 어떤 파일이 어디서 발견됐는지(또는 안 됐는지)를 한 화면에 보여준다. EnsureNativeFFmpegOnPath
	// 가 이미 돌아간 뒤이므로 자동 탐색이 반영된 "실제 스캔이 보게 될" 상태다.
	static void FFCheck() {
		Console.WriteLine($"[ffcheck] exe 폴더        : {AppContext.BaseDirectory}");
		Console.WriteLine($"[ffcheck] ffmpeg          : {FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFmpeg) ?? "(없음)"}");
		Console.WriteLine($"[ffcheck] ffprobe         : {FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFProbe) ?? "(없음)"}");
		Console.WriteLine($"[ffcheck] 네이티브 바인딩 : {(_cfg.useNativeFfmpegBinding ? "켜짐 (공유 라이브러리 필요)" : "꺼짐 (ffmpeg.exe 프로세스 모드)")}");
		Console.WriteLine($"[ffcheck] 라이브러리 폴더 : {(string.IsNullOrEmpty(_cfg.ffmpegLibFolder) ? "(미설정 — exe 폴더·PATH 에서 탐색)" : _cfg.ffmpegLibFolder)}");
		var dirs = ProbeDirs().ToList();
		foreach (var n in ScanEngine.NativeFFmpegLibraryNames) {
			string where = "*** 못 찾음 ***";
			foreach (var d in dirs) {
				try { if (File.Exists(Path.Combine(d, n))) { where = d; break; } } catch { }
			}
			Console.WriteLine($"[ffcheck]   {n,-18} {where}");
		}
		Console.WriteLine($"[ffcheck] 결과: 네이티브 라이브러리 {(ScanEngine.NativeFFmpegExists ? "사용 가능 — 스캔 가능" : "사용 불가 — 스캔은 차단된다 (예전엔 이 지점에서 프로세스가 죽었다)")}");
	}

	// 관리 코드(chromaprint) 파이프라인 단독 비용 (`VDF.Photino.exe fpbench [오디오초]`).
	// 오디오 지문 1건의 비용은 ① FFmpeg 네이티브 디코드+리샘플 ② 이 관리 파이프라인(FFT·크로마·해시)
	// 으로 갈린다. 벡터화가 의미 있는 곳은 ②뿐이므로, 최적화 전에 ②의 실제 비중을 재는 것이 목적이다.
	// 11025Hz 모노 PCM 을 직접 먹여 ①을 완전히 배제한다.
	static void FpBench(int audioSeconds) {
		const int rate = 11025;
		int total = rate * audioSeconds;
		var pcm = new short[rate];                     // 1초 청크
		uint seed = 12345;                             // 결정적 잡음 — 내용은 비용에 영향이 없다
		for (int i = 0; i < pcm.Length; i++) { seed = seed * 1664525 + 1013904223; pcm[i] = (short)(seed >> 17); }

		var ctx = new VDF.Core.Chromaprint.ChromaContext();
		ctx.Start();
		var sw = System.Diagnostics.Stopwatch.StartNew();
		for (int s = 0; s < audioSeconds; s++) ctx.Feed(pcm);
		ctx.Finish();
		var fp = ctx.GetRawFingerprint();
		sw.Stop();

		double ms = sw.Elapsed.TotalMilliseconds;
		Console.WriteLine($"[fpbench] 오디오 {audioSeconds}초 ({total:N0} 샘플) → 관리 파이프라인 {ms:N0} ms");
		Console.WriteLine($"[fpbench] 오디오 1초당 {ms / audioSeconds:N3} ms · 지문 {fp.Length} 워드");
		Console.WriteLine($"[fpbench] → 오디오 1시간당 {ms / audioSeconds * 3600 / 1000:N2} 초 (단일 코어)");
	}

	// DB 의 오디오 지문 덤프 (`VDF.Photino.exe fpdump <db폴더>`) — 경로별 지문 해시·길이를 정렬해 찍는다.
	// 용도: 병렬 디코드 경로와 순차 경로가 **비트 단위로 같은 지문**을 만드는지 실파일로 검증하는 것.
	// 세그먼트 seam 검사는 이걸 대신할 수 없다 — 두 워커가 같은(잘못된) 패킷당 샘플 수를 쓰면 서로
	// 일치하면서 격자만 통째로 어긋나므로, 순차 결과와의 직접 비교만이 증거가 된다.
	static void FpDump(string dbFolder) {
		DatabaseUtils.CustomDatabaseFolder = dbFolder;
		DatabaseUtils.InvalidateDatabaseFolder();
		DatabaseUtils.LoadDatabase();
		foreach (var e in DatabaseUtils.Database.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)) {
			var fp = e.AudioFingerprint;
			ulong h = 14695981039346656037UL;   // FNV-1a over the fingerprint words
			if (fp != null)
				foreach (uint w in fp) { h ^= w; h *= 1099511628211UL; }
			Console.WriteLine($"{(fp == null ? "-none-" : h.ToString("x16"))}\t{fp?.Length ?? -1}\t{Path.GetFileName(e.Path)}");
		}
	}

	// VDF.Core 가 공유 라이브러리를 찾는 순서와 같은 후보 폴더들 (진단 표시용).
	static IEnumerable<string> ProbeDirs() {
		yield return AppContext.BaseDirectory;
		if (!string.IsNullOrEmpty(_cfg.ffmpegLibFolder)) yield return _cfg.ffmpegLibFolder;
		if (FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFmpeg) is { } fp && Path.GetDirectoryName(fp) is { Length: > 0 } fd) yield return fd;
		foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
			if (p.Length > 0) yield return p;
	}

	// 크래시 그물 검증 (`VDF.Photino.exe crashtest`): 포그라운드 스레드에서 미처리 예외를 일으켜
	// crash.log 기록 경로가 살아있는지 확인한다. 런타임이 프로세스를 종료하므로 종료 코드는 0이 아니다.
	static void CrashTest() {
		Console.WriteLine($"[crashtest] 기록 위치: {CrashLogPath}");
		new Thread(() => throw new InvalidOperationException("crashtest: 의도적인 미처리 예외")).Start();
		Thread.Sleep(5000);   // 핸들러가 기록할 시간 (그 전에 런타임이 종료시킨다)
	}

	// VDF.Core Logger appends full file paths to log.txt in the exe dir (unconditional AppendAllText; can't be
	// safely blocked from outside without aborting scans). Best-effort containment: delete it at startup + exit
	// so no private-path log persists between sessions. Never touched while Core runs.
	static void TryDeleteLog() {
		foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VDF") }) {
			try { var f = Path.Combine(dir, "log.txt"); if (File.Exists(f)) File.Delete(f); } catch { }
		}
	}

	static void OnMessage(object? sender, string message) {
		var win = (PhotinoWindow)sender!;
		try {
			using var doc = JsonDocument.Parse(message);
			string cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";
			switch (cmd) {
				// Serialize every engine/DB op over the single static DatabaseUtils.Database (a second
				// op queues on the lock instead of racing enumerate/mutate/save on the shared HashSet).
				case "stats": Task.Run(() => { lock (_engineLock) LoadStats(win); }); break;
				case "compare": Task.Run(() => { lock (_engineLock) RunCompare(win); }); break;
				case "driveInfo": Task.Run(() => { lock (_engineLock) LoadDriveInfo(win); }); break;
				case "startScan": Task.Run(() => { lock (_engineLock) StartScan(win); }); break;
			// Pausing a stop-drain wedges it (workers park in WaitWhilePaused, scanAborted never fires,
			// and only a force-abort recovers) — once 중지 is requested, pause is refused.
			case "pauseScan": if (_activeScan is { } pe && _stopReqFor != pe) { pe.Pause(); Reply(win, "scanPaused", new { }); } break;
			case "resumeScan": if (_activeScan is { } re) { re.Resume(); Reply(win, "scanResumed", new { }); } break;
			case "stopScan": if (_activeScan is { } se) { _stopReqFor = se; Reply(win, "scanStopping", new { safe = se.Stop() }); } break;   // 1st call = safe drain, 2nd = force
				case "thumbs": {
					var wantList = new List<string>();
					if (doc.RootElement.TryGetProperty("payload", out var tpl) && tpl.TryGetProperty("paths", out var tps))
						foreach (var t in tps.EnumerateArray()) { var s = t.GetString(); if (!string.IsNullOrEmpty(s)) wantList.Add(s); }
					Task.Run(() => GetThumbs(win, wantList));
					break;
				}
				case "reclaim": Reclaim(win, doc.RootElement); break;   // sync: native confirm dialog on the message thread
				case "autoCheck": { var a = doc.RootElement.Clone(); Task.Run(() => AutoCheck(win, a)); break; }   // reads the _lastDupes snapshot only — no engine lock needed
				case "dryRun": DryRunReport(win, doc.RootElement); break;      // sync: native save dialog on the message thread
				case "copyTo": CopyMoveTo(win, doc.RootElement, move: false); break;   // sync picker/confirm on the message thread, worker on the pool
				case "moveTo": CopyMoveTo(win, doc.RootElement, move: true); break;
				case "notMatch": MarkNotMatch(win, doc.RootElement); break;   // sync: native confirm on the message thread
				case "exportCsv": ExportCsv(win, doc.RootElement); break;     // sync: native save dialog on the message thread
				// Clone BEFORE Task.Run: `doc` is disposed when OnMessage returns, which can happen
				// before the pool thread runs the lambda (ObjectDisposedException → command silently dropped).
				case "dbQuery": { var q = doc.RootElement.Clone(); Task.Run(() => { lock (_engineLock) DbQuery(win, q); }); break; }
				case "dbRemove": { var r = doc.RootElement.Clone(); Task.Run(() => { lock (_engineLock) DbRemove(win, r); }); break; }
				case "getTriage": ReplyTriage(win); break;
				case "saveTriage": SaveTriage(doc.RootElement); break;
				case "getSources": ReplySources(win); break;
				case "listDir": { var d = doc.RootElement.Clone(); Task.Run(() => ListDir(win, d)); break; }   // read-only folder enumeration for the source tree
				case "setSources": SetSources(win, doc.RootElement); break;   // tree replaces the whole include list
				case "addSource": AddSource(win); break;
				case "addExclude": AddExclude(win); break;
				case "removeSource": RemovePathFrom(win, doc.RootElement, exclude: false); break;
				case "removeExclude": RemovePathFrom(win, doc.RootElement, exclude: true); break;
				case "getSettings": ReplySettings(win); break;
				case "getRules": ReplyRules(win); break;
				case "setRule": SetRule(win, doc.RootElement); break;
				case "browseMpv": BrowseMpv(win); break;
				case "browseRealDb": BrowseRealDb(win); break;
				case "saveSetting": SaveSetting(win, doc.RootElement); break;
				case "compareInMpv": HandleMpvCompare(win, doc.RootElement); break;
				case "openFile": OpenPath(win, doc.RootElement, reveal: false); break;    // 기본 플레이어로 재생
				case "revealFile": OpenPath(win, doc.RootElement, reveal: true); break;   // 탐색기에서 표시
				case "renameFile": { var n = doc.RootElement.Clone(); Task.Run(() => { lock (_engineLock) RenameFile(win, n); }); break; }   // mutates the DB entry
				default: Reply(win, "error", new { message = $"unknown cmd '{cmd}'" }); break;
			}
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	static void LoadStats(PhotinoWindow win) {
		try {
			string core = typeof(ScanEngine).Assembly.GetName().Version?.ToString() ?? "?";
			int files = 0, drives = 0;
			string? lastScan = null;
			string dbFile = Path.Combine(ActiveDbFolder, "ScannedFiles.db");
			if (File.Exists(dbFile)) {
				EnsureDb();
				var db = DatabaseUtils.Database;
				files = db.Count;
				drives = db.Select(e => Path.GetPathRoot(e.Path) ?? "").Where(r => r.Length > 0)
						   .Distinct(StringComparer.OrdinalIgnoreCase).Count();
				lastScan = File.GetLastWriteTime(dbFile).ToString("yyyy-MM-dd");
			}
			Reply(win, "stats", new { core, machine = Environment.MachineName, files, drives, lastScan, activeReal = ActiveDbFolder != CopyDbFolder });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// Re-run duplicate detection over the loaded DB's existing fingerprints (no re-scan) and
	// return real groups shaped exactly like the prototype's mock model so the JS renderer is unchanged.
	static void RunCompare(PhotinoWindow win) {
		try {
			var engine = new ScanEngine();
			engine.Settings.CustomDatabaseFolder = ActiveDbFolder;   // isolated copy
			ApplyRules(engine.Settings);                          // user's 규칙 knobs

			var sw = Stopwatch.StartNew();
			var tcs = new TaskCompletionSource();
			void done(object? s, EventArgs e) => tcs.TrySetResult();
			int lastPct = -1;
			void prog(object? s, ScanProgressChangedEventArgs e) {
				int pct = e.MaxPosition > 0 ? (int)(100L * e.CurrentPosition / e.MaxPosition) : 0;
				if (pct == lastPct) return;                    // whole-percent throttle
				lastPct = pct;
				Reply(win, "progress", new {
					pct, stage = e.CurrentStage ?? "",
					file = string.IsNullOrEmpty(e.CurrentFile) ? "" : Path.GetFileName(e.CurrentFile),   // 파일명만 — 지금 뭘 비교 중인지 보여달라는 요청
					elapsed = (long)e.Elapsed.TotalSeconds,
					remain = e.Remaining > TimeSpan.Zero ? (long)e.Remaining.TotalSeconds : -1,
				});
			}
			engine.Progress += prog;
			// Multicast delegates run in subscription order: the flag setter MUST precede `done`, or the
			// waiter can wake and read aborted==false before the pool thread sets it (engine-internal
			// aborts have _stopReqFor==null, so that stale read would present an abort as a clean result).
			bool aborted = false;
			engine.ScanAborted += (s, e) => aborted = true;
			engine.ScanDone += done;
			engine.ScanAborted += done;
			_activeScan = engine;
			engine.StartCompare();
			// Same stop-aware wait as StartScan: a stop in the pre-compare window no-ops engine-side.
			while (!tcs.Task.Wait(500))
				if (_stopReqFor == engine) engine.Stop(force: true);
			engine.Progress -= prog;
			engine.ScanDone -= done;
			engine.ScanAborted -= done;
			sw.Stop();

			if (aborted || _stopReqFor == engine) { Reply(win, "scanAborted", new { }); return; }

			var dupes = engine.Duplicates;
			ApplyGroupBlacklist(dupes);
			_lastDupes = dupes;   // for on-demand thumbnails
			var groups = BuildGroups(dupes, out int totalGroups);
			string why = string.Join(" › ", _cfg.priority.Take(3).Select(LabelFor));
			Console.WriteLine($"[compare] {dupes.Count} items in {totalGroups} groups, {sw.ElapsedMilliseconds}ms");
			Reply(win, "groups", new { totalGroups, shown = groups.Count, items = dupes.Count, why, groups });
		}
		// fatal: this error ENDS the long op the JS gated _busy on — only these may clear that gate
		// (a thumbs/listDir/mpv error mid-scan must not, or the single-flight guard dies mid-scan).
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message, fatal = true }); }
		finally { _activeScan = null; _stopReqFor = null; }   // runs under _engineLock — strictly before the next op starts
	}

	// ③ 스캔 — per-drive completion straight from the DB (GetDrivePreview): NO scanning, safe to call anytime.
	static void LoadDriveInfo(PhotinoWindow win) {
		try {
			EnsureDb();
			var eng = new ScanEngine();
			eng.Settings.CustomDatabaseFolder = ActiveDbFolder;
			ApplyRules(eng.Settings);   // scope/entireDb shape the preview
			var dp = eng.GetDrivePreview();
			long totBytes = dp.Sum(d => d.TotalBytes), doneBytes = dp.Sum(d => d.DoneBytes);
			int totFiles = dp.Sum(d => d.TotalFiles), doneFiles = dp.Sum(d => d.DoneFiles);
			var drives = dp.Select(d => new {
				root = d.Root, doneFiles = d.DoneFiles, totalFiles = d.TotalFiles,
				fps = 0d, pct = d.TotalBytes > 0 ? (int)(100.0 * d.DoneBytes / d.TotalBytes)
					: d.TotalFiles > 0 ? (int)(100.0 * d.DoneFiles / d.TotalFiles) : 0,
			}).ToArray();
			var tallies = new object[] {
				new[] { "전체 파일", totFiles.ToString("#,0", CultureInfo.InvariantCulture) },
				new[] { "분석 완료", doneFiles.ToString("#,0", CultureInfo.InvariantCulture) },
				new[] { "남은 파일", (totFiles - doneFiles).ToString("#,0", CultureInfo.InvariantCulture) },
				new[] { "전체 용량", HumanBytes(totBytes) },
				new[] { "드라이브", dp.Length.ToString() },
				new[] { "진행", totBytes > 0 ? (100.0 * doneBytes / totBytes).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "0%" },
			};
			Reply(win, "driveInfo", new { drives, tallies });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// ▶ 스캔 시작 — REAL StartSearch (enumerate + hash) then StartCompare, streaming per-drive progress.
	// GUARD: only scans the user's explicitly-chosen source folders (_cfg.sources). Empty => refuse, so a
	// full 35TB library re-scan is never triggered by accident. Writes land on the DB copy only.
	static void StartScan(PhotinoWindow win) {
		try {
			var folders = _cfg.sources.Where(Directory.Exists).ToArray();
			if (folders.Length == 0) {
				Reply(win, "scanBlocked", new { message = "스캔할 소스 폴더가 없다. ① 소스에서 폴더를 지정해줘. (전체 재스캔은 실파일을 읽어 오래 걸린다)" });
				return;
			}
			// StartSearch is async void: if PrepareSearch throws FFNotFound synchronously the completion
			// event never fires and the wait below would hang. Pre-check the tools and refuse cleanly instead.
			if (FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFProbe) is null || FFToolsUtils.GetPath(FFToolsUtils.FFTool.FFmpeg) is null) {
				Reply(win, "scanBlocked", new { message = "ffmpeg/ffprobe를 찾을 수 없다 — 스캔에 필요하다. PATH에 두거나 exe 옆 bin/ 폴더에 넣어줘." });
				return;
			}
			// PrepareSearch 는 네이티브 바인딩이 켜져 있으면 FFmpeg 공유 라이브러리도 요구한다 — 위의 exe
			// 검사는 그걸 못 잡는다(ffmpeg.exe 는 PATH 에 있어도 DLL 은 따로다). 그 throw 는 async void
			// StartSearch 안에서 나므로 예전엔 스캔 시작 즉시 프로세스가 죽었다. 여기서 미리 막고 알린다.
			if (_cfg.useNativeFfmpegBinding && !ScanEngine.NativeFFmpegExists) {
				Reply(win, "scanBlocked", new {
					message = "FFmpeg 네이티브 라이브러리를 찾을 수 없다 — 스캔에 필요하다. 필요 파일: " +
						string.Join(", ", ScanEngine.NativeFFmpegLibraryNames) +
						$" → exe 폴더({AppContext.BaseDirectory})에 두거나, 환경설정에서 '네이티브 FFmpeg 바인딩'을 끄면 ffmpeg.exe(프로세스 모드)로 스캔한다."
				});
				return;
			}
			EnsureDb();
			var engine = new ScanEngine();
			engine.Settings.CustomDatabaseFolder = ActiveDbFolder;
			ApplyRules(engine.Settings);
			foreach (var f in folders) engine.Settings.IncludeList.Add(f);
			engine.Settings.ScanAgainstEntireDatabase = false;   // enumerate ONLY the chosen folders

			void prog(object? s, ScanProgressChangedEventArgs e) {
				var drives = e.Drives?.Select(d => new {
					root = d.Root, doneFiles = d.DoneFiles, totalFiles = d.TotalFiles,
					fps = Math.Round(d.FilesPerSec), conc = d.Concurrency,
					pct = d.TotalBytes > 0 ? (int)(100.0 * d.DoneBytes / d.TotalBytes) : 0,
					// 워커별 "지금 처리 중" 파일 — 어느 드라이브의 어떤 파일에 무슨 작업 중인지 보여달라는 요청.
					// 파일명만 (전체 경로는 카드 폭을 넘친다 — 드라이브는 카드 제목이 이미 말해준다).
					active = d.ActiveFiles?.Where(a => !string.IsNullOrEmpty(a.File))
						.Select(a => new { file = Path.GetFileName(a.File), stage = a.Stage ?? "", cur = a.StageCurrent, max = a.StageMax }).ToArray(),
				}).ToArray();
				int pctAll = e.MaxPosition > 0 ? (int)(100L * e.CurrentPosition / e.MaxPosition) : 0;
				Reply(win, "scanProgress", new {
					pct = pctAll, pos = e.CurrentPosition, max = e.MaxPosition, stage = e.CurrentStage ?? "",
					file = string.IsNullOrEmpty(e.CurrentFile) ? "" : Path.GetFileName(e.CurrentFile),
					sCur = e.StageCurrent, sMax = e.StageMax,
					elapsed = (long)e.Elapsed.TotalSeconds,
					remain = e.Remaining > TimeSpan.Zero ? (long)e.Remaining.TotalSeconds : -1,
					drives,
				});
			}
			engine.Progress += prog;
			bool aborted = false;
			void onAbort(object? s, EventArgs e) => aborted = true;
			engine.ScanAborted += onAbort;
			_activeScan = engine;   // from here 중지/일시정지 can reach it

			// BuildFileList(열거) 단계는 Progress 이벤트를 전혀 내지 않는다 — 단계 전환을 직접 알려서
			// "지금 앱이 뭘 하는지 안 보인다"는 공백을 없앤다. drives 없는 페이로드는 JS 가 그냥 무시한다.
			void enumDone(object? s, EventArgs e) => Reply(win, "scanProgress", new { pct = 0, stage = "파일 목록 완료 — 메타데이터·지문 생성 시작" });
			engine.FilesEnumerated += enumDone;
			var s1 = new TaskCompletionSource();
			void searchDone(object? s, EventArgs e) => s1.TrySetResult();
			engine.BuildingHashesDone += searchDone; engine.ScanAborted += searchDone;
			Reply(win, "scanProgress", new { pct = 0, stage = "파일 목록 작성 중… (폴더 열거 · 이동/개명 감지)" });
			engine.StartSearch(searchAndCompare: false);
			s1.Task.Wait();
			engine.BuildingHashesDone -= searchDone; engine.ScanAborted -= searchDone;
			engine.FilesEnumerated -= enumDone;

			// _stopReqFor (not just the event): abort fires BuildingHashesDone first, so `aborted`
			// may still be false here even though the user stopped — see the field comment.
			if (aborted || _stopReqFor == engine) {
				engine.Progress -= prog;
				Reply(win, "scanAborted", new { });   // partial fingerprints are saved; no compare
				return;
			}

			var s2 = new TaskCompletionSource();
			void cmpDone(object? s, EventArgs e) => s2.TrySetResult();
			engine.ScanDone += cmpDone; engine.ScanAborted += cmpDone;
			Reply(win, "scanProgress", new { pct = 0, stage = "지문 생성 완료 — 중복 비교 시작" });
			engine.StartCompare();
			// A stop landing in the search→compare handoff no-ops engine-side (isScanning briefly false;
			// PrepareCompare wipes its own flags — ScanEngine.cs:853-859), so a plain Wait would sit out
			// the ENTIRE compare before honoring it. Re-issue while waiting until it takes. Compare has
			// no per-file rework to protect, so force is the designed stop semantics for this phase.
			while (!s2.Task.Wait(500))
				if (_stopReqFor == engine) engine.Stop(force: true);
			engine.Progress -= prog;

			if (aborted || _stopReqFor == engine) { Reply(win, "scanAborted", new { }); return; }

			ApplyGroupBlacklist(engine.Duplicates);
			_lastDupes = engine.Duplicates;   // for on-demand thumbnails
			var groups = BuildGroups(engine.Duplicates, out int totalGroups);
			string why = string.Join(" › ", _cfg.priority.Take(3).Select(LabelFor));
			Reply(win, "groups", new { totalGroups, shown = groups.Count, items = engine.Duplicates.Count, why, groups });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message, fatal = true }); }   // ends the long op — see RunCompare
		finally { _activeScan = null; _stopReqFor = null; }   // runs under _engineLock — strictly before the next op starts
	}

	static List<object> BuildGroups(IEnumerable<DuplicateItem> dupes, out int totalGroups) {
		var byGroup = dupes.GroupBy(d => d.GroupId)
						   .Select(g => g.ToList())
						   .Where(g => g.Count >= 2)
						   .ToList();
		totalGroups = byGroup.Count;

		// Most-reclaimable first: order by bytes held in the non-keep members.
		var withKeep = byGroup
			.Select(items => { var keep = PickKeep(items); return (items, keep, waste: items.Where(x => x != keep).Sum(x => Math.Max(0, x.SizeLong))); })
			.ToList();
		// Bake the keeper of EVERY group (not just the 150 sent) — this is what the UI displays, so
		// ReElectKeeps must match against it (not a PickKeep re-run: priority edits mid-session or an
		// already-dead higher-ranked member would miss) and Reclaim unions it into the protected set.
		lock (_sideLock) _keepByGroup = withKeep.ToDictionary(t => t.items[0].GroupId, t => t.keep.Path);
		var ordered = withKeep.OrderByDescending(t => t.waste).Take(MaxGroupsShown);

		var result = new List<object>();
		foreach (var (items, keep, _) in ordered) {
			var sims = items.Select(i => i.Similarity).OrderByDescending(x => x).ToList();
			// Closest real match: highest similarity below 100 (a partial-clip group injects a self-100
			// member; ordinary groups have none, so sims[1] would understate a chained 3+ member group).
			float simVal = sims.Where(s => s < 99.95f).DefaultIfEmpty(sims[0]).Max();
			var files = new List<object>();
			foreach (var it in items.OrderByDescending(i => i == keep).ThenByDescending(i => Height(i))) {
				bool isKeep = it == keep;
				files.Add(new {
					p = it.Path,
					t = FormatDuration(it.Duration),
					res = Height(it) is int h && h > 0 ? h + "p" : "?",
					keep = isKeep ? 1 : 0,
					bytes = it.SizeLong,
					m = isKeep ? KeepMetrics(it) : DeltaMetrics(it, keep),
				});
			}
			result.Add(new { sim = $"{Math.Round(simVal)}%", files });
		}
		return result;
	}

	// Best-quality member = the one to keep, ranked by the user's 우선순위 order (_cfg.priority),
	// with size as the final deterministic tie-break.
	static DuplicateItem PickKeep(List<DuplicateItem> items) {
		IOrderedEnumerable<DuplicateItem>? o = null;
		foreach (var c in _cfg.priority)
			if (_critSel.TryGetValue(c, out var sel))
				o = o == null ? items.OrderByDescending(sel) : o.ThenByDescending(sel);
		o = o?.ThenByDescending(i => i.SizeLong) ?? items.OrderByDescending(i => i.SizeLong);
		return o.First();
	}

	static string[] KeepMetrics(DuplicateItem k) => new[] {
		Height(k) is int h && h > 0 ? h + "p" : "?",
		k.Size,
		$"{k.BitRateKbs.ToString("#,0", CultureInfo.InvariantCulture)}k",
		FormatFps(k.Fps),
		string.IsNullOrEmpty(k.HdrFormat) ? "SDR" : k.HdrFormat,
	};

	// Deltas vs the keep member — negative = "what you lose". JS colors on the − / ! / = prefixes.
	static string[] DeltaMetrics(DuplicateItem o, DuplicateItem k) {
		int oh = Height(o), kh = Height(k);
		string res = oh == 0 ? "?" : oh < kh ? $"{U_MINUS}{oh}p" : oh == kh ? "=" : $"+{oh}p";

		double gb = (o.SizeLong - k.SizeLong) / 1_000_000_000.0;
		string size = Math.Abs(gb) < 0.05 ? "=" : (gb < 0 ? U_MINUS : "+") + Math.Abs(gb).ToString("0.0") + "G";

		decimal br = o.BitRateKbs - k.BitRateKbs;
		string bit = Math.Abs(br) < 1 ? "=" : (br < 0 ? U_MINUS : "+") + Math.Abs(br).ToString("#,0", CultureInfo.InvariantCulture);

		var dd = o.Duration - k.Duration;
		string dur = Math.Abs(dd.TotalSeconds) < 1 ? "=" : (dd < TimeSpan.Zero ? U_MINUS : "+") + FormatDuration(dd.Duration());

		string hdr = !string.IsNullOrEmpty(k.HdrFormat) && string.IsNullOrEmpty(o.HdrFormat) ? "none!"
				   : k.HdrFormat == o.HdrFormat ? "=" : (string.IsNullOrEmpty(o.HdrFormat) ? "SDR" : o.HdrFormat);
		return new[] { res, size, bit, dur, hdr };
	}

	static int Height(DuplicateItem d) {
		var fs = d.FrameSize;
		if (string.IsNullOrEmpty(fs)) return 0;
		int x = fs.IndexOf('x');
		return x >= 0 && int.TryParse(fs.AsSpan(x + 1), out int h) ? h : 0;
	}

	static string FormatFps(float f) =>
		(Math.Abs(f - Math.Round(f)) < 0.01f ? f.ToString("0", CultureInfo.InvariantCulture)
			: f.ToString("0.##", CultureInfo.InvariantCulture)) + "fps";

	static string FormatDuration(TimeSpan t) =>
		t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

	// ---------- Settings (mpvGrid path) ----------
	static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "photino-settings.json");
	static Cfg _cfg = LoadCfg();

	// Persisted config. Rule defaults mirror VDF.Core Settings defaults so a fresh install
	// reproduces the engine's out-of-the-box behavior.
	sealed class Cfg {
		public string mpvGridPath { get; set; } = "";
		public float percent { get; set; } = 96f;        // Settings.Percent (grayscale similarity %)
		public float phashGray { get; set; } = 90f;      // Settings.PHashGrayVerifyPercent
		public double durationPct { get; set; } = 20d;   // Settings.PercentDurationDifference
		public bool usePHash { get; set; }               // Settings.UsePHashing (default off => grayscale %)
		public bool compareFlipped { get; set; }         // Settings.CompareHorizontallyFlipped
		public bool ignoreBW { get; set; }               // Settings.IgnoreBlack/WhitePixels
		public bool includeSubDirs { get; set; } = true; // Settings.IncludeSubDirectories
		public bool includeImages { get; set; } = true;  // Settings.IncludeImages
		public bool entireDb { get; set; } = true;       // Settings.ScanAgainstEntireDatabase
		public bool filterBySize { get; set; }           // Settings.FilterByFileSize
		public int minFileSizeMB { get; set; }           // Settings.MinimumFileSize (MB); 0 = 하한 없음
		public int maxFileSizeMB { get; set; }           // Settings.MaximumFileSize (MB); 0 = 상한 없음 (엔진은 리터럴 비교 — 0을 그대로 주면 전부 제외됨)
		public string[] priority { get; set; } = { "hdr", "res", "bitrate", "duration", "size" };  // keep-selection order
		public string[] sources { get; set; } = System.Array.Empty<string>();  // ① 소스 folders to scan (empty => scan refused)
		public string[] excludes { get; set; } = System.Array.Empty<string>(); // ① 제외 → Settings.BlackList
		public bool realDbMode { get; set; }              // OPT-IN: use the real DB folder instead of the copy
		public string realDbFolder { get; set; } = "";    // the user's real ScannedFiles.db folder

		// ---- full engine options (환경설정) — names/defaults mirror VDF.Core Settings.cs ----
		// 스캔 성능
		public int maxDegreeOfParallelism { get; set; } = 1;
		public int hddMaxDegreeOfParallelism { get; set; } = 2;
		public bool adaptiveConcurrency { get; set; } = true;
		public int adaptiveWindowSeconds { get; set; } = 120;
		public string hardwareAccelerationMode { get; set; } = "none";
		public bool useNativeFfmpegBinding { get; set; }
		public int parallelAudioDecodeThreads { get; set; }
		// 드라이브당 동시 오디오 지문 읽기 수. 1 = 기존 동작(스핀들 1스트림). 지문이 읽기보다
		// 디코드에 묶여 있으면(길고 저비트레이트 파일) 올릴수록 유휴 코어가 채워진다.
		public int audioReadersPerDrive { get; set; } = 1;
		// 적응 동시성의 드라이브별 시작 파일 수. 기본 4는 매우 보수적이고 +1/윈도로만 오르므로,
		// 디코드에 묶인 라이브러리에선 램프 내내 코어가 남는다 (측정: 4→16 에서 2.1배).
		public int adaptiveStartConcurrency { get; set; } = 4;
		// FFmpeg 공유 라이브러리(avcodec-62.dll 등) 폴더. 비우면 EnsureNativeFFmpegOnPath 가 찾아 채운다 —
		// 개발 빌드 폴더와 설치 폴더가 갈릴 때 DLL 250MB 를 복사하지 않기 위한 것.
		public string ffmpegLibFolder { get; set; } = "";
		// 썸네일
		public int thumbnailCount { get; set; } = 1;
		public int thumbnailMaxWidth { get; set; } = 100;
		// 파일 처리
		public bool ignoreReadOnlyFolders { get; set; }
		public bool ignoreReparsePoints { get; set; }
		public bool excludeHardLinks { get; set; }
		public bool includeNonExistingFiles { get; set; }
		public bool useExifCreationDate { get; set; }
		public bool alwaysRetryFailedSampling { get; set; }
		public int maxSamplingRetryAttempts { get; set; } = 2;
		public bool autoDeleteUnrecoverableFiles { get; set; }   // destructive — surfaced with a warning
		// 매칭 고급
		public int threshhold { get; set; } = 5;
		public double durationDifferenceMinSeconds { get; set; }
		public double durationDifferenceMaxSeconds { get; set; }
		public double maxSamplingDurationSeconds { get; set; }
		public int folderMatchMode { get; set; }          // 0 None · 1 SameFolderOnly · 2 DifferentFolderOnly
		public int sameFolderDepth { get; set; } = 1;
		// 부분 클립 (오디오 지문)
		public bool enablePartialClipDetection { get; set; }
		public int audioCompareMethod { get; set; }       // 0 BruteForce · 1 InvertedIndex
		public double partialClipMinRatio { get; set; } = 0.10;
		public double partialClipSimilarityThreshold { get; set; } = 0.80;
		public bool partialClipRequireVisualMatch { get; set; } = true;
		public double partialClipVisualThreshold { get; set; } = 0.85;
		// 기타
		public string customFFArguments { get; set; } = "";
		public bool extendedFFToolsLogging { get; set; }
		public bool logExcludedFiles { get; set; }
		public int databaseCheckpointIntervalMinutes { get; set; } = 5;
	}

	static void EnsureDb() {
		if (DatabaseUtils.Database.Count == 0 && File.Exists(Path.Combine(ActiveDbFolder, "ScannedFiles.db"))) {
			DatabaseUtils.CustomDatabaseFolder = ActiveDbFolder;
			DatabaseUtils.InvalidateDatabaseFolder();
			DatabaseUtils.LoadDatabase();
		}
	}

	static string HumanBytes(long n) {
		if (n <= 0) return "0 B";
		string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
		int i = (int)Math.Floor(Math.Log(n) / Math.Log(1024));
		i = Math.Max(0, Math.Min(i, u.Length - 1));
		double v = n / Math.Pow(1024, i);
		return v.ToString(i >= 4 ? "0.00" : i >= 3 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
	}

	// keep-selection criteria (우선순위) → the DuplicateItem key each ranks by, descending.
	static readonly Dictionary<string, Func<DuplicateItem, IComparable>> _critSel = new() {
		["hdr"] = d => d.HdrFormatRank,
		["res"] = d => d.FrameSizeInt,
		["bitrate"] = d => d.BitRateKbs,
		["duration"] = d => d.Duration,
		["size"] = d => d.SizeLong,
	};
	static string LabelFor(string c) => c switch {
		"hdr" => "HDR", "res" => "해상도", "bitrate" => "비트레이트", "duration" => "재생시간", "size" => "크기", _ => c
	};

	static void ApplyRules(Settings s) {
		s.Percent = _cfg.percent;
		s.PHashGrayVerifyPercent = _cfg.phashGray;
		s.PercentDurationDifference = _cfg.durationPct;
		s.UsePHashing = _cfg.usePHash;
		s.CompareHorizontallyFlipped = _cfg.compareFlipped;
		s.IgnoreBlackPixels = s.IgnoreWhitePixels = _cfg.ignoreBW;
		s.IncludeSubDirectories = _cfg.includeSubDirs;
		s.IncludeImages = _cfg.includeImages;
		s.ScanAgainstEntireDatabase = _cfg.entireDb || s.IncludeList.Count == 0;   // whole-DB compare (#790)
		s.FilterByFileSize = _cfg.filterBySize;
		// Engine compares literally (size > Max || size < Min excludes) — an unset Max of 0 would
		// silently exclude EVERY file, so 0 means "no upper bound" here.
		s.MinimumFileSize = Math.Max(0, _cfg.minFileSizeMB);
		s.MaximumFileSize = _cfg.maxFileSizeMB > 0 ? _cfg.maxFileSizeMB : int.MaxValue;
		s.BlackList.Clear();
		foreach (var e in _cfg.excludes) s.BlackList.Add(e);

		// ---- full engine options (환경설정) ----
		s.MaxDegreeOfParallelism = _cfg.maxDegreeOfParallelism;
		s.HddMaxDegreeOfParallelism = _cfg.hddMaxDegreeOfParallelism;
		s.AdaptiveConcurrency = _cfg.adaptiveConcurrency;
		s.AdaptiveWindowSeconds = _cfg.adaptiveWindowSeconds;
		// IsDefined too: TryParse accepts any numeric string ("42" → undefined enum member fed to ffmpeg)
		s.HardwareAccelerationMode = Enum.TryParse<FFHardwareAccelerationMode>(_cfg.hardwareAccelerationMode, true, out var hw) && Enum.IsDefined(hw)
			? hw : FFHardwareAccelerationMode.none;
		s.UseNativeFfmpegBinding = _cfg.useNativeFfmpegBinding;
		s.ParallelAudioDecodeThreads = _cfg.parallelAudioDecodeThreads;
		s.AudioReadersPerDrive = _cfg.audioReadersPerDrive;
		s.AdaptiveStartConcurrency = _cfg.adaptiveStartConcurrency;
		s.ThumbnailCount = _cfg.thumbnailCount;
		s.ThumbnailMaxWidth = _cfg.thumbnailMaxWidth;
		s.IgnoreReadOnlyFolders = _cfg.ignoreReadOnlyFolders;
		s.IgnoreReparsePoints = _cfg.ignoreReparsePoints;
		s.ExcludeHardLinks = _cfg.excludeHardLinks;
		s.IncludeNonExistingFiles = _cfg.includeNonExistingFiles;
		s.UseExifCreationDate = _cfg.useExifCreationDate;
		s.AlwaysRetryFailedSampling = _cfg.alwaysRetryFailedSampling;
		s.MaxSamplingRetryAttempts = _cfg.maxSamplingRetryAttempts;
		s.AutoDeleteUnrecoverableFiles = _cfg.autoDeleteUnrecoverableFiles;
		s.Threshhold = (byte)Math.Clamp(_cfg.threshhold, 0, 255);
		s.DurationDifferenceMinSeconds = _cfg.durationDifferenceMinSeconds;
		s.DurationDifferenceMaxSeconds = _cfg.durationDifferenceMaxSeconds;
		s.MaxSamplingDurationSeconds = _cfg.maxSamplingDurationSeconds;
		s.FolderMatchMode = (FolderMatchMode)Math.Clamp(_cfg.folderMatchMode, 0, 2);
		s.SameFolderDepth = _cfg.sameFolderDepth;
		s.EnablePartialClipDetection = _cfg.enablePartialClipDetection;
		s.AudioCompareMethod = (AudioCompareMethod)Math.Clamp(_cfg.audioCompareMethod, 0, 1);
		s.PartialClipMinRatio = _cfg.partialClipMinRatio;
		s.PartialClipSimilarityThreshold = _cfg.partialClipSimilarityThreshold;
		s.PartialClipRequireVisualMatch = _cfg.partialClipRequireVisualMatch;
		s.PartialClipVisualThreshold = _cfg.partialClipVisualThreshold;
		s.CustomFFArguments = _cfg.customFFArguments;
		s.ExtendedFFToolsLogging = _cfg.extendedFFToolsLogging;
		s.LogExcludedFiles = _cfg.logExcludedFiles;
		s.DatabaseCheckpointIntervalMinutes = _cfg.databaseCheckpointIntervalMinutes;
	}

	// The whole Cfg is the option state — one message carries rules AND engine options (JS picks what it needs).
	static void ReplyRules(PhotinoWindow win) => Reply(win, "rules", _cfg);

	// DB-target / picker keys change behavior elsewhere — never settable through the generic path.
	static readonly HashSet<string> _guardedKeys = new(StringComparer.Ordinal)
		{ "realDbMode", "realDbFolder", "mpvGridPath", "sources", "excludes" };

	static void SetRule(PhotinoWindow win, JsonElement root) {
		if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object &&
			p.TryGetProperty("key", out var k) && p.TryGetProperty("val", out var v)) {
			string? key = k.GetString();
			if (key == "priority") {
				if (v.ValueKind == JsonValueKind.Array)
					_cfg.priority = v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray();
			}
			else if (key != null && !_guardedKeys.Contains(key)) {
				// Generic: every scalar Cfg property is a settable option (rules + engine options alike).
				var prop = typeof(Cfg).GetProperty(key);
				if (prop is { CanWrite: true }) {
					try {
						object? val =
							prop.PropertyType == typeof(bool) ? v.GetBoolean() :
							prop.PropertyType == typeof(int) ? (int)Math.Round(v.GetDouble()) :
							prop.PropertyType == typeof(float) ? (float)v.GetDouble() :
							prop.PropertyType == typeof(double) ? v.GetDouble() :
							prop.PropertyType == typeof(string) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) :
							null;
						if (val != null) prop.SetValue(_cfg, val);
					}
					catch { }   // wrong type from the UI → ignore, keep the old value
				}
			}
			SaveCfg();
		}
		ReplyRules(win);
	}

	static Cfg LoadCfg() {
		try { if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<Cfg>(File.ReadAllText(SettingsPath)) ?? new(); }
		catch { }
		var c = new Cfg();
		const string guess = @"C:\Users\geech\dev2\jav\mpv\mpvGrid\mpvgrid.exe";   // the old hardcode — works out of the box
		if (File.Exists(guess)) c.mpvGridPath = guess;
		return c;
	}
	static void SaveCfg() {
		try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true })); } catch { }
	}
	static void ReplySettings(PhotinoWindow win) => Reply(win, "settings", new {
		mpvGridPath = _cfg.mpvGridPath,
		mpvGridExists = File.Exists(_cfg.mpvGridPath),
		realDbMode = _cfg.realDbMode,
		realDbFolder = _cfg.realDbFolder,
		realDbHasDb = !string.IsNullOrEmpty(_cfg.realDbFolder) && File.Exists(Path.Combine(_cfg.realDbFolder, "ScannedFiles.db")),
		activeReal = ActiveDbFolder != CopyDbFolder,   // is the app actually on the real DB right now?
	});

	static void BrowseMpv(PhotinoWindow win) {
		try {
			var picked = win.ShowOpenFile("mpvGrid.exe 선택", null, false, new (string, string[])[] { ("실행 파일", new[] { "exe" }) });
			if (picked is { Length: > 0 }) { _cfg.mpvGridPath = picked[0]; SaveCfg(); }
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
		ReplySettings(win);
	}
	static void SaveSetting(PhotinoWindow win, JsonElement root) {
		if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object &&
			p.TryGetProperty("key", out var k) && p.TryGetProperty("val", out var v)) {
			string? key = k.GetString();
			// Repointing the DB the engine reads/writes MID-SCAN is unsafe: ResetActiveDb blocks on
			// _engineLock (held by the running scan for its whole duration) ON THE MESSAGE THREAD, so
			// stopScan/pauseScan become undeliverable and the window wedges; and flipping _cfg first
			// would move ActiveDbFolder under the live scan (blacklist/triage then resolve to the OTHER
			// DB's folder). Refuse until it ends — authoritative guard; the JS _busy check is only UX.
			if ((key == "realDbFolder" || key == "realDbMode") && _activeScan != null) {
				Reply(win, "settingBlocked", new { message = "스캔/비교 중에는 DB 대상을 바꿀 수 없다. 끝난 뒤 다시 시도해줘." });
				ReplySettings(win);   // snap the visual toggle/field back to the unchanged value
				return;
			}
			bool dbChange = false;
			switch (key) {
				case "mpvGridPath": _cfg.mpvGridPath = v.GetString() ?? ""; break;
				case "realDbFolder": _cfg.realDbFolder = v.GetString() ?? ""; dbChange = true; break;
				case "realDbMode": _cfg.realDbMode = v.ValueKind == JsonValueKind.True; dbChange = true; break;
			}
			SaveCfg();
			if (dbChange) ResetActiveDb(win);
		}
		ReplySettings(win);
	}

	// The active DB folder just changed (mode toggle OR a new folder via either the text field or 찾아보기):
	// drop the in-memory DB (NO save) + reset the resolved folder so the next stats/compare reloads from the
	// newly-active folder, and refresh the UI. MUST be called by EVERY "DB target changed" path — otherwise
	// the engine keeps reading/writing the OLD folder while the UI shows the new one (wrong-index corruption).
	static void ResetActiveDb(PhotinoWindow win) {
		lock (_engineLock) {
			try { DatabaseUtils.Database.Clear(); } catch { }
			DatabaseUtils.InvalidateDatabaseFolder();
			DatabaseUtils.CustomDatabaseFolder = ActiveDbFolder;
		}
		Reply(win, "dbModeChanged", new { activeReal = ActiveDbFolder != CopyDbFolder });
	}

	static void BrowseRealDb(PhotinoWindow win) {
		// Same mid-scan wedge as SaveSetting's DB-target change (ShowOpenFolder is modal on the message
		// thread and ResetActiveDb blocks on _engineLock) — refuse before opening the picker.
		if (_activeScan != null) { Reply(win, "settingBlocked", new { message = "스캔/비교 중에는 DB 대상을 바꿀 수 없다. 끝난 뒤 다시 시도해줘." }); return; }
		bool changed = false;
		try {
			var picked = win.ShowOpenFolder("실제 DB 폴더 선택 (ScannedFiles.db 가 있는 폴더)", null, false);
			if (picked is { Length: > 0 }) { _cfg.realDbFolder = picked[0]; SaveCfg(); changed = true; }
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
		if (changed) ResetActiveDb(win);   // same invalidation as the text-field path — do NOT skip it
		ReplySettings(win);
	}

	// ---------- ① 소스 (scan folders + excludes) ----------
	static void ReplySources(PhotinoWindow win) => Reply(win, "sources", new {
		sources = _cfg.sources.Select(p => new { path = p, exists = Directory.Exists(p) }).ToArray(),
		excludes = _cfg.excludes,
	});
	static void AddSource(PhotinoWindow win) {
		try {
			var picked = win.ShowOpenFolder("스캔할 폴더 추가", null, true);
			if (picked is { Length: > 0 }) {
				_cfg.sources = _cfg.sources.Concat(picked).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
				SaveCfg();
			}
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
		ReplySources(win);
	}
	static void AddExclude(PhotinoWindow win) {
		try {
			var picked = win.ShowOpenFolder("제외할 폴더 추가", null, true);
			if (picked is { Length: > 0 }) {
				_cfg.excludes = _cfg.excludes.Concat(picked).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
				SaveCfg();
			}
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
		ReplySources(win);
	}
	// Read-only folder enumeration for the source TREE (lazy expand). Empty path → drive roots.
	// Returns immediate subdirectories only; hasChildren drives the expand arrow. Real folder names
	// are private (their library) — fine on the user's own screen; never screenshot/log the tree.
	static void ListDir(PhotinoWindow win, JsonElement root) {
		try {
			string? path = null;
			if (root.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object && pl.TryGetProperty("path", out var pe))
				path = pe.GetString();

			var entries = new List<object>();
			if (string.IsNullOrEmpty(path)) {
				foreach (var d in DriveInfo.GetDrives()) {
					try {
						if (!d.IsReady) continue;
						string label = "";
						try { label = d.VolumeLabel; } catch { }
						entries.Add(new { path = d.RootDirectory.FullName, name = d.Name + (string.IsNullOrWhiteSpace(label) ? "" : "  " + label), isDrive = true, hasChildren = true });
					}
					catch { }
				}
			}
			else {
				foreach (var dir in SafeSubdirs(path))
					entries.Add(new { path = dir, name = Path.GetFileName(dir), isDrive = false, hasChildren = HasSubdirs(dir) });
			}
			Reply(win, "dirList", new { path = path ?? "", entries });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}
	// Same enumeration policy as the scanner: skip Hidden/System/ReparsePoint (avoids symlink loops), ignore inaccessible.
	static readonly EnumerationOptions _dirOpts = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
	static List<string> SafeSubdirs(string path) {
		try { return Directory.EnumerateDirectories(path, "*", _dirOpts).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).ToList(); }
		catch { return new List<string>(); }
	}
	static bool HasSubdirs(string path) {
		try { using var e = Directory.EnumerateDirectories(path, "*", _dirOpts).GetEnumerator(); return e.MoveNext(); }
		catch { return false; }
	}

	// The source tree replaces the whole include list at once (its SelectThis/ClearSubtree touch many entries).
	static void SetSources(PhotinoWindow win, JsonElement root) {
		if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object &&
			p.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array)
			_cfg.sources = s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		SaveCfg();
		ReplySources(win);
	}

	static void RemovePathFrom(PhotinoWindow win, JsonElement root, bool exclude) {
		if (root.TryGetProperty("payload", out var p) && p.TryGetProperty("path", out var pe)) {
			var path = pe.GetString() ?? "";
			if (exclude) _cfg.excludes = _cfg.excludes.Where(x => !string.Equals(x, path, StringComparison.OrdinalIgnoreCase)).ToArray();
			else _cfg.sources = _cfg.sources.Where(x => !string.Equals(x, path, StringComparison.OrdinalIgnoreCase)).ToArray();
			SaveCfg();
		}
		ReplySources(win);
	}

	// ---------- ⑤ 정리 실행 (체크 항목 삭제 — 휴지통 기본 · 영구 삭제는 ▾ 메뉴의 명시 선택) ----------
	// 휴지통(복구 가능)이 기본. 영구 삭제는 별도 경고 문구로만. 유지본은 사용자가 화면에서 "직접
	// 체크"한 경우(overrides)에만 삭제 가능 — 구버전 GUI 에는 잠금 개념이 없어 유지본도 지울 수
	// 있었고, 그 능력을 명시 체크 + 확인 대화상자의 경고로만 되돌려준다.
	static void Reclaim(PhotinoWindow win, JsonElement root) {
		try {
			if (!root.TryGetProperty("payload", out var pl) || pl.ValueKind != JsonValueKind.Object) {
				Reply(win, "error", new { message = "정리 요청이 비어있다.", fatal = true }); return;
			}
			bool permanent = pl.TryGetProperty("permanent", out var pm) && pm.ValueKind == JsonValueKind.True;
			var overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 체크된 유지본 = 사용자의 명시 승인
			if (pl.TryGetProperty("overrides", out var ov) && ov.ValueKind == JsonValueKind.Array)
				foreach (var k in ov.EnumerateArray()) { var s = k.GetString(); if (!string.IsNullOrEmpty(s)) overrides.Add(s); }
			// Defense in depth: even if the JS sends a keeper path (stale cutState bug), we never delete it
			// without an explicit override.
			var keepers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (pl.TryGetProperty("keepers", out var ks))
				foreach (var k in ks.EnumerateArray()) { var s = k.GetString(); if (!string.IsNullOrEmpty(s)) keepers.Add(s); }
			// The client only sees the top-150 groups, so its keepers list CANNOT protect a path that is
			// keeper in an unsent group yet a checked member of a shown one. Rebuild from the FULL result
			// set (current rules) and union the UI-baked keeper map — kept current by ReElectKeeps when
			// mpvGrid kills a keeper — so every group with survivors keeps one protected copy. NO
			// File.Exists here: a stat storm over a huge result set would freeze the message thread.
			// ponytail: a keeper deleted in Explorer mid-session isn't re-detected until the next 비교.
			foreach (var g in _lastDupes.GroupBy(d => d.GroupId).Select(x => x.ToList()).Where(x => x.Count >= 2))
				keepers.Add(PickKeep(g).Path);
			lock (_sideLock) keepers.UnionWith(_keepByGroup.Values);

			var toDelete = new List<string>();
			var entryOnly = new List<string>();   // 디스크에 없는데 볼륨은 온라인 = 진짜 사라진 파일: DB 엔트리만 제거 (구버전 동작)
			int keeperSkips = 0, unsafeSkips = 0, offlineSkips = 0;
			var rootReady = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // 루트당 1회 — 죽은 UNC/외장에 경로마다 수 초씩 대기하는 폭풍 방지
			var dirOk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			if (pl.TryGetProperty("paths", out var ps))
				foreach (var p in ps.EnumerateArray()) {
					var s = p.GetString();
					if (string.IsNullOrEmpty(s)) continue;
					if (keepers.Contains(s) && !overrides.Contains(s)) { keeperSkips++; continue; }   // NEVER an unapproved keeper
					string rt = Path.GetPathRoot(s) ?? "";
					if (!rootReady.TryGetValue(rt, out bool rdy)) rootReady[rt] = rdy = DriveReady(rt);
					if (!rdy) { offlineSkips++; continue; }   // 오프라인 루트는 File.Exists 조차 부르지 않는다
					if (!File.Exists(s)) {
						// 마운트 포인트 볼륨 분리 오인 방지: 루트가 살아있어도 부모 폴더까지 닿아야
						// "진짜 사라짐"으로 본다 — 아니면 지문(톰스톤)을 통째로 날리는 오판이 된다.
						string dir = Path.GetDirectoryName(s) ?? "";
						if (!dirOk.TryGetValue(dir, out bool de)) dirOk[dir] = de = dir.Length > 0 && Directory.Exists(dir);
						if (de) entryOnly.Add(s); else offlineSkips++;
						continue;
					}
					if (!permanent && !CanRecycle(s)) { unsafeSkips++; continue; }   // no Recycle Bin → refuse (영구 삭제 메뉴로만 가능)
					toDelete.Add(s);
				}
			if (toDelete.Count == 0 && entryOnly.Count == 0) {
				Reply(win, "error", new { message = $"처리할 수 있는 파일이 없다 (유지본 제외 {keeperSkips} · 휴지통 미지원 {unsafeSkips} · 오프라인 {offlineSkips}).", fatal = true });
				return;
			}

			long bytes = toDelete.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
			int keeperDel = toDelete.Count(overrides.Contains);
			var msg = new StringBuilder();
			msg.Append(permanent
				? $"⚠ {toDelete.Count}개 파일 ({HumanBytes(bytes)})을 영구 삭제할까?\n\n휴지통을 거치지 않는다 — 복구 불가!"
				: $"{toDelete.Count}개 파일 ({HumanBytes(bytes)})을 휴지통으로 보낼까?\n\n고정 드라이브 → Windows 휴지통(복구 가능).");
			if (keeperDel > 0) msg.Append($"\n\n⚠ 이 중 {keeperDel}개는 '유지' 표시 파일이 체크되어 있다 — 정말 함께 삭제할지 확인해줘!");
			if (keeperSkips > 0) msg.Append($"\n\n※ 유지본으로 보호된 {keeperSkips}개는 건너뜀 — 삭제하려면 화면에서 해당 유지본을 직접 클릭해 체크(호박색)해야 한다.");
			if (entryOnly.Count > 0) msg.Append($"\n\n※ 디스크에 없는 {entryOnly.Count}개는 DB 엔트리만 제거된다.");
			if (unsafeSkips > 0) msg.Append($"\n\n※ 네트워크/이동식 드라이브의 {unsafeSkips}개는 휴지통이 없어 건너뜀 (영구 삭제 메뉴로는 가능).");
			if (offlineSkips > 0) msg.Append($"\n\n※ 오프라인 드라이브의 {offlineSkips}개는 건너뜀.");
			var choice = win.ShowMessage(permanent ? "영구 삭제 확인" : "정리 확인", msg.ToString(),
				PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Warning);
			if (choice != PhotinoDialogResult.Yes) { Reply(win, "reclaimCancelled", new { }); return; }

			Task.Run(() => {
				int deleted = 0, failed = 0, prog = 0, total = toDelete.Count + entryOnly.Count; long freed = 0;
				lock (_engineLock) {
					foreach (var p in entryOnly) {
						try { _dbEngine.RemoveFromDatabase(new FileEntry { Path = p }); } catch { }
						Reply(win, "removed", new { path = p, newKeeps = ReElectKeeps(p) });
						Reply(win, "reclaimProgress", new { done = ++prog, total });
					}
					foreach (var p in toDelete) {
						long len = 0; try { len = new FileInfo(p).Length; } catch { }
						try {
							if (permanent) File.Delete(p); else MoveToTrash(p);
							try { _dbEngine.RemoveFromDatabase(new FileEntry { Path = p }); } catch { }   // (DB 포함)
							freed += len; deleted++;
							// 체크된 유지본이 지워졌을 수 있다 — 그룹의 키퍼를 재선출해 UI 태그를 갱신 (sidecar 삭제와 동일 기계).
							Reply(win, "removed", new { path = p, newKeeps = ReElectKeeps(p) });
						}
						catch (Exception) { failed++; }   // never echo the path
						Reply(win, "reclaimProgress", new { done = ++prog, total });
					}
					// (DB 포함)을 재시작 후에도 보장 — 구버전 DeleteInternal 도 끝에 SaveDatabase 를 불렀다.
					if (deleted > 0 || entryOnly.Count > 0) try { ScanEngine.SaveDatabase(); } catch { }
				}
				Reply(win, "reclaimDone", new { deleted, failed, skipped = keeperSkips + unsafeSkips + offlineSkips, entryOnly = entryOnly.Count, freed = HumanBytes(freed), permanent });
			});
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message, fatal = true }); }
	}

	// 드라이브 루트가 온라인인지 (오프라인 외장/언마운트 구분용). UNC 는 DriveInfo 가 못 다루므로
	// 디렉터리 존재로 판정 (접근 불가 = 오프라인 취급 — 죽은 공유에선 수 초 걸릴 수 있지만 드물다).
	static bool DriveReady(string root) {
		if (string.IsNullOrEmpty(root)) return false;
		try {
			if (root.StartsWith(@"\\", StringComparison.Ordinal)) return Directory.Exists(root);
			return new DriveInfo(root).IsReady;
		}
		catch { return false; }
	}

	// The Recycle Bin exists only on FIXED local drives. On UNC/network + removable media there is none,
	// and SendToRecycleBin would then SILENTLY PERMANENT-delete (with FOF_NOCONFIRMATION suppressing the
	// "too big / can't recycle" prompt). So we refuse those targets entirely rather than destroy files
	// behind a "recoverable" promise.
	static bool CanRecycle(string path) {
		if (!OperatingSystem.IsWindows()) return false;
		try {
			string full = Path.GetFullPath(path);
			if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // UNC / network share
			var root = Path.GetPathRoot(full);
			if (string.IsNullOrEmpty(root)) return false;
			return new DriveInfo(root).DriveType == DriveType.Fixed;
		}
		catch { return false; }
	}

	static void MoveToTrash(string path) {
		if (!CanRecycle(path))   // belt — never silently permanent-delete
			throw new IOException("휴지통이 없는 드라이브 — 건너뜀");
		Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
			Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
			Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);   // recoverable on fixed drives
	}

	// Headless recycle check — deletes ONE throwaway file to the Recycle Bin. Never a real library file.
	static void TrashTest(string file) {
		Console.WriteLine($"[trashtest] before: exists={File.Exists(file)}");
		MoveToTrash(file);
		Console.WriteLine($"[trashtest] after: exists={File.Exists(file)} (should be False; recoverable from Recycle Bin)");
	}

	// ---------- 썸네일 (on-demand frame previews for the visible review cards) ----------
	static readonly object _thumbLock = new();
	static readonly ScanEngine _thumbEngine = new();

	// Extract 1 small JPEG frame per requested file (reads the real video, read-only) → base64 data URI.
	// Uses its OWN engine/lock — RetrieveThumbnailsForItems only touches the passed items' files, never the
	// static DB, so it must not block compare/scan on _engineLock. Items cache their ImageList across calls.
	static void GetThumbs(PhotinoWindow win, List<string> want) {
		try {
			if (want.Count == 0) return;
			var wantSet = new HashSet<string>(want, StringComparer.OrdinalIgnoreCase);
			var items = _lastDupes.Where(d => wantSet.Contains(d.Path) && File.Exists(d.Path)).ToList();
			if (items.Count == 0) return;
			lock (_thumbLock) {
				_thumbEngine.Settings.ThumbnailCount = 1;
				_thumbEngine.Settings.ThumbnailMaxWidth = 160;
				_thumbEngine.RetrieveThumbnailsForItems(items).GetAwaiter().GetResult();
			}
			foreach (var it in items) {
				var img = it.ImageList is { Count: > 0 } ? it.ImageList[^1] : null;
				if (img is { Length: > 0 })
					Reply(win, "thumb", new { path = it.Path, uri = "data:image/jpeg;base64," + Convert.ToBase64String(img) });   // bytes, not a path
			}
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// ---------- 순회비교: launch mpvGrid on the visible groups + sync its deletions back ----------
	// Manifest = line-per-file .groups (blank line between groups), UTF-8 no BOM, passed as a FILE
	// (not argv) so Japanese paths survive. mpvGrid's file_delete.lua appends deleted/merged paths
	// to <manifest>.deleted; we tail that sidecar and drive row removal/rename in the UI.
	static readonly object _sideLock = new();
	// group → the keeper path the UI currently TAGS (baked by BuildGroups, promoted by ReElectKeeps).
	static Dictionary<Guid, string> _keepByGroup = new();
	static long _sideOffset;
	static FileSystemWatcher? _sideWatcher;
	static readonly ScanEngine _dbEngine = new();

	static void HandleMpvCompare(PhotinoWindow win, JsonElement root) {
		try {
			if (!File.Exists(_cfg.mpvGridPath)) {
				Reply(win, "error", new { message = "mpvGrid.exe 경로가 없다. 환경설정에서 지정해줘." });
				return;
			}
			var groups = new List<List<string>>();
			if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("groups", out var gs))
				foreach (var g in gs.EnumerateArray()) {
					var paths = new List<string>();
					foreach (var p in g.EnumerateArray()) {
						var s = p.GetString();
						if (!string.IsNullOrEmpty(s) && File.Exists(s)) paths.Add(s);
					}
					if (paths.Count >= 2) groups.Add(paths);
				}
			if (groups.Count == 0) { Reply(win, "error", new { message = "순회할 그룹이 없다 (2편+ 영상 필요)." }); return; }

			string manifest = Path.Combine(Path.GetTempPath(), "vdf_compare.groups");
			string bodyText = string.Join("\n\n", groups.Select(g => string.Join("\n", g)));
			File.WriteAllText(manifest, bodyText + "\n", new UTF8Encoding(false));
			StartSidecarWatch(win, manifest + ".deleted");
			Process.Start(new ProcessStartInfo { FileName = _cfg.mpvGridPath, UseShellExecute = false, ArgumentList = { manifest } });
			Reply(win, "compareStarted", new { groups = groups.Count, files = groups.Sum(g => g.Count) });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// CSV 내보내기 — JS sends the visible result rows (incl. check state); target picked via the
	// native save dialog; UTF-8 BOM so Excel reads Korean/Japanese paths. Real paths land in the
	// file BY the user's explicit save action.
	static void ExportCsv(PhotinoWindow win, JsonElement root) {
		try {
			if (!root.TryGetProperty("payload", out var pl) || !pl.TryGetProperty("rows", out var rs) || rs.GetArrayLength() == 0) {
				Reply(win, "error", new { message = "내보낼 결과가 없다 — 먼저 ‘비교만 다시’를 실행해줘." }); return;
			}
			string? target = win.ShowSaveFile("CSV로 내보내기",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vdf-results.csv"),
				new (string, string[])[] { ("CSV", new[] { "csv" }) });
			if (string.IsNullOrEmpty(target)) { Reply(win, "csvCancelled", new { }); return; }
			static string Esc(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
			var sb = new StringBuilder();
			sb.AppendLine("group,similarity,keep,checked,path,resolution,size_bytes,duration");
			foreach (var r in rs.EnumerateArray())
				sb.AppendLine(string.Join(",",
					r.GetProperty("g").GetInt32(),
					Esc(r.GetProperty("sim").GetString()),
					r.GetProperty("keep").GetInt32(),
					r.GetProperty("cut").GetInt32(),
					Esc(r.GetProperty("p").GetString()),
					Esc(r.GetProperty("res").GetString()),
					(long)r.GetProperty("bytes").GetDouble(),
					Esc(r.GetProperty("t").GetString())));
			File.WriteAllText(target, sb.ToString(), new UTF8Encoding(true));
			Reply(win, "csvDone", new { rows = rs.GetArrayLength(), file = Path.GetFileName(target) });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// ---------- 자동 체크 (구버전 GUI '선택' 메뉴 이식 — 메타데이터 전부는 C# 쪽에만 있어 서버측 계산) ----------
	// visible = 화면 필터를 통과한 경로들 (구버전 IsVisibleInFilter 게이팅과 같은 범위 제한).
	// 응답 cut 맵은 "명시적으로 바뀌는 항목"만 담고 JS 가 cutState 에 병합한다 (Ctrl+Z 1회로 되돌림).
	static void AutoCheck(PhotinoWindow win, JsonElement root) {
		try {
			if (!root.TryGetProperty("payload", out var pl) || pl.ValueKind != JsonValueKind.Object) return;
			string mode = pl.TryGetProperty("mode", out var md) ? md.GetString() ?? "" : "";
			static HashSet<string> PathSet(JsonElement obj, string prop) {
				var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (obj.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
					foreach (var v in arr.EnumerateArray()) { var s = v.GetString(); if (!string.IsNullOrEmpty(s)) set.Add(s); }
				return set;
			}
			var visible = PathSet(pl, "visible");
			var checkedSet = PathSet(pl, "checkedPaths");
			var snap = _lastDupes;
			var groups = snap.Where(d => visible.Contains(d.Path))
							 .GroupBy(d => d.GroupId).Select(g => g.ToList()).Where(g => g.Count >= 2).ToList();

			var cut = new Dictionary<string, bool>(StringComparer.Ordinal);
			// Keep(false)가 Check(true)를 이긴다: 한 경로가 두 그룹(일반+부분클립)에 걸릴 때 어느 한쪽의
			// 유지 판정이 다른 쪽의 체크를 무효화한다 — 충돌은 삭제 반대쪽으로 기우는 안전 규칙.
			void Check(DuplicateItem it) { if (!(cut.TryGetValue(it.Path, out var v) && !v)) cut[it.Path] = true; }
			void Keep(DuplicateItem it) => cut[it.Path] = false;
			void KeepRestCheck(List<DuplicateItem> members, DuplicateItem keep) {
				Keep(keep);
				foreach (var it in members) if (it != keep) Check(it);
			}
			// 유지본 선출은 반드시 "디스크에 실존하는" 멤버 중에서 — 외부 삭제된/톰스톤 파일이 유지본으로
			// 뽑히면 실존 사본 전부가 체크되는 재앙이 된다 (identicalButSize 의 SizeLong 가드를 전 모드로 확장).
			// 루트 준비 상태를 먼저 캐시해 오프라인 드라이브에 경로마다 stat 타임아웃이 쌓이는 것을 막는다.
			var rootReady = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			var onDiskCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			bool OnDisk(DuplicateItem i) {
				if (onDiskCache.TryGetValue(i.Path, out var ok)) return ok;
				string rt = Path.GetPathRoot(i.Path) ?? "";
				if (!rootReady.TryGetValue(rt, out var rr)) rootReady[rt] = rr = DriveReady(rt);
				return onDiskCache[i.Path] = rr && File.Exists(i.Path);
			}
			List<DuplicateItem> Live(List<DuplicateItem> g) => g.Where(OnDisk).ToList();

			string label; string note = "";
			switch (mode) {
				case "identical": {   // 100% 동일 체크 — 메타 완전 일치(EqualsFull 상당)만, 유지본(UI 첫 번째) 유지
					label = "100% 동일 체크";
					foreach (var g in groups) {
						var live = Live(g);
						if (live.Count == 0) continue;
						var keep = PickKeep(live);   // UI 정렬상 첫 번째 = 유지본 ("첫 번째 유지"와 동일)
						var same = g.Where(i => i != keep && MetaEqual(i, keep)).ToList();
						if (same.Count == 0) continue;
						Keep(keep);
						foreach (var it in same) Check(it);
					}
					break;
				}
				case "identicalButSize": {   // 그룹 전체에서 가장 작은 것 유지 (구버전도 실제로는 그룹 단위였다)
					label = "크기 빼고 100% 동일 체크";
					foreach (var g in groups) {
						var keep = g.Where(i => i.SizeLong >= 0 && OnDisk(i)).OrderBy(i => i.SizeLong).FirstOrDefault();
						if (keep == null) continue;
						KeepRestCheck(g, keep);
					}
					break;
				}
				case "lowest": {   // 최저 품질 체크 — 우선순위(규칙 화면) 기준 최고 품질만 남긴다
					label = "최저 품질 체크";
					foreach (var g in groups) {
						var live = Live(g);
						if (live.Count == 0) continue;
						KeepRestCheck(g, PickKeep(live));
					}
					break;
				}
				case "missing": {   // 사라진 파일 체크 — 온라인 볼륨에서 실제로 없어진 것만 (오프라인/마운트 분리는 오인 방지 제외)
					label = "사라진 파일 체크";
					int offline = 0;
					var dirOk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
					foreach (var it in groups.SelectMany(g => g)) {
						string rt = Path.GetPathRoot(it.Path) ?? "";
						if (!rootReady.TryGetValue(rt, out bool ok)) rootReady[rt] = ok = DriveReady(rt);
						if (!ok) { offline++; continue; }
						if (File.Exists(it.Path)) continue;
						// 루트는 살아있어도 마운트 포인트 볼륨이 분리됐을 수 있다 — 부모 폴더까지 닿아야 "진짜 사라짐"
						string dir = Path.GetDirectoryName(it.Path) ?? "";
						if (!dirOk.TryGetValue(dir, out bool de)) dirOk[dir] = de = dir.Length > 0 && Directory.Exists(dir);
						if (de) Check(it); else offline++;
					}
					if (offline > 0) note = $"오프라인/접근 불가 {offline}개 파일 제외";
					break;
				}
				case "oldest": {   // 가장 오래된 것 체크 (최신 유지)
					label = "가장 오래된 것 체크 (최신 유지)";
					foreach (var g in groups) {
						var live = Live(g);
						if (live.Count == 0) continue;
						KeepRestCheck(g, live.OrderByDescending(i => i.DateCreated).First());
					}
					break;
				}
				case "newest": {   // 가장 최신 체크 (가장 오래된 것 유지)
					label = "가장 최신 체크 (오래된 것 유지)";
					foreach (var g in groups) {
						var live = Live(g);
						if (live.Count == 0) continue;
						KeepRestCheck(g, live.OrderBy(i => i.DateCreated).First());
					}
					break;
				}
				case "custom": {   // 사용자 지정 체크 — 구버전 CustomSelection 의 핵심 필터 이식
					label = "사용자 지정 체크";
					JsonElement o = pl.TryGetProperty("opts", out var oe) && oe.ValueKind == JsonValueKind.Object ? oe : default;
					static double Num(JsonElement obj, string k, double dflt) =>
						obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : dflt;
					static bool Flag(JsonElement obj, string k) =>
						obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
					static string[] Pats(JsonElement obj, string k) {
						if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
						return v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0)
							.Select(s => s.IndexOfAny(new[] { '*', '?' }) < 0 ? "*" + s + "*" : s)   // 와일드카드 없으면 부분일치로
							.ToArray();
					}
					int ftype = (int)Num(o, "ftype", 0), ident = (int)Num(o, "ident", 0), dateSel = (int)Num(o, "dateSel", 0);
					double minMB = Num(o, "minMB", 0), maxMB = Num(o, "maxMB", 0);
					double simMin = Num(o, "simMin", 0), simMax = Num(o, "simMax", 100);
					string[] inc = Pats(o, "inc"), exc = Pats(o, "exc");
					bool skipChecked = Flag(o, "skipChecked");
					bool Pass(DuplicateItem i) {
						if (ftype == 1 && i.IsImage) return false;
						if (ftype == 2 && !i.IsImage) return false;
						double mb = i.SizeLong / (1024.0 * 1024.0);
						if (minMB > 0 && mb < minMB) return false;
						if (maxMB > 0 && mb > maxMB) return false;
						if (i.Similarity < simMin - 0.001 || i.Similarity > simMax + 0.001) return false;
						if (inc.Length > 0 && !inc.Any(w => FileSystemName.MatchesSimpleExpression(w, i.Path, true))) return false;
						if (exc.Any(w => FileSystemName.MatchesSimpleExpression(w, i.Path, true))) return false;
						return true;
					}
					foreach (var g in groups) {
						if (skipChecked && g.Any(i => checkedSet.Contains(i.Path))) continue;
						var members = g.Where(Pass).ToList();
						if (ident == 1 && members.Count >= 2) {   // "완전 동일만": 유지본과 메타가 같은 부분집합으로 좁힌다
							var rf = PickKeep(members);
							members = members.Where(i => i == rf || MetaEqual(i, rf)).ToList();
						}
						if (members.Count < 2) continue;
						var liveM = Live(members);
						if (liveM.Count == 0) continue;   // 실존 멤버가 없으면 유지본을 못 뽑는다 — 그룹 스킵
						var keep = dateSel == 1 ? liveM.OrderBy(i => i.DateCreated).First()      // 가장 오래된 것 유지
								 : dateSel == 2 ? liveM.OrderByDescending(i => i.DateCreated).First()   // 가장 최신 유지
								 : PickKeep(liveM);                                              // 우선순위 기준
						KeepRestCheck(members, keep);
					}
					break;
				}
				default: Reply(win, "error", new { message = $"알 수 없는 자동 체크 모드 '{mode}'" }); return;
			}
			Reply(win, "autoChecked", new {
				cut,
				@checked = cut.Count(kv => kv.Value),
				@unchecked = cut.Count(kv => !kv.Value),
				label, note,
			});
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// EqualsFull 상당 (구버전 DuplicateItemVM.EqualsFull): 크기 포함 모든 스트림 메타가 같아야 100% 동일.
	static bool MetaEqual(DuplicateItem a, DuplicateItem b) =>
		a.SizeLong == b.SizeLong && a.Duration == b.Duration && a.FrameSizeInt == b.FrameSizeInt &&
		a.Format == b.Format && a.AudioFormat == b.AudioFormat && a.AudioChannel == b.AudioChannel &&
		a.AudioSampleRate == b.AudioSampleRate && a.BitRateKbs == b.BitRateKbs && a.Fps == b.Fps;

	// ---------- 체크된 항목 정리 시뮬레이션 보고서 (dry-run JSON — 구버전 스키마 그대로) ----------
	// 아무것도 삭제/변경하지 않는다. 구버전 CleanupDryRunReport 와 같은 필드 구성(CreatedAt/
	// EstimatedTotalSavingsBytes/Groups[GroupId·EstimatedSavingsBytes·Reason·RemoveItems·KeepItems]).
	static void DryRunReport(PhotinoWindow win, JsonElement root) {
		try {
			var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("paths", out var ps) && ps.ValueKind == JsonValueKind.Array)
				foreach (var p in ps.EnumerateArray()) { var s = p.GetString(); if (!string.IsNullOrEmpty(s)) want.Add(s); }
			if (want.Count == 0) { Reply(win, "error", new { message = "체크된 항목이 없다 — 먼저 체크해줘." }); return; }
			var snap = _lastDupes;
			static object Dto(DuplicateItem i) => new { i.Path, SizeBytes = i.SizeLong, Resolution = i.FrameSize ?? "", i.DateCreated };
			long totalBytes = 0;
			var glist = new List<object>();
			foreach (var g in snap.GroupBy(d => d.GroupId)) {
				var rm = g.Where(i => want.Contains(i.Path)).ToList();
				if (rm.Count == 0) continue;
				long sv = rm.Sum(i => Math.Max(0, i.SizeLong));
				totalBytes += sv;
				glist.Add(new {
					GroupId = g.Key, EstimatedSavingsBytes = sv, Reason = "수동 선택",
					RemoveItems = rm.Select(Dto).ToList(),
					KeepItems = g.Where(i => !want.Contains(i.Path)).Select(Dto).ToList(),
				});
			}
			string? target = win.ShowSaveFile("정리 시뮬레이션 보고서 저장",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vdf-cleanup-dryrun.json"),
				new (string, string[])[] { ("JSON", new[] { "json" }) });
			if (string.IsNullOrEmpty(target)) { Reply(win, "dryRunCancelled", new { }); return; }
			var report = new { CreatedAt = DateTime.UtcNow, EstimatedTotalSavingsBytes = totalBytes, Groups = glist };
			File.WriteAllText(target, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
			Reply(win, "dryRunDone", new { groups = glist.Count, rows = want.Count, bytes = HumanBytes(totalBytes), file = Path.GetFileName(target) });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// ---------- 폴더로 복사 / 이동 (구버전 Copy/MoveCheckedItems 이식) ----------
	// 이동은 DB 경로를 자동 갱신해 지문을 보존한다 (UpdateFilePathInDatabase — 리네임과 같은 경로).
	// 복사는 DB/결과를 건드리지 않는다 (구버전은 행을 사본 쪽으로 돌려세웠지만, 원본이 남아 있는데
	// 행이 사본을 가리키면 이후 삭제가 사본을 지우는 함정이라 여기선 의도적으로 두지 않는다).
	static void CopyMoveTo(PhotinoWindow win, JsonElement root, bool move) {
		string act = move ? "이동" : "복사";
		string cancelCmd = move ? "moveCancelled" : "copyCancelled";
		try {
			var paths = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int missing = 0;
			if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("paths", out var ps) && ps.ValueKind == JsonValueKind.Array)
				foreach (var p in ps.EnumerateArray()) {
					var s = p.GetString();
					if (string.IsNullOrEmpty(s) || !seen.Add(s)) continue;
					if (File.Exists(s)) paths.Add(s); else missing++;
				}
			if (paths.Count == 0) { Reply(win, "error", new { message = $"{act}할 파일이 없다 (디스크에 없는 파일 {missing}개 제외).", fatal = true }); return; }
			var picked = win.ShowOpenFolder($"{act}할 대상 폴더 선택", null, false);
			if (picked is not { Length: > 0 }) { Reply(win, cancelCmd, new { }); return; }
			string dest = picked[0];
			long bytes = paths.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
			var choice = win.ShowMessage($"{act} 확인",
				$"체크된 {paths.Count}개 파일 ({HumanBytes(bytes)})을\n{dest}\n(으)로 {act}할까?" +
				(move ? "\n\n이동 후 DB 경로가 자동 갱신된다 (지문 유지)." : "\n\n원본은 그대로 두고 사본을 만든다.") +
				"\n이름 충돌은 _0, _1… 을 붙여 해결." +
				(missing > 0 ? $"\n\n※ 디스크에 없는 {missing}개는 제외됨." : ""),
				PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Question);
			if (choice != PhotinoDialogResult.Yes) { Reply(win, cancelCmd, new { }); return; }

			Task.Run(() => {
				int done = 0, failed = 0, skipped = 0, prog = 0;
				lock (_engineLock) {   // move 는 DB 를 만진다; copy 도 스캔과의 파일 경합을 피해 직렬화
					foreach (var p in paths) {
						prog++;
						try {
							string dir = Path.GetDirectoryName(p) ?? "";
							if (string.Equals(dir.TrimEnd('\\', '/'), dest.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }   // 이미 대상 폴더에 있음
							string target = UniqueTarget(dest, Path.GetFileName(p));
							if (move) {
								FileEntry? fe = null;
								try { ScanEngine.GetFromDatabase(p, out fe); } catch { }
								File.Move(p, target);
								if (fe != null) try { ScanEngine.UpdateFilePathInDatabase(target, fe); } catch { }
								foreach (var it in _lastDupes.Where(d => d.Path.Equals(p, StringComparison.OrdinalIgnoreCase)).ToList()) it.Path = target;
								lock (_sideLock)
									foreach (var k in _keepByGroup.Where(kv => kv.Value.Equals(p, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
										_keepByGroup[k] = target;   // 이동된 유지본도 계속 보호
								// 'renamed' 가 아니라 'moved': JS 가 행/체크 상태를 새 경로로 옮긴 뒤 체크를 해제한다 —
								// 이동 = 이미 처분된 파일인데 체크가 남으면 다음 정리가 방금 옮긴 사본을 지워버린다.
								Reply(win, "moved", new { oldPath = p, newPath = target });
							}
							else File.Copy(p, target);
							done++;
						}
						catch { failed++; }
						finally { Reply(win, "fileOpProgress", new { op = act, done = prog, total = paths.Count }); }   // continue(스킵)에도 진행은 간다
					}
					if (move && done > 0) try { ScanEngine.SaveDatabase(); } catch { }   // 경로 갱신을 재시작 후에도 보존
				}
				Reply(win, move ? "moveDone" : "copyDone", new { done, failed, skipped, dest });
			});
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message, fatal = true }); }
	}

	// 대상 폴더 내 이름 충돌 → name_0.ext, name_1.ext … (구버전 FileUtils.CopyFile 과 같은 규칙)
	static string UniqueTarget(string destDir, string fileName) {
		string name = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
		string t = Path.Combine(destDir, fileName);
		int c = 0;
		while (File.Exists(t)) t = Path.Combine(destDir, name + "_" + c++ + ext);
		return t;
	}

	// ---------- 트리아지 영속화 (검토 체크 상태 — 재시작·재비교 생존) ----------
	// The fingerprint DB already persists the EXPENSIVE part; what a restart loses is the user's
	// triage (cutState). Path-keyed, so one saved file re-applies cleanly after any re-compare.
	// Lives beside the DB: each DB (copy/real) keeps its own triage.
	static string TriageFile => Path.Combine(ActiveDbFolder, "photino-triage.json");

	// ALWAYS replies — the JS gates its debounced saves on this round-trip, so a missing/corrupt file
	// must still answer (silence would leave saving disabled for the whole session).
	static void ReplyTriage(PhotinoWindow win) {
		Dictionary<string, bool>? cut = null;
		try { if (File.Exists(TriageFile)) cut = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(TriageFile)); }
		catch { }   // corrupt/absent triage is not an error — it's only check state
		Reply(win, "triage", new { cut = cut ?? new Dictionary<string, bool>() });
	}

	static void SaveTriage(JsonElement root) {
		try {
			if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("cut", out var c) && c.ValueKind == JsonValueKind.Object)
				File.WriteAllText(TriageFile, c.GetRawText());
		}
		catch { }
	}

	// ---------- DB 뷰어 (인덱스 열람 — 페이지드, 검색, 엔트리 제거) ----------
	const int DbPageSize = 200;

	static void DbQuery(PhotinoWindow win, JsonElement root) {
		try {
			EnsureDb();
			string q = ""; int off = 0;
			if (root.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object) {
				if (pl.TryGetProperty("q", out var qe)) q = qe.GetString() ?? "";
				if (pl.TryGetProperty("offset", out var oe) && oe.TryGetInt32(out int o)) off = Math.Max(0, o);
			}
			IEnumerable<FileEntry> src = DatabaseUtils.Database;
			if (q.Length > 0) src = src.Where(e => e.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
			var matched = src.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
			// Clamp to the last page: removing the tail entries (or a narrower search) would otherwise
			// strand the viewer on an empty page past the end.
			if (off >= matched.Count) off = Math.Max(0, (matched.Count - 1) / DbPageSize * DbPageSize);
			var rows = matched.Skip(off).Take(DbPageSize).Select(e => new {
				p = e.Path,
				size = e.FileSize > 0 ? HumanBytes(e.FileSize) : "—",
				dur = e.mediaInfo?.Duration is { TotalSeconds: > 0 } d ? FormatDuration(d) : "—",
				mod = e.DateModified.Year > 1601 ? e.DateModified.ToString("yyyy-MM-dd") : "—",
				fp = e.grayBytes is { Count: > 0 },              // visual fingerprints
				au = e.AudioFingerprint is { Length: > 0 },      // audio fingerprint
				err = e.HasMetadataError,
			}).ToList();
			Reply(win, "dbRows", new { total = DatabaseUtils.Database.Count, matched = matched.Count, offset = off, page = DbPageSize, rows });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// In-memory removal, same semantics as every other row removal (copy DB by default; a scan re-adds
	// the file if it still exists — this is "forget", not "delete").
	static void DbRemove(PhotinoWindow win, JsonElement root) {
		try {
			string? p = root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("path", out var pe) ? pe.GetString() : null;
			if (string.IsNullOrEmpty(p)) return;
			_dbEngine.RemoveFromDatabase(new FileEntry { Path = p });
			Reply(win, "dbRemoved", new { path = p, total = DatabaseUtils.Database.Count });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// NO '미존재 정리': DatabaseUtils.CleanupDatabase is a deliberate no-op (tombstone policy — a
	// missing file's fingerprint is kept so a re-download is caught; see TOMBSTONE-DESIGN.md), so the
	// button could only ever promise a prune and report 0. Per-entry ✕ (dbRemove) is the real escape hatch.

	// ---------- 중복 아님 (group blacklist — GUI 호환: DB 폴더의 BlacklistedGroups.json 공유) ----------
	static string BlacklistFile => Path.Combine(ActiveDbFolder, "BlacklistedGroups.json");

	// GUI parity: groups the user marked '중복 아님' are removed after every compare. Subset semantics
	// (GroupBlacklistFilter): a marked {A,B,C} also suppresses a later {A,B}. Oshash tokens make the
	// mark survive moves/renames. GetOsHash is a DB lookup — no file I/O here.
	static void ApplyGroupBlacklist(HashSet<DuplicateItem> dupes) {
		try {
			var list = BlacklistStore.Load(BlacklistFile);
			if (list.Count == 0) return;
			var gids = GroupBlacklistFilter.ComputeBlacklistedGroupIds(
				dupes.Select(d => (d.GroupId, d.Path, ScanEngine.GetOsHash(d.Path))), list);
			if (gids.Count == 0) return;
			int removed = dupes.RemoveWhere(d => gids.Contains(d.GroupId));
			Console.WriteLine($"[blacklist] suppressed {gids.Count} group(s), {removed} item(s)");
		}
		catch { }   // a broken blacklist must never block results (Load already quarantines corrupt files)
	}

	static void MarkNotMatch(PhotinoWindow win, JsonElement root) {
		try {
			var paths = new List<string>();
			if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("paths", out var ps))
				foreach (var p in ps.EnumerateArray()) { var s = p.GetString(); if (!string.IsNullOrEmpty(s)) paths.Add(s); }
			if (paths.Count < 2) { Reply(win, "error", new { message = "그룹 정보가 비었다." }); return; }
			var choice = win.ShowMessage("중복 아님 표시",
				$"이 그룹({paths.Count}개 파일)을 ‘중복 아님’으로 기록할까?\n\n다음 비교부터 이 조합은 결과에서 빠진다. 기록은 DB 폴더의 BlacklistedGroups.json — 지우면 되돌릴 수 있다.",
				PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Question);
			if (choice != PhotinoDialogResult.Yes) { Reply(win, "notMatchCancelled", new { }); return; }

			var entry = new HashSet<string>(PathComparer.ForCurrentPlatform);
			foreach (var p in paths) {
				entry.Add(p);
				if (ScanEngine.GetOsHash(p) is { Length: > 0 } oshash)   // survives a later move/rename
					entry.Add(GroupBlacklistFilter.OsHashToken(oshash));
			}
			var list = BlacklistStore.Load(BlacklistFile);
			list.Add(entry);
			BlacklistStore.SaveAsync(BlacklistFile, list).GetAwaiter().GetResult();

			// prune the live result set so thumbs/reclaim/keeper-map agree with the UI removal —
			// only groups FULLY covered by the payload (a shared path must not nuke an unrelated group).
			// Swap in a pruned copy (see _lastDupes): RemoveWhere here would crash a concurrent
			// ReElectKeeps on the watcher thread mid-enumeration.
			var pset = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
			var snap = _lastDupes;
			var gids = snap.GroupBy(d => d.GroupId)
						   .Where(g => g.All(i => pset.Contains(i.Path)))
						   .Select(g => g.Key).ToHashSet();
			if (gids.Count > 0) {
				_lastDupes = new HashSet<DuplicateItem>(snap.Where(d => !gids.Contains(d.GroupId)));
				lock (_sideLock) foreach (var gid in gids) _keepByGroup.Remove(gid);
			}
			Reply(win, "notMatchDone", new { paths });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// 검토 ✏ — rename in place (same folder only). The DB entry keeps its fingerprints via
	// UpdateFilePathInDatabase; _lastDupes/_keepByGroup follow so thumbs/reclaim/keeper protection
	// stay coherent; the existing JS 'renamed' handler + migrateCut carry the row and check state.
	static void RenameFile(PhotinoWindow win, JsonElement root) {
		try {
			string? p = null, newName = null;
			if (root.TryGetProperty("payload", out var pl)) {
				if (pl.TryGetProperty("path", out var pe)) p = pe.GetString();
				if (pl.TryGetProperty("newName", out var ne)) newName = ne.GetString()?.Trim();
			}
			if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(newName)) return;
			if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Reply(win, "error", new { message = "파일명에 쓸 수 없는 문자가 있다." }); return; }
			if (!File.Exists(p)) { Reply(win, "error", new { message = "파일이 없다 — 이동/삭제된 듯. ‘비교만 다시’로 갱신해줘." }); return; }
			string np = Path.Combine(Path.GetDirectoryName(p)!, newName);
			if (string.Equals(np, p, StringComparison.Ordinal)) return;
			// case-only rename of the SAME file is allowed; anything else colliding is refused
			if (File.Exists(np) && !string.Equals(np, p, StringComparison.OrdinalIgnoreCase)) { Reply(win, "error", new { message = "같은 이름의 파일이 이미 있다." }); return; }
			File.Move(p, np);
			try {
				if (DatabaseUtils.Database.TryGetValue(new FileEntry { Path = p }, out var fe) && fe != null)
					ScanEngine.UpdateFilePathInDatabase(np, fe);   // fingerprints survive the rename
			}
			catch { }
			foreach (var it in _lastDupes.Where(d => d.Path.Equals(p, StringComparison.OrdinalIgnoreCase)).ToList()) it.Path = np;
			lock (_sideLock)
				foreach (var k in _keepByGroup.Where(kv => kv.Value.Equals(p, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
					_keepByGroup[k] = np;   // a renamed KEEPER must stay protected
			Reply(win, "renamed", new { oldPath = p, newPath = np });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	// 검토 카드의 ▶/📂 — the user's own click on their own file; paths never leave the machine.
	static void OpenPath(PhotinoWindow win, JsonElement root, bool reveal) {
		try {
			string? p = root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("path", out var pe) ? pe.GetString() : null;
			if (string.IsNullOrEmpty(p) || !File.Exists(p)) { Reply(win, "error", new { message = "파일이 없다 — 이동/삭제된 듯. ‘비교만 다시’로 갱신해줘." }); return; }
			if (reveal) Process.Start("explorer.exe", $"/select,\"{p}\"");
			else Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message }); }
	}

	static void StartSidecarWatch(PhotinoWindow win, string sidecar) {
		lock (_sideLock) {
			try { File.WriteAllText(sidecar, ""); _sideOffset = 0; } catch { return; }
			if (_sideWatcher is null) {
				_sideWatcher = new FileSystemWatcher(Path.GetDirectoryName(sidecar)!, Path.GetFileName(sidecar)) {
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true,
				};
				_sideWatcher.Changed += (_, _) => DrainSidecar(win, sidecar);
				_sideWatcher.Created += (_, _) => DrainSidecar(win, sidecar);
			}
		}
	}

	static void DrainSidecar(PhotinoWindow win, string sidecar) {
		var lines = new List<string>();
		lock (_sideLock) {
			try {
				using var fs = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				if (fs.Length <= _sideOffset) return;
				fs.Position = _sideOffset;
				using var sr = new StreamReader(fs, new UTF8Encoding(false), false);
				string chunk = sr.ReadToEnd();
				int nl = chunk.LastIndexOf('\n');
				if (nl < 0) return;                                     // no complete line yet
				string complete = chunk[..nl];
				_sideOffset += Encoding.UTF8.GetByteCount(complete) + 1;
				foreach (var ln in complete.Split('\n')) { var t = ln.Trim(); if (t.Length > 0) lines.Add(t); }
			}
			catch { return; }
		}
		foreach (var p in lines) {
			int tab = p.IndexOf('\t');
			if (tab > 0) {                                             // old<TAB>new = merge rename
				string oldP = p[..tab], newP = p[(tab + 1)..];
				if (File.Exists(newP) && !File.Exists(oldP)) Reply(win, "renamed", new { oldPath = oldP, newPath = newP });
			}
			else if (!File.Exists(p)) {                                // deletion
				// Serialize against compare/scan — this runs on a watcher thread and mutates the shared static DB.
				try { lock (_engineLock) _dbEngine.RemoveFromDatabase(new FileEntry { Path = p }); } catch { }   // in-memory only (copy DB)
				Reply(win, "removed", new { path = p, newKeeps = ReElectKeeps(p) });
			}
		}
	}

	// mpvGrid can delete ANY file, including a group's keeper. A keeper-less group is a trap: the UI
	// defaults every survivor to checked, so the group could be wiped wholesale. Match the removed
	// path against the UI-BAKED keeper of EVERY group it leads (one path can be keeper of a regular
	// AND a partial-clip group), re-elect among on-disk survivors, and record the promotion so a
	// chained deletion of the new keeper matches again.
	static List<string> ReElectKeeps(string removedPath) {
		var res = new List<string>();
		lock (_sideLock) {
			foreach (var g in _lastDupes.GroupBy(d => d.GroupId)) {
				if (!_keepByGroup.TryGetValue(g.Key, out var k) || !k.Equals(removedPath, StringComparison.OrdinalIgnoreCase)) continue;
				var alive = g.Where(i => File.Exists(i.Path)).ToList();
				if (alive.Count < 2) continue;   // <2 survivors → the UI dissolves the group anyway
				var nk = PickKeep(alive).Path;
				_keepByGroup[g.Key] = nk;
				if (!res.Contains(nk)) res.Add(nk);
			}
		}
		return res;
	}

	// Headless live-scan check on a SMALL test folder — exercises StartSearch → Progress.Drives → StartCompare.
	// Prints counts/percent only (no file paths). Never point this at the real library.
	static void ScanTest(string folder) {
		EnsureDb();
		var engine = new ScanEngine();
		engine.Settings.CustomDatabaseFolder = ActiveDbFolder;
		ApplyRules(engine.Settings);
		engine.Settings.IncludeList.Add(folder);
		engine.Settings.ScanAgainstEntireDatabase = false;   // only the test folder, in and out
		int last = -1;
		engine.Progress += (s, e) => {
			int pct = e.MaxPosition > 0 ? (int)(100L * e.CurrentPosition / e.MaxPosition) : 0;
			if (pct == last) return; last = pct;
			Console.WriteLine($"[scantest] {pct}% pos={e.CurrentPosition}/{e.MaxPosition} stage={e.CurrentStage} drives={e.Drives?.Length ?? 0}");
		};
		var s1 = new TaskCompletionSource();
		engine.BuildingHashesDone += (s, e) => s1.TrySetResult();
		engine.ScanAborted += (s, e) => s1.TrySetResult();
		engine.StartSearch(searchAndCompare: false);
		s1.Task.Wait();
		Console.WriteLine($"[scantest] search done. DB now {DatabaseUtils.Database.Count} entries");
		var s2 = new TaskCompletionSource();
		engine.ScanDone += (s, e) => s2.TrySetResult();
		engine.ScanAborted += (s, e) => s2.TrySetResult();
		engine.StartCompare();
		s2.Task.Wait();
		BuildGroups(engine.Duplicates, out int tg);
		Console.WriteLine($"[scantest] compare done. {engine.Duplicates.Count} dup items, {tg} groups");
	}

	// Headless check of the scan-control wiring: start a search over `dir`, request Stop shortly
	// after, expect ScanAborted to end the wait. PASS = abort observed; a scan that finishes before
	// the stop lands is inconclusive (use a bigger dir).
	static void StopTest(string dir) {
		EnsureDb();
		var engine = new ScanEngine();
		engine.Settings.CustomDatabaseFolder = ActiveDbFolder;
		ApplyRules(engine.Settings);
		engine.Settings.IncludeList.Add(dir);
		engine.Settings.ScanAgainstEntireDatabase = false;
		bool aborted = false;
		var tcs = new TaskCompletionSource();
		engine.BuildingHashesDone += (s, e) => tcs.TrySetResult();
		engine.ScanAborted += (s, e) => { aborted = true; tcs.TrySetResult(); };
		engine.StartSearch(searchAndCompare: false);
		Thread.Sleep(400);
		bool safe = engine.Stop();                        // 1st: safe drain (file phase) or hard cancel
		if (safe) { Thread.Sleep(200); engine.Stop(); }   // 2nd: force
		bool done = tcs.Task.Wait(30000);
		Thread.Sleep(100);   // BuildingHashesDone fires before ScanAborted on abort — let the second event land
		Console.WriteLine($"[stoptest] done={done} aborted={aborted} firstStopSafe={safe} => "
			+ (done && aborted ? "PASS" : done ? "COMPLETED-BEFORE-STOP (inconclusive — bigger dir)" : "FAIL timeout"));
	}

	// Headless 중복 아님 check: compare on the copy DB → blacklist the first group (same entry shape
	// MarkNotMatch writes, incl. oshash tokens) → ApplyGroupBlacklist → that group must be gone.
	// Prints counts only. Restores the blacklist file afterwards.
	static void BlacklistTest() {
		EnsureDb();
		var engine = new ScanEngine();
		engine.Settings.CustomDatabaseFolder = ActiveDbFolder;
		ApplyRules(engine.Settings);
		var tcs = new TaskCompletionSource();
		void done(object? s, EventArgs e) => tcs.TrySetResult();
		engine.ScanDone += done; engine.ScanAborted += done;
		engine.StartCompare();
		tcs.Task.Wait();
		var dupes = engine.Duplicates;
		int before = dupes.GroupBy(d => d.GroupId).Count(g => g.Count() >= 2);
		var first = dupes.GroupBy(d => d.GroupId).First(g => g.Count() >= 2).ToList();
		Guid gid = first[0].GroupId;

		string bak = BlacklistFile + ".test-bak";
		if (File.Exists(BlacklistFile)) File.Move(BlacklistFile, bak, true);
		try {
			var entry = new HashSet<string>(PathComparer.ForCurrentPlatform);
			foreach (var it in first) {
				entry.Add(it.Path);
				if (ScanEngine.GetOsHash(it.Path) is { Length: > 0 } oh) entry.Add(GroupBlacklistFilter.OsHashToken(oh));
			}
			BlacklistStore.SaveAsync(BlacklistFile, new List<HashSet<string>> { entry }).GetAwaiter().GetResult();
			ApplyGroupBlacklist(dupes);
			int after = dupes.GroupBy(d => d.GroupId).Count(g => g.Count() >= 2);
			bool gone = !dupes.Any(d => d.GroupId == gid);
			Console.WriteLine($"[blacklisttest] groups {before} -> {after}, markedGroupGone={gone} => "
				+ (gone && after < before ? "PASS" : "FAIL"));
		}
		finally {
			File.Delete(BlacklistFile);
			if (File.Exists(bak)) File.Move(bak, BlacklistFile);
		}
	}

	// Headless check for the tombstone-collision rename fix (DatabaseUtils.UpdateFilePath): renaming a
	// real entry onto a path a DEAD entry already occupies must evict the dead one and keep the real
	// fingerprints — not silently no-op and leave the moved file wearing foreign fingerprints. Operates
	// on synthetic in-memory entries; never saves.
	static void RenameTombstoneTest() {
		var db = DatabaseUtils.Database;
		string oldP = @"Z:\vdftest\real (1).mkv", newP = @"Z:\vdftest\real.mkv";
		// Synthetic entries only — the FileEntry(string) ctor stats the file, so build via the disk-free
		// Path setter (these paths don't exist).
		db.Remove(new FileEntry { Path = oldP }); db.Remove(new FileEntry { Path = newP });   // clean slate
		var real = new FileEntry { Path = oldP }; real.grayBytes[0.0] = new byte[] { 1, 2, 3 };   // the entry with the good fingerprint
		var tomb = new FileEntry { Path = newP };                                                 // a dead tombstone squatting on the target name
		db.Add(real); db.Add(tomb);
		ScanEngine.UpdateFilePathInDatabase(newP, real);
		bool hasNew = db.TryGetValue(new FileEntry { Path = newP }, out var at);
		bool isReal = hasNew && ReferenceEquals(at, real) && at!.grayBytes.Count == 1;
		bool oldGone = !db.Contains(new FileEntry { Path = oldP });
		Console.WriteLine($"[renametest] newExists={hasNew} keepsRealFingerprint={isReal} oldGone={oldGone} => "
			+ (hasNew && isReal && oldGone ? "PASS" : "FAIL"));
		db.Remove(new FileEntry { Path = newP }); db.Remove(new FileEntry { Path = oldP });   // don't leave synthetic entries behind
	}

	static void Reply(PhotinoWindow win, string cmd, object data) =>
		win.SendWebMessage(JsonSerializer.Serialize(new { cmd, data }));

	// Headless pipeline check — prints ONLY counts/aggregates (never private paths).
	static void SelfTest() {
		DatabaseUtils.CustomDatabaseFolder = ActiveDbFolder;
		DatabaseUtils.InvalidateDatabaseFolder();
		var sw = Stopwatch.StartNew();
		DatabaseUtils.LoadDatabase();
		int files = DatabaseUtils.Database.Count;
		int drives = DatabaseUtils.Database.Select(e => Path.GetPathRoot(e.Path) ?? "")
			.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
		Console.WriteLine($"[selftest] db loaded: {files} files, {drives} drives, {sw.ElapsedMilliseconds}ms");

		var engine = new ScanEngine();
		engine.Settings.CustomDatabaseFolder = ActiveDbFolder;
		ApplyRules(engine.Settings);
		var tcs = new TaskCompletionSource();
		void done(object? s, EventArgs e) => tcs.TrySetResult();
		engine.ScanDone += done; engine.ScanAborted += done;
		sw.Restart();
		engine.StartCompare();
		tcs.Task.Wait();
		sw.Stop();

		var groups = BuildGroups(engine.Duplicates, out int totalGroups);
		Console.WriteLine($"[selftest] compare: {engine.Duplicates.Count} items, {totalGroups} groups ({groups.Count} shaped for UI), {sw.ElapsedMilliseconds}ms");
	}
}
