# 반복(오늘 · 루틴 옮기기 · 반복 범위) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 할 일을 루틴으로(반대로도) 옮기고, 매월 · 격주 · n일마다 · 매년 같은 반복을 화면에서 골라 정하며, 드문 반복은 할 때까지 오늘에 남게 한다.

**Architecture:** 새 반복은 `RepeatRule`(숫자 칸만) 하나로 나타내고 계산은 순수 함수 `RepeatEngine` 이 한다. 규칙은 데이터 폴더 `repeats\<루틴 Id>.json` 에 하나씩 두고(`RepeatStore`), 그 루틴은 data.json 에서 `Archived: true` 라 옛 버전이 건드리지 못한다. 불러온 규칙은 `Routine.Repeat`(`[JsonIgnore]`)에 달고, 일정 판단은 지금처럼 `DayEngine` 한 곳을 지난다. '남는' 항목의 완료는 data.json 의 `LastDoneDate` 로 판단한다(규칙 파일은 반복을 바꿀 때만 쓴다).

**Tech Stack:** C# / .NET 10, Avalonia 12.1.2, CommunityToolkit.Mvvm, System.Text.Json 소스 생성(`AppJsonContext`), 휴대폰은 `docs/` 의 바닐라 JS.

**Spec:** `specs/2026-11-23-repeat-design.md`

## Global Constraints

- **실제 데이터 금지:** `G:\내 드라이브\Flow` 와 `실제사용파일\Flow.exe` 는 읽지도 쓰지도 끄지도 않는다. 실행 · 시험은 `FLOW_DATA_DIR` 을 스크래치패드 아래 새 폴더로(백슬래시 경로) 두고 한다.
- **enum 문자열 금지:** 새 종류 · 모드는 `int` 로 저장한다. 기존 enum 에 값을 더하지 않는다(옛 버전이 파일 전체를 못 읽는다).
- **지금 루틴은 안 바뀐다:** 매일, '지나가는' 매주 요일(간격 1)은 data.json 의 `Days` 만 쓰고 `repeats` 폴더에 들어가지 않는다. 그런 데이터만 있으면 폴더도 생기지 않는다.
- **자동으로 지우지 않는다:** 규칙 파일을 지우는 때는 그 루틴을 지울 때, 할 일로 옮길 때, 옛 방식으로 반복을 바꿀 때뿐이다. `Archived` 인데 규칙이 없거나 못 읽거나 모르는 `Unit` 이면 "반복 정보 없음"으로 두고 손대지 않는다.
- **값:** `Unit` 1 매주 · 2 n일마다 · 3 매월 날짜 · 4 매월 n번째 요일 · 5 매년(0 매일은 화면에서만, 저장 안 함). 말일 `32`, 마지막 주 `5`. 요일은 `DayOfWeek` 숫자(0 일 … 6 토).
- **휴대폰 갈래:** `repeat-done` · `repeat-undone` · `add-repeat`. 화면 파일 루틴 줄에 `Overdue`(int) · `Sticky`(bool).
- **시험:** `dotnet run --project Tests/Flow.Tests` 가 `통과 N · 실패 0` 으로 끝나야 한다(지금 1055). 새 원본 파일은 `Tests/Flow.Tests/Flow.Tests.csproj` 의 `<Compile Include>` 에 더한다.
- **파일 다루기:** 한글 · 백슬래시가 든 스크립트는 Bash 힉어독으로 쓰지 않는다(Write 로 `.py` 를 만들어 실행). 기존 파일의 BOM · 줄바꿈(CRLF)은 그대로 둔다.
- **깃:** 브랜치 `main-lnhh3y`. 커밋 글 끝에 `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`. `docs/` 는 휴대폰 앱이다 — 문서를 넣지 않는다. 합치기 · `release.ps1` 은 쓰는 사람이 말한 뒤에만.
- **버전:** PC v1.13.0, 휴대폰 `APP_VERSION = 'v18'` + `index.html` 의 `?v=18` 두 곳.

## Review Focus

1. **짧은 달에 날짜가 겹침** — 매월 `[30, 31, 말일]` 은 2월에 하루(28/29일)만 온다. 두 번 세거나 건너뛰면 안 된다. → Task 1
2. **PC 를 오래 꺼 둠** — '남는' 매월 항목이 두 차례 지나도록 못 했으면 하나만 남고 밀림은 가장 오래된 차례부터 센다. 체크 한 번으로 다 끝난다. → Task 3
3. **덜 받아진 규칙 파일** — 0바이트 · 잘린 JSON · 모르는 `Unit` 은 죽지 않고 "반복 정보 없음"이며, 그 뒤 저장(`Sync`)이 그 파일을 덮거나 지우지 않는다. → Task 2
4. **제목 속 낱말** — `매월당 방문` · `격주간지 읽기` · `3일마다가스카` 같은 제목을 반복으로 읽지 않는다. → Task 4
5. **첫 차례가 앞날** — 격주 · n개월마다의 첫 차례를 다음 달로 잡으면 그 전에는 오늘에 안 보이고 밀림도 없다. 루틴 탭에는 "다음 …"으로 보인다. → Task 3

---

### Task 1: 반복 규칙과 계산 (`RepeatRule` · `RepeatEngine`)

**Files:**
- Create: `Flow/Models/Repeat.cs`, `Flow/Services/RepeatEngine.cs`, `Tests/Flow.Tests/RepeatTests.cs`
- Modify: `Tests/Flow.Tests/Flow.Tests.csproj`(Compile 3줄), `Tests/Flow.Tests/Program.cs`(`Section("반복 · 계산"); RepeatEngineTests();` — `MemoLinkTests();` 뒤)

**Interfaces:**
- Produces:
  ```csharp
  // Flow.Models
  public sealed class RepeatRule {
      public int Version { get; set; } = 1;
      public int Unit { get; set; }
      public int Every { get; set; } = 1;
      public List<int> Days { get; set; } = new();        // 0 일 … 6 토
      public List<int> MonthDays { get; set; } = new();   // 1–31, 32 말일
      public int Nth { get; set; }                         // 1–4, 5 마지막
      public int NthDay { get; set; }                      // 0–6
      public int Month { get; set; }                       // 1–12
      public int Day { get; set; }                         // 1–31
      public DateOnly Anchor { get; set; }                 // 첫 차례 날짜(그 자체가 차례)
      public bool Sticky { get; set; }
      public RepeatRule Clone();
  }
  public static class RepeatUnit { public const int Daily = 0, Weekly = 1, EveryDays = 2, MonthDates = 3, MonthNth = 4, Yearly = 5, LastDay = 32, LastWeek = 5; }

  // Flow.Services
  public static class RepeatEngine {
      public static bool IsKnown(RepeatRule r);     // Unit 1–5 이고 그 Unit 에 필요한 칸이 범위 안, Every >= 1
      public static bool IsValid(RepeatRule r);     // 화면의 저장 단추: Unit 0 은 늘 참, 그 밖은 IsKnown
      public static bool OccursOn(RepeatRule r, DateOnly date);        // Anchor 앞은 늘 거짓
      public static DateOnly? Next(RepeatRule r, DateOnly from);       // from 포함 뒤의 첫 차례
      public static DateOnly? Previous(RepeatRule r, DateOnly before); // before 앞의 마지막 차례(Anchor 이상), 없으면 null
      public static DateOnly? PendingSince(RepeatRule r, DateOnly? lastDone, DateOnly today); // (lastDone, today] 의 가장 이른 차례
      public static RepeatRule WithFirst(RepeatRule draft, DateOnly today);   // Anchor = today 포함 뒤, 간격을 무시한 가장 가까운 후보 날
      public static RepeatRule ShiftFirst(RepeatRule r, int steps, DateOnly today); // Anchor 를 후보 날 단위로 ±steps, today 앞으로는 안 감
      public static bool IsLegacy(RepeatRule r);                       // Daily, 또는 Weekly && Every == 1 && !Sticky
      public static List<DayOfWeek> LegacyDays(RepeatRule r);          // Daily · 요일 일곱 개 → 빈 목록(= 매일)
      public static RepeatRule FromLegacy(IReadOnlyList<DayOfWeek> days); // 빈 목록 → Daily, 아니면 Weekly Every 1 Sticky false
      public static bool DefaultSticky(int unit, int every);           // Daily, Weekly&&every==1 → false, 그 밖 true
      public static bool SameSchedule(RepeatRule a, RepeatRule b);     // Sticky 만 빼고 같은지(Anchor 포함)
      public static string Describe(RepeatRule r);
  }
  ```
- 계산 규칙(서명과 시험이 못 정하는 것):
  - Weekly: 주는 월요일 시작. `(date 가 든 주 − Anchor 가 든 주) % Every == 0` 이고 요일이 `Days` 에 있고 `date >= Anchor`.
  - EveryDays: `(date − Anchor) % Every == 0`.
  - MonthDates / MonthNth: `(date 의 달 − Anchor 의 달) % Every == 0`. 날짜는 그 달 길이로 당긴다(31 → 말일, `32` → 말일). 당겨서 같은 날이 되면 한 번.
  - MonthNth: `Nth` 5 는 그 달의 마지막 그 요일.
  - Yearly: 매년 `Month`/`Day`. 2월 29일은 평년에 2월 28일.
  - `Next` · `Previous` 는 하루씩 걷되 20년을 넘기면 null.
  - '후보 날'(`WithFirst` · `ShiftFirst`): 간격(`Every`)을 무시하고 요일 · 날짜 조건만 맞는 날. EveryDays 는 하루하루가 후보.
- `Describe` 글(정확히): `매일` · `월 수 금`(Weekly Every 1, 월요일부터) · `격주 월` · `3주마다 월 수` · `매월 25일` · `매월 10·25일` · `매월 말일` · `매월 둘째 화` · `매월 마지막 금` · `3개월마다 10일` · `3개월마다 둘째 화` · `매년 5/31` · `3일마다`. 몇째: 첫째 둘째 셋째 넷째 마지막.

- [ ] **Step 1: 시험을 쓴다** — `RepeatTests.cs` 에 `private static void RepeatEngineTests()`(partial `Program`, `Check(name, actual, expected)` 사용). 헬퍼 `D(y, m, d)`. 반드시 든 것:

```csharp
var m25 = new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 25 }, Anchor = D(2026, 11, 25), Sticky = true };
Check("매월 25일: 그날", RepeatEngine.OccursOn(m25, D(2026, 12, 25)), true);
Check("매월 25일: 다른 날", RepeatEngine.OccursOn(m25, D(2026, 12, 24)), false);
Check("첫 차례 앞은 아님", RepeatEngine.OccursOn(m25, D(2026, 10, 25)), false);
Check("다음", RepeatEngine.Next(m25, D(2026, 11, 26)), D(2026, 12, 25));
Check("직전", RepeatEngine.Previous(m25, D(2026, 12, 25)), D(2026, 11, 25));
Check("직전 없음", RepeatEngine.Previous(m25, D(2026, 11, 25)), null);

// Review Focus 1: 짧은 달에 겹침
var ends = new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 30, 31, 32 }, Anchor = D(2027, 1, 30) };
Check("2월엔 28일 하루만", Enumerable.Range(1, 28).Count(d => RepeatEngine.OccursOn(ends, D(2027, 2, d))), 1);
Check("그 하루는 28일", RepeatEngine.OccursOn(ends, D(2027, 2, 28)), true);
Check("윤년 2월은 29일", RepeatEngine.OccursOn(ends, D(2028, 2, 29)), true);
Check("4월은 30일 하루", Enumerable.Range(1, 30).Count(d => RepeatEngine.OccursOn(ends, D(2027, 4, d))), 1);

var biweek = new RepeatRule { Unit = RepeatUnit.Weekly, Every = 2, Days = { 1 }, Anchor = D(2026, 11, 23), Sticky = true };
Check("격주 월: 첫 주", RepeatEngine.OccursOn(biweek, D(2026, 11, 23)), true);
Check("격주 월: 다음 주는 쉼", RepeatEngine.OccursOn(biweek, D(2026, 11, 30)), false);
Check("격주 월: 그다음 주", RepeatEngine.OccursOn(biweek, D(2026, 12, 7)), true);

var q10 = new RepeatRule { Unit = RepeatUnit.MonthDates, Every = 3, MonthDays = { 10 }, Anchor = D(2027, 1, 10), Sticky = true };
Check("분기: 4월", RepeatEngine.OccursOn(q10, D(2027, 4, 10)), true);
Check("분기: 2월 아님", RepeatEngine.OccursOn(q10, D(2027, 2, 10)), false);

var tue2 = new RepeatRule { Unit = RepeatUnit.MonthNth, Nth = 2, NthDay = 2, Anchor = D(2026, 12, 8) };
Check("둘째 화", RepeatEngine.OccursOn(tue2, D(2027, 1, 12)), true);
var lastFri = new RepeatRule { Unit = RepeatUnit.MonthNth, Nth = RepeatUnit.LastWeek, NthDay = 5, Anchor = D(2026, 11, 27) };
Check("마지막 금", RepeatEngine.OccursOn(lastFri, D(2026, 12, 25)), true);

var yearly = new RepeatRule { Unit = RepeatUnit.Yearly, Month = 2, Day = 29, Anchor = D(2028, 2, 29) };
Check("매년 2/29 → 평년 2/28", RepeatEngine.OccursOn(yearly, D(2029, 2, 28)), true);
var every3 = new RepeatRule { Unit = RepeatUnit.EveryDays, Every = 3, Anchor = D(2026, 11, 23) };
Check("3일마다", (RepeatEngine.OccursOn(every3, D(2026, 11, 26)), RepeatEngine.OccursOn(every3, D(2026, 11, 25))), (true, false));

Check("남은 차례: 처음", RepeatEngine.PendingSince(m25, null, D(2026, 11, 25)), D(2026, 11, 25));
Check("남은 차례: 끝냈음", RepeatEngine.PendingSince(m25, D(2026, 11, 26), D(2026, 12, 24)), null);
Check("남은 차례: 가장 오래된 것", RepeatEngine.PendingSince(m25, D(2026, 11, 26), D(2027, 2, 3)), D(2026, 12, 25));

var draft = new RepeatRule { Unit = RepeatUnit.Weekly, Every = 2, Days = { 1 } };
Check("첫 차례 기본값", RepeatEngine.WithFirst(draft, D(2026, 11, 24)).Anchor, D(2026, 11, 30));
Check("한 차례 뒤로", RepeatEngine.ShiftFirst(RepeatEngine.WithFirst(draft, D(2026, 11, 24)), 1, D(2026, 11, 24)).Anchor, D(2026, 12, 7));
Check("오늘 앞으로는 안 감", RepeatEngine.ShiftFirst(RepeatEngine.WithFirst(draft, D(2026, 11, 24)), -1, D(2026, 11, 24)).Anchor, D(2026, 11, 30));

Check("옛 방식: 매일", RepeatEngine.IsLegacy(new RepeatRule { Unit = RepeatUnit.Daily }), true);
Check("옛 방식: 지나가는 매주", RepeatEngine.IsLegacy(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 1, 3 } }), true);
Check("남기는 매주는 새 방식", RepeatEngine.IsLegacy(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 1 }, Sticky = true }), false);
Check("요일 일곱 개 = 매일", RepeatEngine.LegacyDays(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 0, 1, 2, 3, 4, 5, 6 } }).Count, 0);
Check("모르는 Unit", RepeatEngine.IsKnown(new RepeatRule { Unit = 9 }), false);
Check("요일 없는 매주는 저장 못 함", RepeatEngine.IsValid(new RepeatRule { Unit = RepeatUnit.Weekly }), false);
Check("기본 남기기", (RepeatEngine.DefaultSticky(RepeatUnit.Weekly, 1), RepeatEngine.DefaultSticky(RepeatUnit.Weekly, 2), RepeatEngine.DefaultSticky(RepeatUnit.MonthDates, 1)), (false, true, true));
```
  그리고 위 '`Describe` 글' 열세 가지를 한 줄씩 `Check`.

- [ ] **Step 2: 돌려서 컴파일 오류(형식 없음)로 실패하는 것을 본다** — `dotnet run --project Tests/Flow.Tests`
- [ ] **Step 3: `Flow/Models/Repeat.cs` · `Flow/Services/RepeatEngine.cs` 를 쓴다**(위 서명 · 계산 규칙). UI 패키지를 참조하지 않는다.
- [ ] **Step 4: 시험이 `실패 0` 인 것을 본다**
- [ ] **Step 5: 커밋** — `반복: 규칙과 차례 계산`

---

### Task 2: 규칙 파일 (`RepeatStore`)

**Files:**
- Create: `Flow/Services/RepeatStore.cs`
- Modify: `Flow/Services/DataStore.cs`(`AppJsonContext` 에 `[JsonSerializable(typeof(RepeatRule))]`), `Flow/Models/AppData.cs`(`Routine` 에 `[JsonIgnore] public RepeatRule? Repeat { get; set; }`), `Tests/Flow.Tests/RepeatTests.cs`, `Flow.Tests.csproj`, `Program.cs`(`Section("반복 · 파일"); RepeatStoreTests();`)

**Interfaces:**
- Consumes: Task 1 의 `RepeatRule` · `RepeatEngine.IsKnown`.
- Produces:
  ```csharp
  public sealed class RepeatStore {
      public RepeatStore(string dataDirectory, Func<bool> canWrite);
      public string Folder { get; }            // <데이터 폴더>\repeats
      public bool Scan();                      // 폴더를 다시 읽는다(없으면 빈 것, 만들지 않는다). 지난 Scan 뒤 바뀌었으면 true
      public RepeatRule? Get(Guid id);         // 읽어 둔 규칙의 사본. 없거나 못 읽은 파일이면 null(모르는 Unit 은 그대로 돌려준다)
      public void Attach(AppData data);        // Archived 루틴마다 Repeat = Get(Id), 그 밖의 루틴은 Repeat = null
      public bool Sync(AppData data);          // Archived 이고 Repeat 가 IsKnown 인 루틴 중 파일 내용과 다른 것만 쓴다. 지우지 않는다. 못 쓴 것이 있으면 false
      public bool Delete(Guid id);             // 그 파일을 지운다. 없으면 true
  }
  ```
  - 파일: `repeats\<Id 소문자 D 형식>.json`, BOM 없는 UTF-8, `이름.tmp` 에 쓴 뒤 바꿔치기. `canWrite()` 가 거짓이면 아무것도 쓰지 않는다(`Sync` · `Delete` 는 false).
  - 이름이 Guid 가 아닌 파일 · `.tmp` 는 무시한다. 읽기 실패(잠김 · 잘림 · 0바이트 · JSON 아님)는 '없음'으로 치고 다음 `Scan` 에 다시 본다.
  - `Sync` 는 **읽지 못한 파일이 있는 Id 에는 쓰지 않는다**(덜 받아진 파일을 덮지 않게) — 그 루틴의 `Repeat` 가 null 이라 저절로 그렇다. `Repeat` 가 모르는 `Unit` 이어도 쓰지 않는다.

- [ ] **Step 1: 시험을 쓴다** — 폴더는 `Path.Combine(DataStore.Directory, "repeattest-store")`(있으면 지우고 시작).

```csharp
var store = new RepeatStore(root, () => true);
Check("폴더가 없어도 훑기는 조용함", (store.Scan(), Directory.Exists(store.Folder)), (false, false));
var data = new AppData();
var legacy = new Routine { Title = "매일" };
var monthly = new Routine { Title = "정산", Archived = true, Repeat = m25.Clone() };
data.Routines.AddRange(new[] { legacy, monthly });
Check("쓰기", store.Sync(data), true);
Check("옛 방식 루틴은 파일 없음", File.Exists(Path.Combine(store.Folder, legacy.Id + ".json")), false);
Check("새 반복은 파일 하나", Directory.GetFiles(store.Folder).Length, 1);
Check("같으면 다시 안 씀", (File.GetLastWriteTimeUtc(path) == stamp), true);   // Sync 를 한 번 더 부른 뒤

var other = new RepeatStore(root, () => true);   // 다른 PC
other.Scan();
var copy = new AppData(); copy.Routines.Add(new Routine { Id = monthly.Id, Archived = true });
other.Attach(copy);
Check("다른 PC 가 읽음", RepeatEngine.Describe(copy.Routines[0].Repeat!), "매월 25일");

// Review Focus 3: 덜 받아진 파일
File.WriteAllText(path, "{ \"Unit\": 3, \"Mon");
Check("잘린 파일은 바뀜으로 알림", other.Scan(), true);
other.Attach(copy);
Check("잘린 파일 = 규칙 없음", copy.Routines[0].Repeat, null);
Check("그 뒤 저장이 파일을 덮지 않음", (other.Sync(copy), File.ReadAllText(path)), (true, "{ \"Unit\": 3, \"Mon"));
File.WriteAllText(path, "");
other.Scan(); other.Attach(copy);
Check("0바이트도 규칙 없음", copy.Routines[0].Repeat, null);
File.WriteAllText(path, "{\"Unit\":9,\"Every\":1,\"Anchor\":\"2026-11-25\"}");
other.Scan(); other.Attach(copy);
Check("모르는 Unit 은 읽기만", (copy.Routines[0].Repeat?.Unit, RepeatEngine.IsKnown(copy.Routines[0].Repeat!)), (9, false));
other.Sync(copy);
Check("모르는 Unit 파일을 덮지 않음", File.ReadAllText(path).Contains("\"Unit\":9"), true);

// 루틴 없는 파일은 안 지운다
var empty = new AppData();
store.Scan(); store.Attach(empty); store.Sync(empty);
Check("루틴 없는 파일은 그대로", File.Exists(path), true);
Check("Archived 아닌 루틴엔 안 단다", AttachTo(new Routine { Id = monthly.Id }).Repeat, null);
Check("지우기", (store.Delete(monthly.Id), File.Exists(path)), (true, false));
Check("못 쓰는 동안엔 안 씀", new RepeatStore(root, () => false).Sync(data), false);
```

- [ ] **Step 2: 실패를 본다**(형식 없음)
- [ ] **Step 3: `RepeatStore` 를 쓴다** — 바뀜 감지는 파일마다 (크기, 고친 시각)을 기억해 견준다(`MemoStore.ScanFolder` 와 같은 방식). 스스로 쓴 파일은 그 값을 기억해 다음 `Scan` 이 바뀜으로 읽지 않게 한다.
- [ ] **Step 4: `실패 0`**. 그리고 `직렬화` 시험: `Routine.Repeat` 가 data.json 에 안 들어가는지 `Check("Repeat 는 data.json 에 없음", DataStore.Serialize(data).Contains("Anchor"), false)`.
- [ ] **Step 5: 커밋** — `반복: 규칙 파일 읽기 · 쓰기`

---

### Task 3: 일정 판단을 잇는다 (`DayEngine` · `ReminderEngine`)

**Files:**
- Modify: `Flow/Services/DayEngine.cs`(`IsScheduled` 81행, `PreviousScheduledDay`, `CompleteRoutine`, `Rollover` 의 루틴 고리, `SyncToday`), `Flow/Services/ReminderEngine.cs`(32–34행의 `routine.Archived` 검사), `Tests/Flow.Tests/RepeatTests.cs`, `Program.cs`(`Section("반복 · 하루 넘기기"); RepeatDayTests();`)

**Interfaces:**
- Consumes: `Routine.Repeat`, `RepeatEngine`.
- Produces(모두 `DayEngine` 의 static):
  ```csharp
  public static bool HasRule(Routine r);          // Archived && Repeat 있음 && IsKnown
  public static bool IsRepeatMissing(Routine r);  // Archived && !HasRule
  public static bool IsSticky(Routine r);         // HasRule && Repeat.Sticky
  public static bool IsScheduled(Routine r, DateOnly date); // HasRule → OccursOn / Archived → false / 그 밖은 지금 그대로
  public static DateOnly? DueSince(Routine r, DateOnly today); // 남는 항목의 못 끝낸 가장 이른 차례(LastDoneDate 뒤), 아니면 null
  public static bool IsDue(Routine r, DateOnly today);  // 남는 항목: DueSince 있음 || (DoneToday && LastDoneDate == today). 그 밖: IsScheduled
  public static int OverdueDays(Routine r, DateOnly today); // today − DueSince, 없으면 0
  public static DateOnly? NextDay(Routine r, DateOnly from); // from 포함 뒤의 첫 차례(옛 방식은 7일 안에서 찾는다). 규칙 없음이면 null
  ```
- 바뀌는 동작:
  - `PreviousScheduledDay`: 규칙이 있으면 `RepeatEngine.Previous`(14일 제한 없음). 옛 방식은 그대로.
  - `CompleteRoutine`: 남는 항목이면 스냅숏을 남기고 `LastDoneDate = today; DoneToday = true` 만(연속 기록은 건드리지 않는다). `UncompleteRoutine` 은 그대로 쓴다.
  - `Rollover` 의 날마다 고리: 남는 항목은 `LastDoneDate == day` 일 때만 `scheduled++; done++`, 연속 기록을 0 으로 만들지 않는다. 그 밖은 지금 그대로(`IsScheduled` 가 규칙을 읽는다).
  - `SyncToday`: 남는 항목은 오늘 끝냈을 때만 센다(`scheduled`·`done` 둘 다 +1).
  - `ReminderEngine`: `routine.Archived` 로 거르지 말고 `DayEngine.IsScheduled(routine, date)` 만 믿는다(차례 날에만 울린다 — 밀려 남은 날은 안 울린다).

- [ ] **Step 1: 시험을 쓴다**

```csharp
// 옛 방식은 그대로
var daily = new Routine { Title = "물" };
var mwf = new Routine { Title = "운동", Days = { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday } };
Check("매일은 그대로", DayEngine.IsDue(daily, D(2026, 11, 24)), true);
Check("요일도 그대로", (DayEngine.IsDue(mwf, D(2026, 11, 23)), DayEngine.IsDue(mwf, D(2026, 11, 24))), (true, false));
Check("다음 날(옛 방식)", DayEngine.NextDay(mwf, D(2026, 11, 24)), D(2026, 11, 25));

// 남는 매월
var pay = new Routine { Title = "정산", Archived = true, Repeat = m25.Clone() };   // 매월 25일, Sticky
Check("차례 전엔 안 보임", (DayEngine.IsDue(pay, D(2026, 11, 24)), DayEngine.OverdueDays(pay, D(2026, 11, 24))), (false, 0));
Check("차례 날", (DayEngine.IsDue(pay, D(2026, 11, 25)), DayEngine.OverdueDays(pay, D(2026, 11, 25))), (true, 0));
Check("못 하면 남고 밀림 3일", (DayEngine.IsDue(pay, D(2026, 11, 28)), DayEngine.OverdueDays(pay, D(2026, 11, 28))), (true, 3));
// Review Focus 2: 두 차례가 지나도 하나, 가장 오래된 것부터
Check("두 차례 지나도 가장 오래된 차례부터", DayEngine.DueSince(pay, D(2027, 1, 3)), D(2026, 11, 25));
DayEngine.CompleteRoutine(pay, D(2027, 1, 3));
Check("체크한 날은 체크된 채 보임", (DayEngine.IsDue(pay, D(2027, 1, 3)), pay.DoneToday, pay.Streak), (true, true, 0));
DayEngine.UncompleteRoutine(pay);
Check("같은 날 풀면 다시 밀림", DayEngine.DueSince(pay, D(2027, 1, 3)), D(2026, 11, 25));
DayEngine.CompleteRoutine(pay, D(2027, 1, 3));
pay.DoneToday = false;   // 다음 날 정산이 푼다
Check("다음 날엔 사라짐", DayEngine.IsDue(pay, D(2027, 1, 4)), false);
Check("다음 차례에 다시", DayEngine.IsDue(pay, D(2027, 1, 25)), true);

// Review Focus 5: 첫 차례가 앞날
var later = new Routine { Archived = true, Repeat = new RepeatRule { Unit = RepeatUnit.Weekly, Every = 2, Days = { 1 }, Anchor = D(2026, 12, 7), Sticky = true } };
Check("첫 차례 전엔 안 보이고 밀림도 없음", (DayEngine.IsDue(later, D(2026, 11, 30)), DayEngine.OverdueDays(later, D(2026, 12, 6))), (false, 0));
Check("루틴 탭의 '다음'", DayEngine.NextDay(later, D(2026, 11, 24)), D(2026, 12, 7));

// 규칙 없음
var lost = new Routine { Archived = true };
Check("규칙 없음: 오늘엔 안 보임", (DayEngine.IsRepeatMissing(lost), DayEngine.IsDue(lost, D(2026, 11, 24)), DayEngine.NextDay(lost, D(2026, 11, 24))), (true, false, (DateOnly?)null));
var unknown = new Routine { Archived = true, Repeat = new RepeatRule { Unit = 9 } };
Check("모르는 Unit 도 규칙 없음", DayEngine.IsRepeatMissing(unknown), true);

// 지나가는 매월(Sticky 끔): 연속 기록이 한 달 간격으로 이어진다
var habit = new Routine { Archived = true, Repeat = new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 25 }, Anchor = D(2026, 11, 25) } };
DayEngine.CompleteRoutine(habit, D(2026, 11, 25)); habit.DoneToday = false;
DayEngine.CompleteRoutine(habit, D(2026, 12, 25));
Check("한 달 간격 연속 2", habit.Streak, 2);

// 정산: 남는 항목은 연속 기록을 끊지 않고, 끝낸 날에만 센다
var app = new AppData { LastLogicalDate = D(2026, 11, 25) };
var sticky = new Routine { Title = "정산", Archived = true, Repeat = m25.Clone(), Streak = 0 };
var water = new Routine { Title = "물", Streak = 5, LastDoneDate = D(2026, 11, 25) };
app.Routines.AddRange(new[] { sticky, water });
DayEngine.Rollover(app, D(2026, 11, 27));
Check("못 한 남는 항목은 그날 기록에 안 들어감", app.History.First(h => h.Date == D(2026, 11, 25)).RoutinesScheduled, 1);
Check("며칠 건너뛰어도 여전히 남음", DayEngine.OverdueDays(sticky, D(2026, 11, 27)), 2);
Check("매일 루틴은 지금처럼 끊김", water.Streak, 0);
```
  알림: `ReminderEngine` 의 지금 시험 옆에 — 시각 · 알림을 켠 남는 매월 항목이 25일에는 울릴 대상이고 26일(밀린 날)에는 아니다.

- [ ] **Step 2: 실패를 본다**
- [ ] **Step 3: `DayEngine` · `ReminderEngine` 을 고친다**
- [ ] **Step 4: `실패 0`** — 기존 "루틴 자동 초기화 · 스트릭" · "알림" 시험이 그대로 통과해야 한다.
- [ ] **Step 5: 커밋** — `반복: 일정 판단 · 남는 항목 · 하루 넘기기`

---

### Task 4: 문장 읽기와 줄 만들기 · 옮기기 (`QuickAddParser` · `ItemFactory`)

**Files:**
- Modify: `Flow/Services/QuickAddParser.cs`, `Flow/Services/ItemFactory.cs`, `Tests/Flow.Tests/RepeatTests.cs`, `Program.cs`(`Section("반복 · 문장과 옮기기"); RepeatParseTests();`)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record QuickAddResult(bool IsRoutine, string Title, List<DayOfWeek> Days, Priority Priority,
      DateOnly? Due, TimeOnly? DueTime, RepeatRule? Repeat = null);   // Repeat 가 있으면 IsRoutine 참, Days 는 빔
  // ItemFactory
  public static Routine CreateRoutine(QuickAddResult parsed, string title, int order, Guid? id = null); // Repeat 가 있으면 Archived = true, Repeat = 그 규칙
  public static void SetRepeat(Routine routine, RepeatRule choice);  // IsLegacy → Days = LegacyDays, Archived = false, Repeat = null / 아니면 Archived = true, Repeat = choice. 일정이 달라졌으면(SameSchedule 아님) Streak · StreakSnapshot = 0
  public static Routine ToRoutine(TaskItem task, RepeatRule choice, int order); // 같은 Id · 제목 · MemoId, Time = DueTime, Remind = task.Remind && DueTime 있음. 그다음 SetRepeat
  public static TaskItem ToTask(Routine routine, DateOnly today, int order);    // 같은 Id · 제목 · MemoId, CreatedDate = today. Time 이 있으면 Due = today, DueTime = Time, Remind = routine.Remind. 없으면 Due null
  ```
- 문장 규칙(새 패턴은 기존 `WeeklyPattern` 보다 먼저 먹는다. 모두 **앞뒤 낱말 경계**를 요구한다 — 앞은 줄 처음이나 공백, 뒤는 공백):
  - `매월` | `매달` + (`말일` | `N일`(여러 개, 공백 · `,` · `·` 로 나눔) | `첫째|둘째|셋째|넷째|마지막 X요일`). 날짜 말이 없으면 오늘의 날짜(일).
  - `N개월마다` + 위와 같은 날짜 말 → `Every = N`. `분기마다` → `Every = 3`, 첫 차례는 1·4·7·10월 중 오늘 포함 뒤의 가장 가까운 달. `반기마다` → `Every = 6`, 1·7월.
  - `격주` | `N주마다` + 요일(`월요일` · `화목` · `월수금`) → Weekly, `Every` 2 또는 N.
  - `N일마다` → EveryDays, 첫 차례는 오늘.
  - `매년 M월 D일` → Yearly.
  - 읽은 규칙은 `Sticky = true`, `Anchor` 는 `RepeatEngine.WithFirst`(분기 · 반기는 위 달 맞춤).
  - 시각(`오후 3시`)은 지금처럼 `DueTime` 으로 따로 읽는다.

- [ ] **Step 1: 시험을 쓴다** — 오늘은 `D(2026, 11, 23)`(월요일).

```csharp
QuickAddResult P(string s) => QuickAddParser.Parse(s, D(2026, 11, 23));
string R(string s) => P(s).Repeat is { } r ? $"{RepeatEngine.Describe(r)} @{r.Anchor:yyyy-MM-dd} | {P(s).Title}" : "없음 | " + P(s).Title;
Check("매월 25일", R("매월 25일 정산"), "매월 25일 @2026-11-25 | 정산");
Check("매달 말일", R("매달 말일 결산"), "매월 말일 @2026-11-30 | 결산");
Check("날짜 둘", R("매월 10일 25일 송금"), "매월 10·25일 @2026-11-25 | 송금");
Check("격주 월요일", R("격주 월요일 회의"), "격주 월 @2026-11-23 | 회의");
Check("2주마다 화목", R("2주마다 화목 운동"), "격주 화 목 @2026-11-24 | 운동");
Check("3일마다", R("3일마다 화분 물"), "3일마다 @2026-11-23 | 화분 물");
Check("매년", R("매년 5월 31일 종합소득세"), "매년 5/31 @2027-05-31 | 종합소득세");
Check("분기마다 = 달력 분기", R("분기마다 10일 부가세"), "3개월마다 10일 @2027-01-10 | 부가세");
Check("반기마다", R("반기마다 말일 점검"), "6개월마다 말일 @2027-01-31 | 점검");
Check("둘째 화요일", R("매월 둘째 화요일 정기회의"), "매월 둘째 화 @2026-12-08 | 정기회의");
Check("마지막 금요일", R("매월 마지막 금요일 회식"), "매월 마지막 금 @2026-11-27 | 회식");
Check("3개월마다", R("3개월마다 15일 점검"), "3개월마다 15일 @2026-12-15 | 점검");
Check("날짜 없는 매월은 오늘 날짜", R("매월 정산"), "매월 23일 @2026-11-23 | 정산");
Check("읽은 반복은 남기기", (P("매월 25일 정산").IsRoutine, P("매월 25일 정산").Repeat!.Sticky, P("매월 25일 정산").Days.Count), (true, true, 0));
Check("시각도 같이", P("매월 25일 오후 3시 정산").DueTime, new TimeOnly(15, 0));
// Review Focus 4: 제목 속 낱말
Check("매월당은 제목", R("매월당 방문"), "없음 | 매월당 방문");
Check("격주간지는 제목", R("격주간지 읽기"), "없음 | 격주간지 읽기");
Check("붙여 쓴 '3일마다가스카'는 제목", R("3일마다가스카 여행"), "없음 | 3일마다가스카 여행");
// 지금 것은 그대로
Check("매주는 옛 방식 그대로", (P("매주 월수금 운동").Repeat, P("매주 월수금 운동").Days.Count), ((RepeatRule?)null, 3));
Check("그냥 25일은 지금처럼 마감일", (P("25일 정산").IsRoutine, P("25일 정산").Due), (false, D(2026, 11, 25)));

// 옮기기
var task = new TaskItem { Title = "정산", Priority = Priority.High, Due = D(2026, 11, 20), DueTime = new TimeOnly(15, 0), Remind = true, MemoId = Guid.NewGuid(), CarryOverCount = 3 };
var moved = ItemFactory.ToRoutine(task, P("매월 25일 x").Repeat!, 7);
Check("할 일 → 루틴", (moved.Id == task.Id, moved.Title, moved.MemoId == task.MemoId, moved.Time, moved.Remind, moved.Archived, moved.Order), (true, "정산", true, (TimeOnly?)new TimeOnly(15, 0), true, true, 7));
var simple = ItemFactory.ToRoutine(new TaskItem { Title = "물" }, RepeatEngine.FromLegacy(new[] { DayOfWeek.Monday }), 0);
Check("옛 방식으로 옮기면 파일 대상 아님", (simple.Archived, simple.Repeat, simple.Days.Count), (false, (RepeatRule?)null, 1));
var back = ItemFactory.ToTask(moved, D(2026, 11, 23), 2);
Check("루틴 → 할 일(시각 있음)", (back.Id == moved.Id, back.Due, back.DueTime, back.Remind, back.MemoId == moved.MemoId), (true, (DateOnly?)D(2026, 11, 23), (TimeOnly?)new TimeOnly(15, 0), true, true));
Check("루틴 → 할 일(시각 없음)", ItemFactory.ToTask(new Routine { Title = "물" }, D(2026, 11, 23), 0).Due, null);
var r5 = new Routine { Streak = 5, StreakSnapshot = 5, Days = { DayOfWeek.Monday } };
ItemFactory.SetRepeat(r5, new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 1 }, Sticky = true, Anchor = D(2026, 11, 23) });
Check("남기기만 켜면 기록 그대로", (r5.Streak, r5.Archived), (5, true));
ItemFactory.SetRepeat(r5, P("매월 25일 x").Repeat!);
Check("일정을 바꾸면 기록 0", r5.Streak, 0);
```
  마지막 두 시험을 위해 `SetRepeat` 의 "일정이 달라졌나"는 **바꾸기 전 루틴의 일정**(옛 방식이면 `FromLegacy(Days)`, 규칙이면 그 규칙)과 `choice` 를 `Anchor` · `Sticky` 를 빼고 견준다.

- [ ] **Step 2: 실패를 본다**
- [ ] **Step 3: 파서 · `ItemFactory` 를 고친다** — `QuickAddResult` 를 만드는 기존 호출은 마지막 인자를 더하지 않아도 컴파일된다.
- [ ] **Step 4: `실패 0`** — 기존 "빠른 추가 파서" 시험이 그대로 통과해야 한다.
- [ ] **Step 5: 커밋** — `반복: 문장 읽기 · 할 일과 루틴 옮기기`

---

### Task 5: 휴대폰과 주고받기 — PC 쪽 (`PhoneLinkEngine`)

**Files:**
- Modify: `Flow/Models/PhoneLink.cs`(`PhoneOp` 상수 셋, `PhoneViewItem` 칸 둘), `Flow/Services/PhoneLinkEngine.cs`(310–320행 갈래 switch, `ApplyCheck` 497행, `BuildView` 의 루틴 고리 631행), `Tests/Flow.Tests/RepeatTests.cs`, `Program.cs`(`Section("반복 · 폰 연동"); RepeatPhoneTests();`)

**Interfaces:**
- Produces: `PhoneOp.RepeatDone = "repeat-done"`, `PhoneOp.RepeatUndone = "repeat-undone"`, `PhoneOp.AddRepeat = "add-repeat"`; `PhoneViewItem.Overdue`(int) · `PhoneViewItem.Sticky`(bool).
- 동작:
  - `repeat-done` / `repeat-undone` 은 `ApplyCheck` 로, `add-repeat` 은 `ApplyAdd` 로 간다(같은 처리). `done` / `undone` 이 남는 항목을 가리켜도 받는다.
  - `ApplyCheck` 의 루틴 갈래: `IsScheduled` 대신 `DayEngine.IsDue(routine, op.Day)`.
  - `BuildView`: `IsDue` 인 루틴을 넣는다. `Overdue = OverdueDays`, `Sticky = IsSticky`, 남는 항목은 `Streak = 0`.

- [ ] **Step 1: 시험을 쓴다** — `PhoneLinkTests.cs` 의 헬퍼(`Op(...)`, `Apply(...)`)를 그대로 쓴다.
  - 28일(밀림 3일)의 화면: 남는 매월 항목이 `Routines` 에 있고 `Overdue == 3`, `Sticky == true`, `Streak == 0`. 차례가 아닌 날의 지나가는 루틴은 없다.
  - `repeat-done`(Day = 오늘) → `LastDoneDate == 오늘`, `DoneToday`, 반영 기록에 들어감. 이어서 `repeat-undone` → 다시 밀림.
  - `add-repeat` 글 `매월 25일 정산` → 루틴 하나, `Archived`, `Repeat` 가 `매월 25일`, Id 는 op.Id. 같은 op 를 두 번 넣어도 하나.
  - 옛 PC 흉내: `PhoneOp` 상수를 모르는 갈래 글(`"repeat-done-x"`)은 지금처럼 넣지도 적지도 않는다(기존 시험이 이미 본다면 그대로 둔다).
- [ ] **Step 2: 실패를 본다** · **Step 3: 고친다** · **Step 4: `실패 0`**
- [ ] **Step 5: 커밋** — `반복: 폰 화면의 밀림 · 새 갈래 받기`

---

### Task 6: 백업 · 되돌리기 · 폴더 옮기기

**Files:**
- Modify: `Flow/Services/BackupService.cs`(zip 만들기 62–75행 옆, 되돌리기 229행 옆), `Flow/Services/DataLocation.cs`(`Prepare` 86–99행), `Tests/Flow.Tests/RepeatTests.cs`, `Program.cs`(`Section("반복 · 백업"); RepeatBackupTests();`)

**Interfaces:**
- zip 엔트리 `repeats/<파일 이름>`(`.json` 만, `.tmp` 제외).
- 되돌리기: 백업에 `repeats/` 엔트리가 하나라도 있으면 폴더를 그 내용과 같게 맞춘다(백업에 없는 지금 파일은 지운다 — 되돌리기 직전 백업에 들어 있다). 하나도 없으면 폴더를 건드리지 않는다. 규칙 파일을 못 되돌려도 결과는 지금의 `RestoreOutcome` 그대로(`Ok`/`MemosPartial`) — 새 enum 값을 만들지 않는다.
- `DataLocation.Prepare`: `repeats` 폴더를 메모와 같은 규칙으로 따라가게 한다(그쪽에 있는 파일은 덮지 않는다).

- [ ] **Step 1: 시험을 쓴다** — `BackupTests` 의 방식 그대로(`DataStore.Directory` 아래).
  - 규칙 파일 하나를 두고 백업 → zip 에 `repeats/<id>.json` 이 있다.
  - 규칙을 바꾸고(매월 25일 → 10일) 새 파일을 하나 더 만든 뒤 그 백업으로 되돌리기 → 25일로 돌아오고, 새 파일은 없다.
  - `repeats/` 가 없는 zip(옛 백업 흉내: 엔트리를 빼고 만든 zip)으로 되돌리기 → 지금 규칙 파일이 그대로다.
  - `DataLocation.Prepare(새 폴더)` 뒤 새 폴더에 `repeats\<id>.json` 이 있다.
- [ ] **Step 2: 실패를 본다** · **Step 3: 고친다** · **Step 4: `실패 0`**
- [ ] **Step 5: 커밋** — `반복: 백업 · 되돌리기 · 폴더 옮기기에 규칙 파일`

---

### Task 7: 위젯에 잇기 (`MainViewModel` · 줄)

**Files:**
- Modify: `Flow/ViewModels/MainViewModel.cs`, `Flow/ViewModels/Rows.cs`
- 시험: 이 계층은 시험 프로젝트에 없다 → 빌드 + 아래 '그려서 확인'.

**Interfaces:**
- Consumes: Task 2–6.
- Produces(`MainViewModel`):
  ```csharp
  public RepeatStore Repeats { get; }
  public void MoveToRoutine(TaskRow row);      // 반복 창을 연다(기본: 매월, 오늘 날짜) → 저장하면 ItemFactory.ToRoutine
  public void ChangeRepeat(RoutineRow row);    // 반복 창(지금 반복으로 시작) → 저장하면 ItemFactory.SetRepeat (+ 옛 방식이 되면 Repeats.Delete)
  public void MoveToTask(RoutineRow row);      // ItemFactory.ToTask + Repeats.Delete
  public void OpenRepeatEditor(RepeatRule start, string title, Action<RepeatRule> onSave);  // Task 8 의 판을 띄운다(이 Task 에서는 속이 빈 채 두고 Task 8 이 채운다)
  ```
  `RoutineRow`: `RepeatText`(옛 방식은 지금 `DaysText` 글, 규칙은 `Describe`, 규칙 없음은 `반복 정보 없음`), `NextText`(오늘 차례가 아니면 `다음 11/25`, 아니면 빈 글), `IsSticky`, `IsRepeatMissing`, `OverdueText`(`밀림 3일`/빈 글), `ShowStreak` 는 남는 항목이면 거짓, `ChangeRepeatCommand`, `MoveToTaskCommand`. `TaskRow`: `MoveToRoutineCommand`.
- 잇는 자리(지금 줄 번호):
  - 생성자: `_store.Load()` 뒤, 첫 `Rollover` · 폰 일 넣기 **앞**에 `Repeats.Scan(); Repeats.Attach(_data);`. `canWrite` 는 메모와 같이 `() => !_store.WaitingForFolder`.
  - `Persist()`(1345행): `_store.RequestSave` **앞**에 `Repeats.Sync(_data)`.
  - 데이터를 갈아끼우는 세 곳 — `SyncFromDisk`(914), `TryRecoverDataFolder`(865 근처), `RestoreFrom`(1219 근처): `Rollover` 앞에 `Repeats.Scan(); Repeats.Attach(_data);`.
  - 분 틱(287행 근처): `if (Repeats.Scan()) { Repeats.Attach(_data); RebuildAll(); }`.
  - `DeleteRoutine`(1629): `Repeats.Delete(row.Model.Id)`.
  - `CanToggleRoutine`(1572) · `RebuildRoutines`(2111)의 오늘 목록: `DayEngine.IsDue`.
  - `QuickAdd`: 읽은 `parsed.Repeat` 가 있으면 알림 글은 `루틴 추가 · {Describe}`.
  - `UpdateQuickAddHint`(2050): 반복을 읽었으면 `루틴 · {Describe} · 다음 {M/d}`.

- [ ] **Step 1: 고친다**
- [ ] **Step 2: 빌드** — `dotnet build Flow/Flow.csproj -c Debug -nologo -v q` → `경고 0개 · 오류 0개`
- [ ] **Step 3: 시험** — `실패 0`(이 Task 가 순수 계층을 깨지 않았는지)
- [ ] **Step 4: 그려서 확인** — 스크래치패드의 그리기 도구(없으면 만든다: `Avalonia.Headless` · `Avalonia.Skia` · `Avalonia.Themes.Fluent` 12.1.2, `Flow.csproj` 참조, `win-x64` · `SelfContained true` 인 콘솔. `AppBuilder.Configure<앱>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting()` 뒤 `new MainWindow { DataContext = new MainViewModel() }` 를 띄워 `CaptureRenderedFrame()` 을 PNG 로). `FLOW_DATA_DIR` 은 스크래치패드의 새 폴더.
  - 그 폴더에 `매월 25일 정산`(첫 차례를 지난 날짜로 고친 규칙 파일)과 매일 루틴을 심고 오늘 탭 · 루틴 탭을 찍는다: 오늘 탭 루틴 구역에 `정산` 이 `밀림 n일` 과 함께, 루틴 탭에 `매월 25일` 이 보인다.
  - 도구에서 `MoveToTask` · `ItemFactory.ToRoutine` 경로를 한 번씩 불러 `repeats` 폴더의 파일이 생기고 없어지는지 본다.
- [ ] **Step 5: 커밋** — `반복: 위젯에 잇기`

---

### Task 8: 화면 — 반복 창 · 메뉴 · 칩 · 줄 표시

**Files:**
- Create: `Flow/ViewModels/RepeatEditorViewModel.cs`
- Modify: `Flow/Views/MainWindow.axaml`(오른쪽 클릭 메뉴 다섯 곳: 24 · 177 · 242 · 396행과 완료된 할 일 줄, 되돌리기 판(894행) 옆에 새 판, 빠른 추가(Row 5) 줄의 칩, 루틴 줄 템플릿 두 곳), `Flow/Views/MainWindow.axaml.cs`(Esc 로 판 닫기 — 설정 · 되돌리기 판이 닫히는 자리와 같이), `Flow/ViewModels/MainViewModel.cs`(`IsRepeatOpen`, `RepeatEditor`, `PendingRepeat`, `RepeatChipText`, `PickRepeatCommand`), `Flow/Styles/Controls.axaml`(필요한 모양만)

**Interfaces:**
- `RepeatEditorViewModel(RepeatRule start, DateOnly today, string title, Action<RepeatRule> save, Action close)`:
  - 탭 `Tab`(0 매일 · 1 매주 · 2 매월 · 3 매년 · 4 n일마다), `Every`(1–99), 요일 일곱 개 토글, `MonthDays`(목록 + `AddMonthDay(int)` · `RemoveMonthDay(int)` · `ToggleLastDay()`), `ByNth`(매월의 날짜/요일 고르기), `Nth` · `NthDay`, `Month` · `Day`, `Sticky`, `FirstText`, `ShiftFirstCommand`(−1/+1), `CanSave`, `SaveCommand`, `CancelCommand`.
  - `Build()` 는 `RepeatRule` 을 만들어 `RepeatEngine.WithFirst`(+ 옮긴 만큼 `ShiftFirst`)를 거친다. 탭이나 간격을 바꾸면 `Sticky` 는 `RepeatEngine.DefaultSticky` 로 따라가되, 사람이 스위치를 한 번 누른 뒤에는 따라가지 않는다.
  - 저장 단추는 `RepeatEngine.IsValid(Build())` 일 때만 켜진다.
- 화면 글(정확히): 판 제목 `반복`, 탭 `매일` `매주` `매월` `매년` `n일마다`, 빠른 고르기 `평일` `주말`, `말일`, `+ 날짜`, 스위치 `못 하면 할 때까지 남기기`, `첫 차례:`, 단추 `취소` `저장`. 메뉴: 할 일 줄에 `루틴으로 옮기기`, 루틴 줄에 `반복 바꾸기` · `할 일로 옮기기`(모두 `메모 연결` 위). 칩 `반복: {글} ▾`(루틴 탭에서만 보임, 기본 `매일`).
- 칩: 누르면 반복 창(시작은 지금 칩 값). 저장하면 `PendingRepeat` 에 담고, 다음 `QuickAdd`(루틴 탭, 문장에 반복 말이 없을 때)가 그 반복으로 만든 뒤 `PendingRepeat = null`.
- 줄 표시: 루틴 탭 줄은 `RepeatText` 와 `NextText`, 오늘 탭 줄은 `OverdueText`(할 일의 `밀림 n일` 과 같은 모양 · 색). 규칙 없음 줄은 흐리게(오늘 차례가 아닌 루틴과 같은 모양).
- 판은 위젯 안에 뜬다(설정 · 되돌리기 판과 같은 `Border` · `DialogBrush`). 기본 너비 360 에서 탭 다섯 칸이 한 줄에 들어가야 한다.

- [ ] **Step 1: `RepeatEditorViewModel` 과 화면을 만든다**
- [ ] **Step 2: 빌드** `경고 0개 · 오류 0개`, 시험 `실패 0`
- [ ] **Step 3: 그려서 확인**(Task 7 의 도구, 너비 360 과 300): 매주 탭 · 매월 탭(날짜 둘 + 말일) · 매월 요일 고르기 · n일마다 탭의 반복 창, 루틴 탭의 칩과 줄 글, 오늘 탭의 밀림 표시. 글이 잘리거나 두 줄로 넘어가면 고친다. 찍은 그림은 쓰는 사람에게 보낸다.
- [ ] **Step 4: 흐름 확인**(도구에서 명령을 불러): 할 일 → `MoveToRoutineCommand` → 매월 25일로 저장 → 할 일이 사라지고 루틴이 생기며 `repeats\<id>.json` 이 있다. 그 루틴 → `ChangeRepeatCommand` → 매일로 저장 → 파일이 없어지고 `Archived` 가 풀린다. 취소는 아무것도 바꾸지 않는다.
- [ ] **Step 5: 커밋** — `반복: 반복 창 · 메뉴 · 칩`

---

### Task 9: 휴대폰 v18 (`docs/`)

**Files:**
- Modify: `docs/app.js`(`composeView` 의 일 얹기, `rowHtml` 1261행, 체크 누르기 2159행, 추가 2179행, `APP_VERSION`), `docs/style.css`(필요하면), `docs/index.html`(`?v=18` 두 곳)

**Interfaces:**
- Consumes: 화면 파일 루틴 줄의 `Overdue` · `Sticky`(Task 5).
- 동작:
  - 루틴 줄: `Overdue > 0` 이면 `<span class="carry">밀림 n일</span>`(할 일과 같은 모양). `Sticky` 면 🔥 를 안 보인다.
  - 체크: 그 줄이 `Sticky` 면 `repeat-done` / `repeat-undone`, 아니면 지금처럼 `done` / `undone`. `composeView` 는 두 새 갈래도 `Done` 을 바꿔 얹는다.
  - 추가: 글이 `/(^|\s)(매월|매달|격주|\d+주마다|\d+일마다|\d+개월마다|분기마다|반기마다|매년)(?=\s)/` 에 맞으면 `add-repeat`, 아니면 `add`. `composeView` 는 `add-repeat` 도 `add` 처럼 대기 줄로 보인다.
  - PC 가 하루 늦은 화면(`stale`)일 때 남는 줄은 체크만 풀어 보이고 그대로 둔다(지금 루틴과 같다).

- [ ] **Step 1: 고친다** · `node --check docs/app.js`
- [ ] **Step 2: 브라우저로 확인** — `.claude/launch.json` 의 `phone`(5280)으로 `?demo` 를 휴대폰 크기로 띄운다. 보기용 데이터(`loadDemo`)에 `Sticky: true, Overdue: 3` 인 루틴 하나를 넣어 밀림 표시를 보고, 눌러서 `state.pending` 의 마지막 일이 `repeat-done` 인지, `매월 25일 정산` 을 적어 `add-repeat` 인지, `내일 회의` 는 `add` 인지 본다. 콘솔 오류 0.
- [ ] **Step 3: 커밋** — `폰 v18: 반복 루틴의 밀림 표시 · 새 갈래`

---

### Task 10: 옛 버전 왕복 확인 · 사용법 글 · 마무리

**Files:**
- Modify: `사용법.txt`, `README.md`, `Flow/Views/MainWindow.axaml`(앱 안 사용법의 '1 · 적는 법'과 '2 · 다루기'에 반복 문장 · 옮기기 한두 줄), `specs/2026-11-23-repeat-design.md`(상태 줄을 `구현됨`으로)
- 스크래치패드에서만: v1.12.0 작업 폴더(`git worktree add <스크래치>\flow-v1.12.0 v1.12.0`)와 그것을 참조하는 작은 콘솔.

- [ ] **Step 1: 옛 버전 왕복** — 시험용 `FLOW_DATA_DIR`(새 폴더)에서:
  1. 새 버전으로 매일 루틴 하나, `매월 25일 정산`(남는 항목, 한 번 체크해 `LastDoneDate` 가 있음), `격주 월` 을 만들고 저장한다. data.json 의 그 둘이 `"Archived": true` 인지, `repeats` 폴더에 파일 둘이 있는지 적어 둔다(파일 글을 복사해 둔다).
  2. data.json 의 `LastLogicalDate` 를 이틀 전으로 고친 뒤, **v1.12.0** 의 `MainViewModel` 을 그 폴더로 띄운다(정산이 돈다). 매일 루틴을 체크하고 `FlushNow()`.
  3. 확인: `repeats` 의 두 파일이 글자 하나 안 바뀌었다. data.json 의 두 루틴이 그대로 있고 `Archived: true`, `LastDoneDate` 가 1 과 같다.
  4. 새 버전으로 다시 연다: 두 반복이 `매월 25일` · `격주 월` 로 보이고 완료 기록이 그대로다.
  - 하나라도 다르면 여기서 멈추고 원인을 고친 뒤 처음부터 다시 한다.
- [ ] **Step 2: 지금 데이터 모양 확인** — 매일 · 요일 루틴만 있는 시험용 data.json 을 새 버전으로 열고 체크 · 저장 · 하루 넘기기를 해도 `repeats` 폴더가 생기지 않고, 연속 기록 값이 v1.12.0 으로 같은 일을 했을 때와 같다.
- [ ] **Step 3: 글** — `사용법.txt` · `README.md` · 앱 안 사용법에: 반복 종류와 예문(Task 4 의 문장), 못 했을 때의 두 방식, 오른쪽 클릭의 옮기기 · 반복 바꾸기, 루틴 탭의 반복 칩, 휴대폰에서는 보기 · 체크 · 문장으로 적기만 된다는 것. `사용법.txt` 는 BOM 이 있다 — 그대로 지킨다.
- [ ] **Step 4: 전체 확인** — 시험 `실패 0`, 빌드 `경고 0개 · 오류 0개`, `git worktree remove` 로 v1.12.0 작업 폴더를 걷는다.
- [ ] **Step 5: 커밋** — `반복: 사용법 · 옛 버전 왕복 확인`. 올리기(`git push origin main-lnhh3y`)까지 하고, PR · 합치기 · `release.ps1 1.13.0` 은 쓰는 사람에게 묻는다.
