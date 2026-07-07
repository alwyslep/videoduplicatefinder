// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GPLv3 as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//     You should have received a copy of the GNU General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	// "순회 비교": 현재 보이는(필터/정렬 적용) 중복 그룹을 매니페스트로 써서 GridPlayer(mpv 포크)에 넘긴다.
	// 실시간 나란한 재생·PgUp/PgDn 순회·DEL 삭제는 GridPlayer ComparisonManager 가 처리 → VDF 쪽엔 런처만.
	// 계약: pythonw -m gridplayer --new-window <파일>.gpcompare.json  /  매니페스트 {"groups":[[path,...],...]}
	// 삭제 동기화: GridPlayer 가 삭제/병합 경로를 <manifest>.deleted 사이드카에 append → 아래 감시기가 행 즉시 제거.
	public partial class MainWindowVM {
		// ponytail: 이 머신 전용 통합이라 GridPlayer venv 경로 하드코딩. 옮기면 이 한 줄만 수정.
		const string GridPlayerPythonW = @"C:\Users\geech\dev\gridplayer\.venv\Scripts\pythonw.exe";

		// 외부(GridPlayer) 삭제 → 목록 실시간 반영용 사이드카 감시 상태. 앱 1개 감시기 재사용.
		FileSystemWatcher? _compareWatcher;
		long _compareSidecarOffset;
		readonly object _compareSidecarLock = new();

		public ReactiveCommand<Unit, Unit> CompareInPlayerCommand => ReactiveCommand.CreateFromTask(async () => {
			// 보이는 목록(필터/정렬 반영) = DataGridCollectionView 열거; 미초기화면 IsVisibleInFilter 폴백.
			IEnumerable<DuplicateItemVM> visible = view is not null
				? view.OfType<DuplicateItemVM>()
				: Duplicates.Where(d => d.IsVisibleInFilter);

			List<List<string>> groups = visible
				.Where(d => !d.ItemInfo.IsImage)                       // 영상만(GridPlayer 재생 대상)
				.GroupBy(d => d.ItemInfo.GroupId)                      // VDF 중복 그룹 = 한 비교 세트
				.Select(g => g.Select(d => d.ItemInfo.Path)
							  .Where(File.Exists)
							  .Distinct(StringComparer.OrdinalIgnoreCase)
							  .ToList())
				.Where(paths => paths.Count >= 2)                      // 2편+만 비교 의미 있음
				.ToList();
			// 그룹을 통째로 보낸다(청킹 안 함). 큰 그룹의 동시 mpv 폭주는 GridPlayer 가 ≤4 슬라이딩
			// 윈도우로 막고(compare_window.py), 삭제 시 생존자를 유지한 채 다음 항목을 채워 한 세션에서
			// 수렴시킨다. VDF 는 후보만 넘기면 됨.

			if (groups.Count == 0) {
				await MessageBoxService.Show(App.Lang["Message.CompareNothingToCompare"]);
				return;
			}
			if (!File.Exists(GridPlayerPythonW)) {
				await MessageBoxService.Show(string.Format(App.Lang["Message.CompareGridPlayerMissing"], GridPlayerPythonW));
				return;
			}

			JsonArray groupsArr = new();
			foreach (List<string> g in groups) {
				JsonArray inner = new();
				foreach (string p in g)
					inner.Add(p);
				groupsArr.Add(inner);
			}
			string manifest = Path.Combine(Path.GetTempPath(), "vdf_compare.gpcompare.json");
			try {
				// utf-8(BOM 무); GridPlayer 는 utf-8-sig 로 읽어 BOM 유무 모두 허용.
				File.WriteAllText(manifest, new JsonObject { ["groups"] = groupsArr }.ToJsonString(), new UTF8Encoding(false));
				StartCompareSync(manifest + ".deleted");   // GridPlayer 삭제 → 목록 실시간 반영
				Process.Start(new ProcessStartInfo {
					FileName = GridPlayerPythonW,
					UseShellExecute = false,
					ArgumentList = { "-m", "gridplayer", "--new-window", manifest },
				});
			}
			catch (Exception ex) {
				Logger.Instance.Info($"Compare-in-player launch failed: {ex.Message}");
			}
		});

		// 사이드카(한 파일)만 감시. 매 실행마다 초기화(truncate + offset 0). ponytail: 감시기는 앱 수명 동안 1개 재사용(미 dispose).
		void StartCompareSync(string sidecar) {
			lock (_compareSidecarLock) {
				try {
					File.WriteAllText(sidecar, "");
					_compareSidecarOffset = 0;
				}
				catch (Exception) { return; }
				if (_compareWatcher is null) {
					_compareWatcher = new FileSystemWatcher(Path.GetDirectoryName(sidecar)!, Path.GetFileName(sidecar)) {
						NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
						EnableRaisingEvents = true,
					};
					_compareWatcher.Changed += (_, _) => DrainCompareSidecar(sidecar);
					_compareWatcher.Created += (_, _) => DrainCompareSidecar(sidecar);
				}
			}
		}

		// append된 새 줄(완결된 것만)을 offset부터 읽어 각 경로를 UI 스레드에서 처리. GridPlayer 동시쓰기 허용(ReadWrite share).
		void DrainCompareSidecar(string sidecar) {
			List<string> lines = new();
			lock (_compareSidecarLock) {
				try {
					using FileStream fs = new(sidecar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
					if (fs.Length <= _compareSidecarOffset)
						return;
					fs.Position = _compareSidecarOffset;
					using StreamReader sr = new(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
					string chunk = sr.ReadToEnd();
					int lastNl = chunk.LastIndexOf('\n');
					if (lastNl < 0)
						return;                                       // 아직 완결된 줄 없음 → 다음 이벤트까지 대기
					string complete = chunk.Substring(0, lastNl);
					_compareSidecarOffset += Encoding.UTF8.GetByteCount(complete) + 1;   // +1 = 마지막 '\n'(다음 읽기는 그 뒤부터)
					foreach (string ln in complete.Split('\n')) {
						string t = ln.Trim();
						if (t.Length > 0)
							lines.Add(t);
					}
				}
				catch (Exception) { return; }
			}
			// 탭 있는 줄 = 병합 리네임(old<TAB>new) → 행 갱신; 없으면 삭제 → 행 제거.
			foreach (string p in lines) {
				int tab = p.IndexOf('\t');
				if (tab > 0) {
					string oldP = p.Substring(0, tab);
					string newP = p.Substring(tab + 1);
					Dispatcher.UIThread.Post(() => ApplyExternalRename(oldP, newP));
				}
				else
					Dispatcher.UIThread.Post(() => ApplyExternalRemoval(p));
			}
		}

		// 파일이 실제로 사라졌으면 해당 행 제거 + 2편 미만 그룹 collapse(VDF 자체 삭제 흐름 DropSingletonGroups 재사용).
		// VDF가 존재 재확인하므로, GridPlayer가 병합 시 생존자 옛 경로를 함께 기록해도 리네임 안 됐으면 유지됨.
		void ApplyExternalRemoval(string path) {
			if (File.Exists(path))
				return;

			// Which duplicate group does this deleted file belong to? Capture it before its row goes.
			var row = Duplicates.FirstOrDefault(d =>
				string.Equals(d.ItemInfo.Path, path, StringComparison.OrdinalIgnoreCase));

			// A duplicate deletion -- a live copy still survives in the group -- drops this entry: the
			// survivor already carries the content's fingerprint, so no tombstone is needed. But deleting
			// the WHOLE group with no copy left (GridPlayer Ctrl+DEL) is a content rejection: keep exactly
			// ONE fingerprint as a tombstone so a future re-download is still caught. Keep it on the LAST
			// group member processed -- by then the earlier ones are already dropped. See TOMBSTONE-DESIGN.md.
			bool keepAsTombstone = false;
			if (row is not null) {
				var others = Duplicates.Where(d =>
					d.ItemInfo.GroupId == row.ItemInfo.GroupId &&
					!string.Equals(d.ItemInfo.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
				bool aliveSurvivor = others.Any(d => File.Exists(d.ItemInfo.Path));
				keepAsTombstone = !aliveSurvivor && others.Count == 0;
			}

			bool dbRemoved = false;
			if (!keepAsTombstone)
				dbRemoved = ScanEngine.RemoveFromDatabase(new FileEntry { Path = path });

			bool rowRemoved = false;
			for (int i = Duplicates.Count - 1; i >= 0; i--)
				if (string.Equals(Duplicates[i].ItemInfo.Path, path, StringComparison.OrdinalIgnoreCase)) {
					Duplicates.RemoveAt(i);
					rowRemoved = true;
				}
			if (!dbRemoved && !rowRemoved && !keepAsTombstone)
				return;
			if (rowRemoved) {
				DropSingletonGroups();
				RefreshGroupStats();
				view?.Refresh();
			}
			// ponytail: full serialize per external delete. GridPlayer deletes arrive
			// interactively (seconds apart) so this is fine; debounce if a bulk purge janks.
			if (dbRemoved || keepAsTombstone)
				ScanEngine.SaveDatabase();
		}

		// 병합(Shift+Del) 생존자 리네임: 사이드카 old<TAB>new. 행을 제거하지 말고 새 경로로 갱신 →
		// 3편+ 그룹에서 남은 중복쌍이 통째로 사라지지 않게(옛 경로만 지우면 생존자 행까지 collapse됐음).
		// DB 엔트리는 손대지 않음 — 다음 스캔의 oshash relink가 old→new 자가치유(재분석 0).
		void ApplyExternalRename(string oldPath, string newPath) {
			if (!File.Exists(newPath) || File.Exists(oldPath))
				return;   // 새 경로 실재 + 옛 경로 소멸일 때만(리네임 실패/롤백 방어)
			bool updated = false;
			foreach (DuplicateItemVM d in Duplicates)
				if (string.Equals(d.ItemInfo.Path, oldPath, StringComparison.OrdinalIgnoreCase)) {
					d.ItemInfo.Path = newPath;   // DuplicateItem.Path setter가 OnPropertyChanged → 셀 갱신
					updated = true;
				}
			if (updated) {
				Logger.Instance.Info($"[compare-sync] survivor renamed '{oldPath}' -> '{newPath}'");
				view?.Refresh();
			}
		}

		// 순수 로직 자가검증용(빌드와 별개): 그룹핑 규칙이 깨지면 실패. 호출부에서 직접 쓸 수 있음.
		internal static List<List<string>> BuildCompareGroups(
			IEnumerable<(Guid group, string path, bool isImage)> items, Func<string, bool> exists) =>
			items.Where(i => !i.isImage)
				 .GroupBy(i => i.group)
				 .Select(g => g.Select(i => i.path).Where(exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
				 .Where(p => p.Count >= 2)
				 .ToList();
	}
}
