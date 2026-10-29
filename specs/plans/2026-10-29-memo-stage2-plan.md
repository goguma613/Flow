# 메모 2단계 (루틴·할 일 ↔ 메모 연결, 매일 초기화) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 루틴·할 일에 메모를 연결해 📝 로 바로 열고, 메모마다 '매일 초기화'를 켜 하루가 바뀌면 체크 칸이 풀리게 하고, '체크 모두 풀기' 버튼을 더한다.

**Architecture:** 연결은 `data.json` 의 `Routine.MemoId`·`TaskItem.MemoId` 에만 둔다(메모 파일은 모른다). 매일 초기화는 `MemoStore` 가 머리말 `daily`·`reset` 으로 한다 — 날짜를 인자로 받는 순수한 규칙이라 시험할 수 있다. 위젯은 롤오버가 일어나는 세 자리(켤 때·하루가 바뀔 때·늦게 온 저장 폴더가 돌아올 때)에서 열린 메모를 먼저 저장하고 `ResetDaily` 를 부른다. 메모 창은 도구 줄에 ↻·모두 풀기를, 태그 줄 아래에 '연결된 할 일'을 보인다.

**Tech Stack:** .NET 10, Avalonia 12, CommunityToolkit.Mvvm. 시험은 `Tests/Flow.Tests` 자체 러너, CI(`test.yml`, Windows).

**Spec:** `specs/2026-10-29-memo-design.md` — 1장 'PC 위젯'의 할 일·루틴 줄, 'PC 메모 창' 도구 줄, 2장 '할 일·루틴 연결'·'매일 초기화', 7장 2단계.

## Global Constraints

- 연결: `Routine.MemoId`, `TaskItem.MemoId` (`Guid?`). 옛 `data.json` 은 null 로 읽힌다. 메모가 없거나 휴지통이면 📝 를 그리지 않고 연결 값은 지우지 않는다(되살리면 다시 보인다). 여러 루틴·할 일이 같은 메모를 가리킬 수 있다.
- 매일 초기화: `daily: true` 이고 `reset` 이 없거나 오늘(논리적 날짜)보다 이전인 메모의 줄 머리 체크(`[x]`·`[X]`, 들여쓰기·`- ` 포함)를 `[ ]` 로 바꾸고 `reset: 오늘` 을 적는다. 이미 오늘 푼 파일은 건드리지 않는다. 휴지통 메모는 건드리지 않는다.
- '매일 초기화'를 켜는 순간 `reset` 을 오늘로 적는다 — 켠 날 바로 풀리지 않게.
- '체크 모두 풀기'는 날짜와 상관없이 지금 푼다(`reset` 은 그대로).
- 롤오버 자리에서는 `MemoFlushRequested` 로 열린 메모를 먼저 저장한 뒤 `ResetDaily` 를 부른다. 저장 폴더를 기다리는 동안에는 아무것도 쓰지 않는다(`canWrite`).
- 화면 글: 📝 (줄 오른쪽 작은 아이콘 단추, 툴팁 "메모 열기"), 우클릭 `메모 연결` ▸ `새 메모 만들기` / 메모 제목들 / `연결 끊기`, 메모 창 ↻ 툴팁 "매일 초기화 — 하루가 바뀌면 체크가 풀려요", `모두 풀기` 툴팁 "체크를 모두 풀어요", 연결 줄 "연결: 루틴 · AK넷 배포, 할 일 · 정산".
- 버전 `1.7.0`.
- 로컬 dotnet 이 없을 수 있다: 커밋·푸시 후 `test` 워크플로 결과로 확인.

## Review Focus

1. **쓰는 중에 하루가 바뀜** — 새벽 4시에 메모를 쓰고 있으면 롤오버가 그 메모를 풀면서 쓰던 글과 엇갈린다. 먼저 저장(flush)하고, 저장이 안 되는 동안(dirty)이면 그 메모는 이번에 풀지 않고 다음 기회(다음 롤오버·다음 시작)에 푼다 → Task 1·2 시험.
2. **켠 날 바로 풀림** — '매일 초기화'를 켜자마자 체크가 사라지면 사용자가 놀란다 → Task 1 `SetDaily` 시험.
3. **지운·없는 메모를 가리키는 연결** — 📝 를 눌렀는데 아무 일도 없거나 예외 → Task 2: 📝 는 메모가 있을 때만 보이고, 열기 직전 다시 확인.
4. **같은 메모를 여러 할 일이 공유** — 하나에서 '연결 끊기'가 다른 할 일의 연결을 건드리면 안 된다 → Task 2.
5. **위젯 줄 클릭과 📝 클릭의 충돌** — 📝 를 누르면 그 할 일이 체크되면 안 된다 → Task 2(기존 rowaction 단추와 같은 방식).

---

### Task 1: 연결 필드와 매일 초기화 (데이터)

**Files:** `Flow/Models/AppData.cs`, `Flow/Services/MemoStore.cs`, `Tests/Flow.Tests/MemoTests.cs`(+`Program.cs` 직렬화 시험)

**Interfaces — Produces:**
- `Routine.MemoId`, `TaskItem.MemoId` : `Guid?`
- `bool MemoStore.SetDaily(Guid id, bool daily, DateOnly today)` — 머리말만 고침(판 그대로), 켤 때 `Reset = today`.
- `int MemoStore.ResetDaily(DateOnly today, Func<Guid, bool>? skip = null)` — 푼 메모 수. `skip(id)` 가 참이면 건너뜀(열린 메모가 저장 안 된 경우).
- `bool MemoStore.UncheckAll(Guid id)` — 본문 체크를 지금 풀고 저장(판이 바뀜, 스스로 쓴 것으로 기억).

- [ ] **Step 1: 실패하는 시험**
  ```csharp
  // 직렬화
  var d = new AppData(); d.Routines.Add(new Routine { Title = "배포", MemoId = id });
  Check("루틴 MemoId 왕복", Roundtrip(d).Routines[0].MemoId, (Guid?)id);
  Check("옛 파일은 null", Deserialize("{\"Routines\":[{\"Title\":\"a\"}]}").Routines[0].MemoId, (Guid?)null);
  // 매일 초기화 (FLOW_DATA_DIR 필요)
  var m = store.Create("절차\n[x] 하나\n  - [X] 둘\n글 [x]")!;
  Check("켜기", store.SetDaily(m.Doc.Id, true, Mon), true);
  Check("켠 날은 안 풀림", store.ResetDaily(Mon), 0);
  Check("다음 날 풀림", store.ResetDaily(Tue), 1);
  Check("줄 머리만 풂", Body(m) == "절차\n[ ] 하나\n  - [ ] 둘\n글 [x]", true);
  Check("reset 기록", Header(m).Contains("reset: " + Tue.ToString("yyyy-MM-dd")), true);
  Check("같은 날 두 번째는 0", store.ResetDaily(Tue), 0);
  Check("daily 아니면 그대로", ...);           // daily:false 메모의 [x] 유지
  Check("휴지통은 안 건드림", ...);
  Check("skip 은 건너뜀", store.ResetDaily(Wed, id => id == m.Doc.Id), 0);
  Check("skip 한 메모는 다음에 풂", store.ResetDaily(Wed), 1);
  Check("모두 풀기", store.UncheckAll(other.Doc.Id) && !Body(other).Contains("[x]"), true);
  Check("못 쓰면 아무것도", waitingStore.ResetDaily(Thu), 0);
  Check("ResetDaily 뒤 Scan 은 바뀜 없음", store.Scan(), false);
  ```
- [ ] **Step 2–4:** 구현(`MemoFormat.UncheckAll` 재사용, 쓰기는 기존 `Locate`·`WriteDoc`·`Remember` 경로) → 푸시 → `test` 초록.
- [ ] **Step 5:** 커밋 `메모: 연결 필드 · 매일 초기화 · 모두 풀기 (데이터)`.

### Task 2: 위젯 — 📝 와 연결 메뉴, 롤오버 때 매일 초기화

**Files:** `Flow/ViewModels/Rows.cs`, `Flow/ViewModels/MainViewModel.cs`, `Flow/Views/MainWindow.axaml(.cs)`, `Flow/Styles/Controls.axaml`

**Interfaces — Consumes:** Task 1 전부, `MemoOpenRequested`, `MemoFlushRequested`, `MemosChanged`, `Memos`(MemoStore), `MemoQuery.Filter`.
**Produces:** `RowBase`(또는 Routine/Task 줄)에 `bool HasMemo`, `OpenMemoCommand`, `LinkNewMemoCommand`, `LinkMemoCommand(Guid)`, `UnlinkMemoCommand`; `MainViewModel.LinkedItems(Guid memoId) → IReadOnlyList<string>` ("루틴 · 제목" / "할 일 · 제목"); `MainViewModel.MemoChoices`(연결 메뉴용, 고정 먼저·최근 순 제목 목록).

- [ ] **Step 1:** 루틴·할 일 줄 오른쪽에 📝 단추(`rowaction` 과 같은 방식 — 줄 클릭으로 번지지 않음). `HasMemo` = 연결이 있고 그 id 가 `Memos.Memos` 에 있음. 목록이 바뀔 때(`MemosChanged`, 휴지통·되살리기) 다시 계산.
- [ ] **Step 2:** 세 목록(오늘·루틴·예정)의 우클릭 메뉴에 `메모 연결` 하위 메뉴: `새 메모 만들기`(그 항목 제목으로 메모를 만들고 연결·저장·메모 창 열기), 구분선, 메모 제목들(지금 연결된 것은 체크 표시, 누르면 연결), 구분선, `연결 끊기`(연결이 있을 때만). 바꾸면 `data.json` 저장.
- [ ] **Step 3:** 매일 초기화 호출: ① 생성자에서 `Memos` 를 만든 직후(`PurgeTrash` 옆) ② `CheckRollover` 에서 날짜가 바뀐 경우 ③ `TryRecoverDataFolder` 가 데이터를 갈아끼운 뒤. 각 자리에서 `MemoFlushRequested` 를 먼저 올리고, `skip` 은 "열린 메모 창이 그 id 를 저장 안 된 상태로 들고 있음"(메모 창이 없으면 null). 하나라도 풀었으면 `RefreshMemos()` + `MemosChanged`.
- [ ] **Step 4:** 푸시 → `test` 초록(빌드가 바인딩을 확인).
- [ ] **Step 5:** 커밋 `위젯: 할 일·루틴에 메모 연결(📝) · 하루가 바뀌면 메모 체크 풀기`.

### Task 3: 메모 창 — ↻ · 모두 풀기 · 연결 줄, 문서, v1.7.0

**Files:** `Flow/Views/MemoWindow.axaml(.cs)`, `Flow/ViewModels/MemoEditorViewModel.cs`, `Flow/ViewModels/MemoEditorState.cs`(+시험), `사용법.txt`, `README.md`, 위젯 도움말, `Flow/Flow.csproj`

**Interfaces — Consumes:** `MemoStore.SetDaily/UncheckAll`, `MainViewModel.LinkedItems`, `MemoEditorState`.
**Produces:** `MemoEditorState.SetDaily(bool, DateOnly)`, `MemoEditorState.UncheckAll()` — 둘 다 저장 안 된 글을 먼저 저장하고(실패면 하지 않음), 바뀐 파일을 다시 읽어 본문·판을 맞춘다.

- [ ] **Step 1: 시험** — 쓰는 중 `UncheckAll` → 먼저 저장 후 풂, 본문에 `[x]` 없음, `IsDirty` 거짓; 저장 실패 상태면 풀지 않고 띠 유지; `SetDaily(true)` 뒤 머리말 `daily: true`·`reset: 오늘`, 본문 그대로.
- [ ] **Step 2:** 도구 줄(📌 옆)에 ↻ 토글(켜지면 강조색) · `모두 풀기`(체크된 칸이 있을 때만 활성). 태그 줄 아래 연결 줄 — 연결이 있을 때만, 흐린 작은 글씨 "연결: 루틴 · …, 할 일 · …".
- [ ] **Step 3:** 메모 창 사용법 판의 '고정과 삭제' 앞에 절을 더한다(정확히 이 글):
  ```
  매일 하는 절차라면
  ↻ 를 켜 두면 하루가 바뀔 때 체크가 저절로 풀려요.
  지금 다 풀고 싶으면 '모두 풀기'.
  ```
  그리고 '태그와 찾기' 뒤에:
  ```
  할 일과 연결
  위젯에서 할 일을 우클릭 → 메모 연결.
  연결되면 📝 를 눌러 바로 열 수 있어요.
  ```
- [ ] **Step 4:** `사용법.txt` 10절·README '메모'에 같은 내용 짧게, 위젯 도움말에 📝 한 줄, 1단계 문서의 "아직 없는 것"에서 연결·매일 초기화를 뺀다. 버전 `1.7.0`.
- [ ] **Step 5:** 푸시 → `test` 초록 → 커밋 `메모 창: 매일 초기화 · 모두 풀기 · 연결 줄 · v1.7.0`.
