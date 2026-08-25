<#
.SYNOPSIS
    VDF.Photino 작업표시줄 고정이 "삭제되었거나 이동된 항목" 오류를 내는 문제를 고친다.

.DESCRIPTION
    증상: VDF.Photino.exe 를 실행 → 작업표시줄에 고정 → 사용 → 종료 → 고정 아이콘 클릭 →
    "이 바로 가기는 삭제되었거나 이동된 항목을 가리킵니다".

    원인: VDF.Photino 는 예전에 명시적 AppUserModelID 를 설정하지 않았다. 그러면 Windows 는
    exe 경로로 ID 를 추측하고, App Resolver 가 실행 중인 창을 "비슷해 보이는" 기존 VDF 바로가기에
    병합한다. 그 결과 "고정"이 새 바로가기를 만드는 대신 예전에 만들어져 타겟이 이미 죽어 있던
    바로가기를 재사용했다:

      · %APPDATA%\...\User Pinned\TaskBar\VDF.lnk
          → dedup\vdf-deploy\VDF.GUI.exe            (구버전 Avalonia 배포 폴더 — 삭제됨)
      · %APPDATA%\...\Start Menu\Programs\VDF — Deep Space.lnk
          → VDF.Photino\bin\Debug\net10.0\win-x64\VDF.Photino.exe
            (VDF.Photino.csproj 가 OutputPath 를 ..\..\VDF_Photino\ 로 돌려서 bin 폴더는 생기지도 않는다)

    앱이 떠 있는 동안은 작업표시줄 버튼이 살아있는 창이라 클릭해도 포커스만 가므로 멀쩡해 보인다.
    종료한 뒤에야 버튼이 .lnk 실행으로 바뀌고 죽은 경로를 만난다.

    이 스크립트가 하는 일:
      1. 두 바로가기를 백업한다.
      2. 타겟/작업폴더/아이콘을 실제 배포 경로(dedup\VDF_Photino)로 고쳐 쓴다.
      3. 두 바로가기에 System.AppUserModel.ID = Geech.VDF.Photino 를 심는다.
         (Program.cs 의 SetCurrentProcessExplicitAppUserModelID 와 같은 값 — 이게 짝이 맞아야
          다시는 엉뚱한 바로가기로 병합되지 않는다.)
      4. 탐색기를 재시작하고 App Resolver 캐시를 버려 잘못된 매핑을 지운다.

    고정 아이콘의 위치는 그대로 유지된다 — 파일 이름을 바꾸지 않고 내용만 고치기 때문이다
    (Taskband 레지스트리가 VDF.lnk 를 이름으로 참조한다).

    알려진 잔여 현상 (문제 없음):
      앱을 실행하면 시작 메뉴의 'VDF — Deep Space.lnk' 의 AUMID 가 창 제목값 'VDF — Deep Space' 로
      다시 덮인다. Photino.Native 가 창을 띄우는 시점에 이미 제목 기반 ID 로 작업표시줄 버튼이
      만들어지고, Windows 가 같은 exe 를 가리키는 시작 메뉴 링크에 그 값을 입양(adopt)시키기 때문이다.
      TaskbarIdentity.ApplyToWindow 의 창 수준 재정의는 그 직후에 걸린다.
      실제로 중요한 것은 "고정된 VDF.lnk 의 AUMID == 실행 중인 창의 AUMID" 이고 그건 유지된다
      (둘 다 Geech.VDF.Photino — 검증 완료). 시작 메뉴 링크는 타겟이 유효하므로 눌러도 정상 실행된다.
      원래 버그(죽은 타겟)는 두 링크 모두에서 사라졌다.

    구현 주의: WScript.Shell(IWshShortcut)은 쓰지 않는다. 경로에 비ASCII 문자가 있으면
    (여기서는 'VDF — Deep Space.lnk' 의 U+2014 EM DASH) 읽기는 조용히 빈 바로가기를 돌려주고
    쓰기는 "바로 가기를 저장할 수 없습니다" 로 실패한다. 실제로 이것 때문에 처음 진단에서
    이 바로가기의 타겟이 비어 보였다 — 원본 바이트에는 죽은 bin\Debug 경로가 멀쩡히 들어 있었다.
    전부 IShellLinkW + IPropertyStore(둘 다 LPWStr)로 다룬다.

.PARAMETER Deploy
    VDF.Photino.exe 가 있는 배포 폴더. 기본값 C:\Users\geech\dev2\jav\dedup\VDF_Photino

.PARAMETER SkipExplorerRestart
    탐색기를 재시작하지 않는다 (캐시 삭제도 건너뛴다). 바로가기 내용만 고친다.

.PARAMETER VerifyOnly
    아무것도 고치지 않고 현재 상태만 출력한다.
#>
[CmdletBinding()]
param(
	[string]$Deploy = 'C:\Users\geech\dev2\jav\dedup\VDF_Photino',
	[switch]$SkipExplorerRestart,
	[switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'

$AUMID = 'Geech.VDF.Photino'
$Exe   = Join-Path $Deploy 'VDF.Photino.exe'
$Icon  = Join-Path $Deploy 'app.ico'

if (-not $VerifyOnly -and -not (Test-Path -LiteralPath $Exe)) {
	throw "배포된 exe 가 없다: $Exe  (먼저 dotnet build -c Release 를 돌려라)"
}
if (-not (Test-Path -LiteralPath $Icon)) { $Icon = $Exe }

$Targets = @(
	(Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\VDF.lnk'),
	(Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\VDF — Deep Space.lnk')
)

# ---------------------------------------------------------------- 셸 링크 인터롭 (유니코드 안전)
if (-not ('VdfLink' -as [type])) {
	Add-Type -Language CSharp @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class VdfLink {
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, IntPtr pv);
        void SetValue(ref PROPERTYKEY key, IntPtr pv);
        void Commit();
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(IntPtr pvar);

    // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5
    private static readonly Guid FMTID = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const short VT_LPWSTR    = 31;
    private const int   STGM_READ    = 0;
    private const int   SLGP_RAWPATH = 4;   // 저장된 경로 그대로 — 셸이 추적/복구하도록 두지 않는다

    // 셸 COM 은 STA 를 요구한다. PowerShell 7 은 기본이 MTA 라 전용 STA 스레드에서 돌린다.
    private static T Sta<T>(Func<T> f) {
        T result = default(T);
        Exception err = null;
        var t = new System.Threading.Thread(() => { try { result = f(); } catch (Exception ex) { err = ex; } });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Exception(err.Message, err);
        return result;
    }

    private static void SetAumid(object link, string aumid) {
        var store = (IPropertyStore)link;
        var key = new PROPERTYKEY { fmtid = FMTID, pid = 5 };
        // PROPVARIANT: vt at offset 0, union at offset 8 (x86 16 bytes / x64 24 bytes).
        int size = IntPtr.Size == 8 ? 24 : 16;
        IntPtr pv = Marshal.AllocCoTaskMem(size);
        try {
            for (int i = 0; i < size; i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 0, VT_LPWSTR);
            Marshal.WriteIntPtr(pv, 8, Marshal.StringToCoTaskMemUni(aumid));
            store.SetValue(ref key, pv);
            store.Commit();
        } finally {
            PropVariantClear(pv);   // vt=VT_LPWSTR 이므로 문자열도 여기서 해제된다
            Marshal.FreeCoTaskMem(pv);
        }
    }

    private static string GetAumid(object link) {
        var store = (IPropertyStore)link;
        var key = new PROPERTYKEY { fmtid = FMTID, pid = 5 };
        int size = IntPtr.Size == 8 ? 24 : 16;
        IntPtr pv = Marshal.AllocCoTaskMem(size);
        try {
            for (int i = 0; i < size; i++) Marshal.WriteByte(pv, i, 0);
            store.GetValue(ref key, pv);
            if (Marshal.ReadInt16(pv, 0) != VT_LPWSTR) return "";
            IntPtr s = Marshal.ReadIntPtr(pv, 8);
            return s == IntPtr.Zero ? "" : Marshal.PtrToStringUni(s);
        } catch { return ""; }
        finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
    }

    /// 타겟/작업폴더/아이콘/설명/AUMID 를 지정해 lnk 를 그 경로에 쓴다 (있으면 덮어쓴다).
    /// 일부러 Load 하지 않는다: STGM_READ 로 연 파일을 같은 경로에 Save 하면 열기 모드가 충돌해
    /// STG_E_ACCESSDENIED(0x80030005) 가 난다. 어차피 아래에서 모든 필드를 설정하므로 읽을 게 없다.
    public static void Write(string lnkPath, string target, string workDir, string icon, string desc, string aumid) {
        Sta<object>(() => {
            object link = new ShellLink();
            try {
                var sl = (IShellLinkW)link;
                sl.SetPath(target);
                sl.SetArguments("");
                sl.SetWorkingDirectory(workDir);
                sl.SetIconLocation(icon, 0);
                sl.SetDescription(desc);
                SetAumid(link, aumid);
                ((IPersistFile)link).Save(lnkPath, true);
                return null;
            } finally { Marshal.FinalReleaseComObject(link); }
        });
    }

    /// [target, workDir, iconPath, description, aumid] — 셸 복구 없이 저장된 값 그대로 읽는다.
    public static string[] Read(string lnkPath) {
        return Sta(() => {
            object link = new ShellLink();
            try {
                ((IPersistFile)link).Load(lnkPath, STGM_READ);
                var sl = (IShellLinkW)link;
                var b = new StringBuilder(1024);
                sl.GetPath(b, b.Capacity, IntPtr.Zero, SLGP_RAWPATH);
                string target = b.ToString();
                b.Clear(); b.EnsureCapacity(1024);
                sl.GetWorkingDirectory(b, b.Capacity);
                string wd = b.ToString();
                int idx;
                b.Clear(); b.EnsureCapacity(1024);
                sl.GetIconLocation(b, b.Capacity, out idx);
                string ic = b.ToString();
                b.Clear(); b.EnsureCapacity(1024);
                sl.GetDescription(b, b.Capacity);
                string ds = b.ToString();
                return new string[] { target, wd, ic, ds, GetAumid(link) };
            } finally { Marshal.FinalReleaseComObject(link); }
        });
    }
}
'@
}

function Show-Lnk([string]$Path, [string]$Tag) {
	if (-not (Test-Path -LiteralPath $Path)) { Write-Output "  $Tag : (파일 없음)"; return }
	$r = [VdfLink]::Read($Path)
	$ok = if ($r[0] -and (Test-Path -LiteralPath $r[0])) { 'OK' } else { '### 죽은 타겟 ###' }
	Write-Output "  $Tag"
	Write-Output "    타겟     : $($r[0])   [$ok]"
	Write-Output "    작업폴더 : $($r[1])"
	Write-Output "    아이콘   : $($r[2])"
	Write-Output "    설명     : $($r[3])"
	Write-Output "    AUMID    : $($r[4])"
}

# ---------------------------------------------------------------- 진단만
if ($VerifyOnly) {
	Write-Output "현재 상태:"
	foreach ($lnk in $Targets) { Show-Lnk $lnk (Split-Path $lnk -Leaf) }
	return
}

# ---------------------------------------------------------------- 1) 백업 + 2/3) 수리
$backupDir = Join-Path $env:TEMP ("vdf-lnk-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

foreach ($lnk in $Targets) {
	if (-not (Test-Path -LiteralPath $lnk)) { Write-Warning "없음, 건너뜀: $lnk"; continue }

	Copy-Item -LiteralPath $lnk -Destination $backupDir -Force
	Write-Output ""
	Write-Output "=== $(Split-Path $lnk -Leaf) ==="
	Show-Lnk $lnk '수리 전'

	[VdfLink]::Write($lnk, $Exe, $Deploy, $Icon, 'VDF — Deep Space (Photino)', $AUMID)

	Show-Lnk $lnk '수리 후'
}
Write-Output ""
Write-Output "백업 위치: $backupDir"

# ---------------------------------------------------------------- 4) 캐시 무효화 + 탐색기 재시작
if ($SkipExplorerRestart) {
	Write-Output "탐색기 재시작 건너뜀 (-SkipExplorerRestart). 로그오프/재로그인 전까지 예전 매핑이 남아있을 수 있다."
	return
}

Write-Output ""
Write-Output "탐색기 재시작 + App Resolver 캐시 삭제…"
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1200

$cacheDir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Caches'
if (Test-Path $cacheDir) {
	Get-ChildItem $cacheDir -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
		try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; Write-Output "  삭제됨: $($_.Name)" }
		catch { Write-Output "  잠김(무시): $($_.Name)" }
	}
}

if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
Start-Sleep -Milliseconds 1500
Write-Output "완료."
