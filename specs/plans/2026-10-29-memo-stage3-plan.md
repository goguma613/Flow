# 메모 3단계 (휴대폰에서 메모 보기 · 체크) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 휴대폰 Flow 에 `메모` 탭을 더해 PC 의 메모를 찾아 읽고, 메모 안 체크 칸을 누르면 PC 가 받아 넣는다. 오늘 목록의 루틴·할 일에 연결된 메모는 📝 로 바로 연다.

**Architecture:** PC 가 이미 쓰는 `phone-view.json` 에 `Memos` 와 각 줄의 `MemoId` 를 더한다(중계 스크립트는 그대로 넘기므로 바꿀 것 없음). 휴대폰은 체크를 `memo-check` 일로 inbox 에 쌓고, PC 의 `PhoneLinkEngine` 이 `MemoStore` 로 그 줄만 고친다 — 지금의 체크·추가와 같은 길. 메모 본문 읽기는 PC 가 판(Rev)이 바뀐 메모만 다시 읽어 캐시한다.

**Tech Stack:** PC .NET 10 / Avalonia 12, 휴대폰 `docs/app.js`(그냥 JS, GitHub Pages). 시험: `Tests/Flow.Tests`(CI), 휴대폰은 `?demo` + 가짜 중계(Playwright).

**Spec:** `specs/2026-10-29-memo-design.md` — 1장 '휴대폰', 3장 '휴대폰이 받는 것'·'휴대폰이 보내는 일'(`memo-check` 줄), 7장 3단계.

## Global Constraints

- `PhoneView.Memos`: 휴지통·읽지 못함 제외. 항목 `Id, Title(기호 뗀 제목), Text(머리말 뺀 본문 전체), Pinned, Daily, Reset(yyyy-MM-dd 또는 null), Tags(머리말+본문), Rev, Updated`. 순서: 고정 먼저, 최근에 고친 순.
- `PhoneViewItem.MemoId`: 연결된 메모가 `Memos` 에 있을 때만 채운다(없으면 null).
- 새 필드는 모두 옛 휴대폰·옛 PC 가 모르는 채 건너뛸 수 있어야 한다(필드 추가만, 기존 필드 의미 그대로).
- `memo-check` 일: `Target` = 메모 id, `Text` = 그 줄의 글(체크 기호·앞 들여쓰기 뺀 것), `Line` = 몇째 줄(0부터, 본문 기준), `Checked` = true/false. PC 반영: `Line` 줄이 체크 줄이고 글이 같으면 그 줄을, 아니면 같은 글의 체크 줄 중 첫째를 `Checked` 로 맞춘다. 맞는 줄이 없으면 건너뛰고 '처리함'으로 적는다. 이미 그 상태면 건너뜀.
- PC 가 지금 그 메모를 쓰기 모드로 들고 있거나 저장 안 된 글이 있으면(`IsMemoDirty`/쓰기 중), 또는 저장 폴더를 기다리는 중이면 그 `memo-check` 는 **처리하지 않고 inbox 에 남긴다**(다음 확인 때 다시).
- 휴대폰 화면: 위에 `오늘 | 메모` 탭. 메모 탭은 검색칸(제목·본문, `#태그`), 태그 칩, 목록(📌 먼저, 제목 + 흐린 첫머리, 매일 초기화면 ↻). 메모를 누르면 전체 화면 읽기(PC 와 같은 줄 서식: `#`·`##`·번호·점·체크, 들여쓰기), 맨 위 `‹ 메모` 로 돌아감. 체크 칸을 누르면 바로 바뀌어 보이고 'PC 반영 대기' 표시.
- PC 가 꺼져 하루가 지났으면(`view.Today` < 휴대폰의 논리적 오늘) `daily` 이고 `Reset` 이 오늘보다 이전인 메모는 체크를 풀어 보여 준다(루틴과 같은 미리보기).
- 오늘 목록의 루틴·할 일 줄에 `MemoId` 가 있으면 📝 → 누르면 그 메모를 연다(줄 체크는 안 됨).
- 휴대폰에서는 아직 메모를 쓰거나 고치지 못한다(4단계). 메모 읽기 화면 아래에 한 줄: "고치기는 다음 업데이트에서 됩니다".
- 버전: PC `1.8.0`, 휴대폰 파일 `v8`(index.html 의 `?v=` 와 diag).

## Review Focus

1. **PC 가 쓰는 중인 메모에 휴대폰 체크가 옴** — 덮어쓰거나 충돌 사본이 생기면 안 된다 → 남겨 두었다가 다음에(Task 1 시험).
2. **줄이 밀림** — 휴대폰이 본 뒤 PC 에서 위에 줄을 더했으면 `Line` 이 틀린다 → 같은 글을 찾아 맞춘다, 같은 글이 둘이면 첫째(Task 1 시험).
3. **view 가 커짐** — 메모 본문이 다 들어가므로 30초마다 받는 양이 는다. PC 는 내용이 같으면 view 를 다시 쓰지 않고(이미 그렇다), 본문은 판이 바뀐 것만 읽는다(Task 1).
4. **휴대폰에 남은 대기 체크와 PC 결과가 다름** — PC 가 건너뛴 체크(줄 없음)는 applied 로 와서 대기 표시가 사라지고 PC 의 실제 상태가 보여야 한다(Task 2).
5. **한글 검색** — 대소문자 없는 한글과 영문이 섞인 검색이 PC 와 같은 결과(낱말 모두 포함, `#태그`)여야 한다(Task 2).

---

### Task 1: PC — view 에 메모, `memo-check` 반영

**Files:** `Flow/Models/PhoneLink.cs`, `Flow/Services/PhoneLinkEngine.cs`, `Flow/Services/MemoStore.cs`, `Flow/ViewModels/MainViewModel.cs`(BuildView/Apply 호출), `Tests/Flow.Tests/PhoneLinkTests.cs`(또는 MemoTests)

**Interfaces — Produces:**
- `PhoneMemo` 클래스(위 필드), `PhoneView.Memos: List<PhoneMemo>`, `PhoneViewItem.MemoId: Guid?`, `PhoneOp.Line: int?`, `PhoneOp.Checked: bool?`, `PhoneOp.MemoCheck = "memo-check"`.
- `PhoneLinkEngine.BuildView(AppData data, DateOnly today, DateTime now, IReadOnlyList<PhoneMemo>? memos = null)` — 기존 호출은 그대로 컴파일.
- `PhoneLinkEngine.Apply(AppData data, IEnumerable<PhoneOp>? ops, IPhoneMemoTarget? memos = null)` — `memo-check` 는 `memos` 가 있을 때만 처리(없으면 지금처럼 모르는 갈래로 남김). `IPhoneMemoTarget`: `MemoCheckResult SetLineChecked(Guid id, string text, int? line, bool isChecked)`(`Applied`/`Skipped`/`Deferred`). `PhoneApplyResult` 에 `MemoChecked` 수.
- `MemoStore.SetLineChecked(...)` 가 위 규칙(줄 찾기, 쓸 수 없으면 Deferred)을 구현. MainViewModel 이 쓰기 중·저장 안 된 메모는 Deferred 로 돌려주는 얇은 어댑터를 넘긴다.
- MainViewModel: 메모 본문 캐시(id → (Rev, Body)), view 를 만들 때 판이 바뀐 메모만 `Load`.

- [ ] **Step 1: 시험** — view 에 Memos(휴지통 제외, 순서, Title 기호 뗌, Tags 합침, MemoId 는 살아 있는 메모만); `memo-check`: 줄 번호 맞음 → 체크, 줄 밀림 → 같은 글 찾음, 같은 글 둘 → 첫째, 줄 없음 → Skipped 로 처리함 기록, 이미 그 상태 → 건너뜀, Deferred → 처리함에 안 적혀 inbox 에 남음, `memos` 없이 Apply → 모르는 갈래처럼 남김; JSON 직렬화에 새 필드.
- [ ] **Step 2–4:** 구현 → 푸시 → `test` 초록.
- [ ] **Step 5:** 커밋 `폰 연동: 메모를 휴대폰 화면에 · 휴대폰 메모 체크 받아 넣기`.

### Task 2: 휴대폰 — 메모 탭 · 읽기 · 체크 · 📝

**Files:** `docs/app.js`, `docs/style.css`, `docs/index.html`

- [ ] **Step 1:** `오늘 | 메모` 탭(상태는 localStorage 에 기억). 메모 탭: 검색·태그 칩·목록(PC `MemoQuery` 와 같은 규칙 — 낱말 모두, `#태그` 는 태그 조건, 대소문자 무시, 고정 먼저·최근 순).
- [ ] **Step 2:** 읽기 화면: PC `MemoFormat.Classify` 와 같은 줄 분류를 JS 로 옮긴다(`# `, `## `, 들여쓴 `1. `, `- `/`* `, `[ ]`/`[]`/`[x]`/`- [ ]`). 제목(기호 뗌), 태그 칩, ↻ 표시. 체크 칸 누름 → `memo-check` 일(Text·Line·Checked) 쌓기, 바로 바뀌어 보임, 대기 표시. `composeView` 처럼 대기 중인 memo-check 를 얹어 보여 주고, applied 로 오면 PC 상태를 따른다. 하루 지남 미리보기(Global Constraints).
- [ ] **Step 3:** 오늘 목록 줄에 📝(MemoId 있을 때) → 그 메모 읽기 화면. 줄 체크와 겹치지 않게(길게 누르기 고치기 창과도).
- [ ] **Step 4:** `?demo` 에 메모 몇 개(체크·번호·태그·매일)와 연결된 할 일. 가짜 중계(실제 relay.gs)로 Playwright 확인: 탭 전환, 검색, 메모 열기, 체크 → inbox 에 `memo-check`, 📝 로 열기, 뒤로. 파일 `v8`.
- [ ] **Step 5:** 커밋 `폰: 메모 탭 · 읽기 · 체크 · 📝`.

### Task 3: 문서 · 버전

- [ ] `사용법.txt`·README 의 휴대폰·메모 절, 메모 "아직 없는 것" 줄을 "휴대폰에서 메모 고치기는 다음 업데이트"로. PC `1.8.0`. 커밋 `메모 3단계 사용법 · v1.8.0`.
