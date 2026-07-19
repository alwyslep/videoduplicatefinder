using VDF.Core;
using VDF.Core.ViewModels;

namespace VDF.Web.Services {
	// 워크벤치 우측 인스펙터 공유 상태 — Results(중앙)가 "지금 보는 항목"을 세팅하면
	// InspectorPanel(우측)이 그 상세를 그린다. ScanService 와 같은 Singleton(단일 사용자 로컬 도구).
	public class InspectorState {
		public DuplicateItem? Focused { get; private set; }
		public List<DuplicateItem>? Group { get; private set; }
		public event Action? Changed;

		public void Focus(DuplicateItem item, List<DuplicateItem> group) {
			Focused = item;
			Group = group;
			Changed?.Invoke();
		}

		public void Clear() {
			Focused = null;
			Group = null;
			Changed?.Invoke();
		}
	}
}
