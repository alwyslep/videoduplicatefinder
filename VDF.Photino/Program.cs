using System.Diagnostics;
using System.Globalization;
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
	static readonly string CopyDbFolder = Path.Combine(Path.GetTempPath(), "vdf-devdb");
	// The DB folder VDF.Core actually reads/writes — real folder only when the opt-in is on AND it exists.
	static string ActiveDbFolder => _cfg.realDbMode && !string.IsNullOrEmpty(_cfg.realDbFolder) && Directory.Exists(_cfg.realDbFolder)
		? _cfg.realDbFolder : CopyDbFolder;
	// Serializes every engine/DB operation (compare/scan/stats/driveInfo + the mpvGrid sidecar's
	// RemoveFromDatabase) — VDF.Core keeps ONE static DatabaseUtils.Database, so overlapping ops
	// on separate ScanEngine instances would race enumerate/mutate/save and tear the copy DB.
	static readonly object _engineLock = new();
	static HashSet<DuplicateItem> _lastDupes = new();   // last compare/scan results — source for on-demand thumbnails
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

	[STAThread]
	static void Main(string[] args) {
		// Guarantee the isolated copy folder EXISTS: if it is missing (fresh box / %TEMP% cleaned),
		// VDF.Core silently falls back to the exe dir for reads AND WRITES — breaking isolation. With
		// the folder present, ResolveDatabaseFolder always returns it, so every write lands on the copy.
		Directory.CreateDirectory(CopyDbFolder);   // always ensure the copy exists as the safe fallback

		if (args.Length > 0 && args[0] == "selftest") { SelfTest(); return; }
		if (args.Length > 1 && args[0] == "scantest") { ScanTest(args[1]); return; }
		if (args.Length > 1 && args[0] == "trashtest") { TrashTest(args[1]); return; }
		if (args.Length > 1 && args[0] == "stoptest") { StopTest(args[1]); return; }
		if (args.Length > 0 && args[0] == "blacklisttest") { BlacklistTest(); return; }
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
			.SetUseOsDefaultSize(false)
			.SetSize(1440, 920)   // the restore-down size
			.Center()
			.SetContextMenuEnabled(false)
			.SetDevToolsEnabled(true)
			.RegisterWebMessageReceivedHandler(OnMessage)
			.Load(index);
		// Open maximized — a data-dense app wants the space, and it sidesteps the DPI-scale SetSize
		// ambiguity (fills the work area regardless of monitor scaling).
		try { win.SetMaximized(true); } catch { }
		win.WaitForClose();
		TryDeleteLog();   // wipe on normal exit — nothing with private paths persists after use (best-effort: a hard crash mid-session leaves it)
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
				case "notMatch": MarkNotMatch(win, doc.RootElement); break;   // sync: native confirm on the message thread
				case "getSources": ReplySources(win); break;
				case "listDir": Task.Run(() => ListDir(win, doc.RootElement.Clone())); break;   // read-only folder enumeration for the source tree
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
				Reply(win, "progress", new { pct, stage = e.CurrentStage ?? "" });   // no paths
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
				}).ToArray();
				int pctAll = e.MaxPosition > 0 ? (int)(100L * e.CurrentPosition / e.MaxPosition) : 0;
				Reply(win, "scanProgress", new { pct = pctAll, pos = e.CurrentPosition, max = e.MaxPosition, stage = e.CurrentStage ?? "", drives });   // no file paths
			}
			engine.Progress += prog;
			bool aborted = false;
			void onAbort(object? s, EventArgs e) => aborted = true;
			engine.ScanAborted += onAbort;
			_activeScan = engine;   // from here 중지/일시정지 can reach it

			var s1 = new TaskCompletionSource();
			void searchDone(object? s, EventArgs e) => s1.TrySetResult();
			engine.BuildingHashesDone += searchDone; engine.ScanAborted += searchDone;
			engine.StartSearch(searchAndCompare: false);
			s1.Task.Wait();
			engine.BuildingHashesDone -= searchDone; engine.ScanAborted -= searchDone;

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

	// ---------- ⑤ 정리 실행 (recycle the checked duplicates) ----------
	// v1 is Recycle-Bin ONLY (recoverable) — no permanent-delete path exposed. A native confirm
	// gates it, and the keeper is never in `paths` (the JS sends only checked/cut items).
	static void Reclaim(PhotinoWindow win, JsonElement root) {
		try {
			if (!root.TryGetProperty("payload", out var pl) || pl.ValueKind != JsonValueKind.Object) {
				Reply(win, "error", new { message = "정리 요청이 비어있다.", fatal = true }); return;
			}
			// Defense in depth: even if the JS sends a keeper path (stale cutState bug), we NEVER delete it.
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
			int keeperSkips = 0, unsafeSkips = 0;
			if (pl.TryGetProperty("paths", out var ps))
				foreach (var p in ps.EnumerateArray()) {
					var s = p.GetString();
					if (string.IsNullOrEmpty(s) || !File.Exists(s)) continue;
					if (keepers.Contains(s)) { keeperSkips++; continue; }     // NEVER a keeper
					if (!CanRecycle(s)) { unsafeSkips++; continue; }          // no Recycle Bin → refuse (never permanent-delete)
					toDelete.Add(s);
				}
			if (toDelete.Count == 0) {
				Reply(win, "error", new { message = $"휴지통으로 보낼 수 있는 파일이 없다 (유지본 제외 {keeperSkips} · 휴지통 미지원 드라이브 {unsafeSkips}).", fatal = true });
				return;
			}

			long bytes = toDelete.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
			string extra = unsafeSkips > 0 ? $"\n\n※ 네트워크/이동식 드라이브의 {unsafeSkips}개는 휴지통이 없어 안전상 건너뜀." : "";
			var choice = win.ShowMessage("정리 확인",
				$"{toDelete.Count}개 파일 ({HumanBytes(bytes)})을 휴지통으로 보낼까?\n\n고정 드라이브 → Windows 휴지통(복구 가능). 유지본은 제외됨.{extra}",
				PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Warning);
			if (choice != PhotinoDialogResult.Yes) { Reply(win, "reclaimCancelled", new { }); return; }

			Task.Run(() => {
				int deleted = 0, failed = 0; long freed = 0;
				lock (_engineLock) {
					foreach (var p in toDelete) {
						long len = 0; try { len = new FileInfo(p).Length; } catch { }
						try {
							MoveToTrash(p);
							try { _dbEngine.RemoveFromDatabase(new FileEntry { Path = p }); } catch { }   // copy DB, in-memory
							freed += len; deleted++;
							Reply(win, "removed", new { path = p });   // drop the row live
						}
						catch (Exception) { failed++; }   // never echo the path
					}
				}
				Reply(win, "reclaimDone", new { deleted, failed, skipped = keeperSkips + unsafeSkips, freed = HumanBytes(freed) });
			});
		}
		catch (Exception ex) { Reply(win, "error", new { message = ex.Message, fatal = true }); }
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
			// only groups FULLY covered by the payload (a shared path must not nuke an unrelated group)
			var pset = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
			var gids = _lastDupes.GroupBy(d => d.GroupId)
								 .Where(g => g.All(i => pset.Contains(i.Path)))
								 .Select(g => g.Key).ToList();
			foreach (var gid in gids) {
				_lastDupes.RemoveWhere(d => d.GroupId == gid);
				lock (_sideLock) _keepByGroup.Remove(gid);
			}
			Reply(win, "notMatchDone", new { paths });
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
