# 메모 4단계 (휴대폰에서 메모 쓰기 · 고치기) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 휴대폰에서 메모를 새로 쓰고, 고치고, 고정·매일 초기화·모두 풀기·삭제를 하고, 할 일·루틴에 메모를 연결한다. PC 는 그 일을 받아 넣고, 겹치면 덮어쓰지 않고 `(휴대폰 충돌 사본)` 을 남긴다.

**Architecture:** 3단계와 같은 길 — 휴대폰은 일(op)을 inbox 에 쌓고, PC 의 `PhoneLinkEngine.Apply` 가 `IPhoneMemoTarget` 을 통해 `MemoStore` 로 반영한다. 고치기는 '보고 고친 판(Base)'을 함께 보내 PC 가 판을 비교한다. PC 에서 그 메모를 쓰는 중이면 3단계 체크처럼 기다린다(Held).

**Tech Stack:** PC .NET 10 / Avalonia 12, 휴대폰 `docs/app.js`. 시험: `Tests/Flow.Tests`(CI), 휴대폰 Playwright + 가짜 중계(실제 relay.gs).

**Spec:** `specs/2026-10-29-memo-design.md` — 3장 '휴대폰이 보내는 일' 표(memo-save·pin·daily·uncheck-all·delete·link), 1장 '휴대폰', 7장 4단계.

## Global Constraints

- 일의 모양(모두 `Id, Kind, Target, Text, At, Day` + 필요한 것):
  - `memo-save`: Target = 메모 id(새 메모면 휴대폰이 만든 새 Guid), Text = 본문 전체(`\n`), `Base` = 보고 고친 판(Rev), 새 메모면 null.
  - `memo-pin` / `memo-daily`: Text = `"true"`/`"false"`.
  - `memo-uncheck-all`, `memo-delete`: Target 만.
  - `memo-link`: Target = 루틴·할 일 id, Text = 메모 id 또는 빈 글(끊기).
- PC 반영 규칙(`memo-save`):
  - 메모가 없고 Base 가 null → **그 id 로** 새로 만든다(같은 일이 두 번 와도 하나).
  - 메모가 없고 Base 가 있음(그 사이 지워짐) → 새 메모로 남긴다(새 id), 처리함.
  - 있고 지금 판 == Base → 덮어쓴다. 지금 본문과 같으면 건너뜀.
  - 있고 판 ≠ Base → 원본 그대로, 휴대폰 글을 제목 끝에 ` (휴대폰 충돌 사본)` 을 붙인 새 메모로.
- 그 메모를 PC 가 쓰기 모드로 들고 있거나 저장 안 된 글이 있으면 `memo-save`·`pin`·`daily`·`uncheck-all`·`delete` 는 **Held**(inbox 에 남김, 3단계 띠·`HeldMemos` 그대로 쓴다). 저장 폴더를 기다리거나 파일이 잠겼으면 Deferred. 같은 메모의 뒤 일들도 함께 기다린다(순서 지킴).
- `memo-daily true` 는 PC 의 `SetDaily(id, true, op.Day)` — 켠 날은 안 풀림. `memo-uncheck-all` 은 `reset` 그대로.
- `memo-delete` 는 휴지통으로(바로 지우지 않음). 이미 없으면 건너뜀.
- `memo-link`: 메모 id 가 지금 있는 메모가 아니면 건너뜀(빈 글은 끊기). `data.json` 저장.
- PC 알림: 기존 '폰에서 가져옴 · …' 에 "메모 고침 n · 새 메모 n · 충돌 사본 n" 등.
- 휴대폰 화면:
  - 메모 목록 오른쪽 위 `+` → 새 메모(제목 칸 없이 본문 한 칸, 첫 줄이 제목).
  - 메모 읽기 화면 오른쪽 위 `고치기` → 전체 화면 글 칸(내용 그대로) + 아래 서식 줄 `# · ## · 1. · - · [ ]`(커서 줄 앞에 붙이기/떼기, PC `MemoEditing` 과 같은 규칙) + `저장`·`취소`. 체크·번호·점 줄에서 줄바꿈하면 다음 줄에도 이어짐(PC 와 같음).
  - 읽기 화면 `…` → `고정`/`고정 풀기`, `매일 초기화 켜기`/`끄기`, `체크 모두 풀기`, `삭제`(두 번 눌러야). 
  - 오늘 탭 할 일·루틴 길게 누르기 창에 `메모 연결` → 메모 고르기 목록(고정 먼저·최근 순) / `새 메모로` / `연결 끊기`.
  - 고치는 동안 30초 새로고침이 글 칸을 건드리지 않는다. 쓰던 글은 폰에 임시로 남겨(localStorage) 앱을 닫았다 열어도 이어서.
  - 보낸 일은 PC 가 받기 전까지 그대로 얹어 보인다('PC 반영 대기'). PC 가 받은 뒤엔 PC 결과(충돌 사본 포함)를 따른다. 그 메모를 PC 에서 쓰는 중이면 "PC에서 쓰는 중 · 쓰기를 마치면 들어갑니다".
  - 메모 화면의 "고치기는 다음 업데이트에서 됩니다" 줄은 지운다.
- 버전: PC `1.9.0`, 휴대폰 `v10`. 문서(사용법.txt·README·위젯 도움말·메모 창 사용법)의 "아직 없는 것"·휴대폰 줄을 고친다.

## Review Focus

1. **휴대폰 고치기와 PC 고치기가 겹침** — 어느 쪽 글도 사라지면 안 된다 → 판 비교 + 충돌 사본(Task 1 시험).
2. **같은 고치기가 두 번 옴**(중계·여러 탭) — 새 메모가 둘 생기거나 두 번 덮으면 안 된다 → 새 메모는 그 id 로, 덮기는 '같은 글이면 건너뜀'(Task 1 시험).
3. **고치는 중 새로고침·앱 전환** — 휴대폰 글 칸이 지워지면 안 된다 → 렌더 제외 + 임시 저장(Task 2 Playwright).
4. **연속 고치기** — 휴대폰에서 저장을 두 번 하면 두 번째 Base 는 첫 번째 결과의 판이어야 한다(PC 가 아직 안 받았으면 첫 번째 글의 판을 휴대폰이 계산해 쓴다 → PC `RevOf` 와 같은 SHA-256 앞 16자를 JS 로) → 거짓 충돌 사본이 생기면 안 된다(Task 1·2 시험).
5. **큰 글** — 본문 전체가 inbox 에 들어간다. 중계 POST 와 드라이브 파일 크기에 무리가 없어야 한다(몇십 KB) → 너무 크면(예: 200 KB 초과) 휴대폰이 저장 전에 알린다.

---

### Task 1: PC — 휴대폰 메모 고치기 받아 넣기

**Files:** `Flow/Models/PhoneLink.cs`, `Flow/Services/PhoneLinkEngine.cs`, `Flow/Services/MemoStore.cs`, `Flow/ViewModels/MainViewModel.cs`, `Tests/Flow.Tests/PhoneLinkTests.cs`

**Produces:** `PhoneOp.Base: string?`; op 상수 `MemoSave, MemoPin, MemoDaily, MemoUncheckAll, MemoDelete, MemoLink`; `IPhoneMemoTarget` 에 `Save(Guid id, string text, string? baseRev, DateOnly day)`, `SetPinned`, `SetDaily(Guid, bool, DateOnly)`, `UncheckAll`, `Delete` (결과: `Applied`/`Skipped`/`Deferred`/`Held` + 새로 생긴 메모 id·충돌 여부); `MemoStore.CreateWithId(Guid id, string body)`; `PhoneApplyResult` 에 `MemoSaved, MemoCreated, MemoConflicts, MemoHeader, MemoDeleted, MemoLinked`.

- [ ] **Step 1: 시험** — 새 메모(Base null, 그 id) · 같은 일 두 번 → 하나 · 판 같음 → 덮음 · 같은 글 → 건너뜀 · 판 다름 → 원본 그대로 + `(휴대폰 충돌 사본)` · 지워진 메모에 고치기 → 새 메모 · 쓰는 중 → Held, 풀린 뒤 반영 · 잠김 → Deferred · 같은 메모 순서(save 뒤 pin) · pin/daily(켠 날 안 풀림)/uncheck-all/delete(휴지통, 없으면 건너뜀) · link(있는 메모, 빈 글 끊기, 없는 메모 건너뜀, data.json) · 옛 PC 처럼 target 없으면 모두 남김 · JS 와 같은 `RevOf`(고정 입력 몇 개의 기대 판을 시험에 적어 Task 2 와 공유).
- [ ] **Step 2–4:** 구현 → 푸시 → `test` 초록.
- [ ] **Step 5:** 커밋 `폰 연동: 휴대폰 메모 쓰기·고치기·고정·매일·모두 풀기·삭제·연결 받아 넣기`.

### Task 2: 휴대폰 — 쓰기 · 고치기 · 메뉴 · 연결, 문서 · 버전

**Files:** `docs/app.js`, `docs/style.css`, `docs/index.html`, `사용법.txt`, `README.md`, `Flow/Views/MainWindow.axaml`(위젯 도움말), `Flow/Views/MemoWindow.axaml`(메모 창 사용법 판의 휴대폰 줄이 있으면), `Flow/Flow.csproj`

- [ ] **Step 1:** 새 메모 `+`, `고치기` 글 칸 + 서식 줄 + 목록 이어 쓰기, `저장`/`취소`, 임시 저장, 새로고침 제외. 판 계산(`RevOf` 를 JS 로, Web Crypto SHA-256) — Task 1 시험의 기대 판과 같아야 한다.
- [ ] **Step 2:** `…` 메뉴(고정·매일·모두 풀기·삭제 두 번), 오늘 탭 길게 누르기 창의 `메모 연결`.
- [ ] **Step 3:** 보낸 일 얹어 보이기(새 메모는 목록에, 고친 글은 그 메모에, 고정·매일은 표시에, 삭제는 목록에서 빼기, 연결은 📝), PC 결과 따르기, Held 문구. 200 KB 초과 경고.
- [ ] **Step 4:** Playwright + 가짜 중계: 새 메모 → inbox `memo-save`(Base null), 고치기 → Base = 받은 판, 연속 저장 → 두 번째 Base = 첫 글의 판, 새로고침 중 글 칸 유지, 메뉴 일들, 연결, Held 문구. `v10`.
- [ ] **Step 5:** 문서 · PC `1.9.0`. 커밋 `폰: 메모 쓰기 · 고치기 · 메뉴 · 연결` / `메모 4단계 사용법 · v1.9.0`.
