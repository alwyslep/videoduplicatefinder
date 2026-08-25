using System.Runtime.InteropServices;

namespace VDF.Photino;

// 작업표시줄 신원(AppUserModelID). Windows 는 이 ID 로 "실행 중인 창"과 "고정된 바로가기"를 짝짓는다.
//
// 왜 필요한가 — 실제로 겪은 버그 (2026-07-28):
//   VDF.Photino.exe 실행 → 작업표시줄에 고정 → 사용 → 종료 → 고정 아이콘 클릭 →
//   "이 바로 가기는 삭제되었거나 이동된 항목을 가리킵니다".
//   명시적 ID 가 없으면 Windows 는 App Resolver 로 "비슷해 보이는" 기존 바로가기를 골라 창을 병합한다.
//   그래서 "고정"이 새 바로가기를 만드는 대신, 타겟이 이미 죽어 있던 옛날 VDF 바로가기
//   (dedup\vdf-deploy\VDF.GUI.exe — 폴더째 사라진 구버전 Avalonia 배포)를 재사용해 버렸다.
//   앱이 떠 있는 동안은 버튼이 살아있는 창이라 멀쩡해 보이고, 종료한 뒤에야 죽은 .lnk 를 실행하며 터진다.
//
// 왜 프로세스 수준 설정만으로는 부족한가:
//   Photino.Native.dll 이 SetCurrentProcessExplicitAppUserModelID 를 직접 import 해서, 창을 만들면서
//   자기 값(창 제목 "VDF — Deep Space")으로 다시 부른다. Main 초입에서 우리가 먼저 부른 값은 그대로
//   덮인다 — 측정으로 확인했다. 창 수준 AUMID 는 프로세스 수준을 이기므로, 창이 생긴 직후 HWND 의
//   속성 저장소에 직접 박아야 최종 신원이 우리 것이 된다.
//
// 바로가기 쪽에도 같은 ID 를 심어야 짝이 맞는다 — tools\Fix-VdfShortcuts.ps1 이 그 일을 한다.
// 두 값이 어긋나면 고정 아이콘과 별개로 두 번째 작업표시줄 버튼이 생긴다.
static class TaskbarIdentity {
	// 제목에서 유도되지 않는 고정 문자열이어야 한다 — 제목을 바꿔도 고정이 깨지지 않도록.
	internal const string AppUserModelId = "Geech.VDF.Photino";

	[DllImport("shell32.dll", PreserveSig = false)]
	static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

	// 되읽기는 프로세스 안에서만 가능하다 — 밖에서 SHGetPropertyStoreForWindow 로 프로세스 ID 를 보면
	// 비어 있다(그 저장소는 창 고유 재정의만 담는다). 창 수준으로 박은 값은 밖에서도 읽힌다.
	[DllImport("shell32.dll", PreserveSig = false)]
	static extern void GetCurrentProcessExplicitAppUserModelID(out IntPtr AppID);

	[DllImport("shell32.dll", PreserveSig = false)]
	static extern void SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
		[MarshalAs(UnmanagedType.Interface)] out object ppv);

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	struct PropertyKey { public Guid fmtid; public uint pid; }

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
	interface IPropertyStore {
		void GetCount(out uint cProps);
		void GetAt(uint iProp, out PropertyKey pkey);
		void GetValue(ref PropertyKey key, IntPtr pv);
		void SetValue(ref PropertyKey key, IntPtr pv);
		void Commit();
	}

	[DllImport("ole32.dll")]
	static extern int PropVariantClear(IntPtr pvar);

	// PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5
	static readonly Guid PkeyFmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
	static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
	const short VT_LPWSTR = 31;

	/// 창을 만들기 전에 부른다. Photino 가 나중에 덮어쓰지만, 그 전에 만들어지는 셸 상호작용은 이 값을 쓴다.
	internal static void ApplyToProcess() {
		if (!OperatingSystem.IsWindows()) return;   // 셸 AUMID 는 Windows 전용 개념이다
		try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
		catch (Exception ex) { Console.Error.WriteLine($"[aumid] 프로세스 설정 실패: {ex.Message}"); }
	}

	/// 창 수준 AUMID — 이게 최종 작업표시줄 신원이다. WindowCreated 직후에 부른다.
	internal static void ApplyToWindow(IntPtr hwnd) {
		if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
		try {
			Guid iid = IidPropertyStore;
			SHGetPropertyStoreForWindow(hwnd, ref iid, out object o);
			var store = (IPropertyStore)o;
			try {
				var key = new PropertyKey { fmtid = PkeyFmtid, pid = 5 };
				WithLpwstrPropVariant(AppUserModelId, pv => { store.SetValue(ref key, pv); store.Commit(); });
			}
			finally { Marshal.FinalReleaseComObject(store); }
		}
		catch (Exception ex) { Console.Error.WriteLine($"[aumid] 창 설정 실패: {ex.Message}"); }
	}

	// PROPVARIANT: vt 가 offset 0, 공용체가 offset 8 (x86 16바이트 / x64 24바이트).
	// vt=VT_LPWSTR 이므로 PropVariantClear 가 문자열까지 해제한다 — 따로 FreeCoTaskMem 하면 이중 해제다.
	static void WithLpwstrPropVariant(string value, Action<IntPtr> use) {
		int size = IntPtr.Size == 8 ? 24 : 16;
		IntPtr pv = Marshal.AllocCoTaskMem(size);
		try {
			for (int i = 0; i < size; i++) Marshal.WriteByte(pv, i, 0);
			Marshal.WriteInt16(pv, 0, VT_LPWSTR);
			Marshal.WriteIntPtr(pv, 8, Marshal.StringToCoTaskMemUni(value));
			use(pv);
		}
		finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
	}

	/// `VDF.Photino.exe aumidtest` — 프로세스 수준 설정/되읽기가 실제로 동작하는지.
	/// 창 수준은 프로세스 밖에서 검증한다 (tools\Fix-VdfShortcuts.ps1 옆의 검증 스크립트 참고).
	internal static string SelfTestProcess() {
		if (!OperatingSystem.IsWindows()) return "SKIP (Windows 전용)";
		try {
			SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
			GetCurrentProcessExplicitAppUserModelID(out IntPtr p);
			string actual = Marshal.PtrToStringUni(p) ?? "(null)";
			Marshal.FreeCoTaskMem(p);   // shell32 이 CoTaskMemAlloc 으로 준다
			return $"{(actual == AppUserModelId ? "PASS" : "FAIL")} expected={AppUserModelId} actual={actual}";
		}
		catch (Exception ex) { return $"FAIL {ex.GetType().Name}: {ex.Message}"; }
	}
}
