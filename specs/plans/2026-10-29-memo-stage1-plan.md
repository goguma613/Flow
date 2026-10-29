# 메모 1단계 (PC에서 메모 쓰고 찾기) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PC Flow 에 메모를 더한다 — 메모 하나 = `.md` 파일 하나, 위젯의 `메모` 탭, 따로 뜨는 메모 창(읽기/쓰기), 가벼운 서식, 검색·고정·태그, 휴지통, zip 백업, PC 끼리 덮어쓰기 방지.

**Architecture:** 순수 함수 `MemoFormat`(글 ↔ 메모)과 파일만 아는 `MemoStore`(폴더·저장·충돌·휴지통)가 바닥이고, 목록 거르기는 순수 함수 `MemoQuery`. 화면은 그 위에 얇게 얹는다: 위젯 `MainViewModel` 이 `MemoStore` 와 목록을 들고, 메모 창(`MemoWindow` + `MemoEditorViewModel`)은 열 때 만들고 닫으면 버린다. 백업은 `.json` 하나에서 `data.json`+`memos\` 를 담은 `.zip` 으로 바뀐다.

**Tech Stack:** .NET 10, Avalonia 12, CommunityToolkit.Mvvm 8.4, System.IO.Compression (기본 포함). 시험은 `Tests/Flow.Tests` 의 자체 `Check(...)` 러너.

**Spec:** `specs/2026-10-29-memo-design.md` (1·2·3장의 PC 부분, 5·6장, 7장 1단계)

## Global Constraints

- 메모 폴더: `<데이터 폴더>\memos\`, 휴지통: `<데이터 폴더>\memos\휴지통\`. 메모를 하나도 만들지 않으면 폴더를 만들지 않는다.
- 파일: UTF-8(BOM 없음), 쓸 때 줄바꿈 `\r\n`, 읽을 때 `\r\n`·`\n`·`\r` 모두 받는다. 확장자 `.md`(읽기는 `.md`·`.txt`).
- 머리말: 첫 줄이 정확히 `---`, 다음 `---` 줄까지. 키: `id`, `pinned`, `daily`, `reset`, `deleted`(날짜는 `yyyy-MM-dd`). 모르는 키는 순서대로 보존. `false`·빈 값은 쓰지 않는다.
- 제목 = 본문의 첫 **비어 있지 않은** 줄(앞뒤 공백 제외). 없으면 `제목 없음`.
- 태그 = 앞이 줄 시작이거나 공백인 `#` + 공백·`#` 아닌 글자 1개 이상. `# 준비` 는 태그가 아니다.
- 판(Rev) = 본문(머리말 제외, 줄바꿈을 `\n` 으로 맞춘 것)의 SHA-256 소문자 16진 앞 16자.
- 파일 이름: `\ / : * ? " < > |` 와 제어 문자 제거 → 앞뒤 공백·점 제거 → 60자에서 자름 → 비었거나 예약 이름(`CON PRN AUX NUL COM1-9 LPT1-9`, 대소문자 무시)이면 `메모`. 겹치면 `이름 (2)`, `(3)` … (대소문자 무시로 겹침 판단).
- 충돌 사본 제목: 첫 줄 끝에 ` (충돌 사본 MM-dd HH:mm)`(PC 저장 충돌), ` (충돌 사본)`(같은 id 가 둘).
- 자동 저장: 입력이 1초 멈추면 · 다른 메모로 바꿀 때 · 창을 닫을 때. 저장 실패면 5초마다 다시.
- 폴더 훑기: 10초마다, (크기, 고친 시각)이 바뀐 파일만 읽는다.
- 휴지통 보관 30일(`deleted` 기준), PC 가 켜질 때 정리.
- 목록 첫머리(Preview): 제목 줄 다음 본문을 공백 하나로 접어 앞 80자.
- `DataStore.WaitingForFolder` 동안은 메모 파일을 쓰지 않는다.
- 문구(화면 글)는 스펙 그대로: 탭 `메모`, `휴지통`, `제목 없음`, 충돌 띠 "다른 곳에서 먼저 고쳐서, 내 글은 충돌 사본으로 따로 남겼습니다", 실패 띠 "저장하지 못했습니다 · 다시 시도 중".
- 이 저장소에는 로컬 dotnet 이 없을 수 있다. 시험은 Task 0 의 GitHub Actions(`test.yml`)로 돌린다.

## Review Focus

1. **한글 입력 중 저장/다시 읽기** — IME 조합 중이거나 쓰기 모드인 동안 폴더 훑기가 본문을 갈아끼우면 커서가 튀고 글자가 사라진다. 쓰기 모드(또는 저장 안 한 글)일 때는 절대 본문을 바꾸지 않고 저장 시 판 검사로 넘긴다 → Task 6 의 `MemoEditorState` 시험.
2. **줄바꿈만 다른 같은 글** — 메모장·드라이브가 `\n`↔`\r\n` 을 바꿔도 판이 같아야 한다(아니면 거짓 충돌 사본) → Task 1 시험.
3. **대소문자만 다른 파일 이름** — Windows 는 `Memo.md` 와 `memo.md` 를 같은 파일로 본다. 겹침 판단이 대소문자를 무시해야 남의 파일을 덮지 않는다 → Task 2 시험.
4. **잠긴/덜 받아진 파일** — 드라이브가 받는 중인 파일을 읽다 예외가 나도 앱이 죽지 않고, 목록에 '읽지 못함'으로 남았다가 다음 훑기에 다시 읽힌다 → Task 2 시험.
5. **열려 있는 메모가 사라짐** — 다른 PC 에서 지우거나 휴지통으로 가면, 메모 창은 빈 화면으로 죽지 않고 "이 메모는 다른 곳에서 지워졌습니다"를 보이며, 저장 안 한 글이 있으면 새 메모(충돌 사본)로 남긴다 → Task 2(`Save` 가 `Missing`) + Task 6 시험.

---

### Task 0: 시험을 PR·푸시마다 GitHub 에서 돌리기

**Files:**
- Create: `.github/workflows/test.yml`

**Interfaces:**
- Produces: `main-lnhh3y` 푸시와 `main` 대상 PR 마다 Windows 에서 `dotnet build Flow/Flow.csproj` + `dotnet run --project Tests/Flow.Tests -c Release` (env `FLOW_DATA_DIR=${{ runner.temp }}\flow-test`). 이후 모든 Task 의 "시험 실행"은 이 워크플로 결과를 본다.

- [ ] **Step 1:** `release.yml` 의 checkout·setup-dotnet(10.0.x)·시험 단계를 그대로 써서 `test.yml` 작성. 트리거 `push: branches: [main-lnhh3y]`, `pull_request: branches: [main]`. 앱 빌드 단계(`dotnet build Flow/Flow.csproj -c Release --nologo -v q`)를 시험 앞에 둔다 — UI 코드 컴파일 오류도 잡기 위해.
- [ ] **Step 2:** 커밋·푸시 후 워크플로가 초록인지 확인(지금 시험 357개 통과).

```bash
git add .github/workflows/test.yml && git commit -m "CI: 푸시·PR 마다 빌드와 시험" && git push origin main-lnhh3y
```

---

### Task 1: `MemoFormat` — 글 ↔ 메모 (순수 함수)

**Files:**
- Create: `Flow/Models/Memo.cs`, `Flow/Services/MemoFormat.cs`
- Create: `Tests/Flow.Tests/MemoTests.cs`
- Modify: `Tests/Flow.Tests/Flow.Tests.csproj` (Compile 3개 추가), `Tests/Flow.Tests/Program.cs` (`Section("메모 · 형식"); MemoFormatTests();`)

**Interfaces:**
- Produces (`Flow/Models/Memo.cs`):
  ```csharp
  public sealed record MemoDocument(Guid Id, bool HasHeader, bool Pinned, bool Daily,
      DateOnly? Reset, DateOnly? Deleted, IReadOnlyList<KeyValuePair<string, string>> Extra, string Body);
  public enum MemoLineKind { Blank, Text, Heading1, Heading2, Numbered, Bullet, Check }
  public sealed record MemoLine(int Index, MemoLineKind Kind, string Text, string? Marker, bool Checked);
  ```
  (`Marker`: 번호 단계면 `"3."`, 그 밖은 null. `Text`: 기호를 뗀 글.)
- Produces (`Flow/Services/MemoFormat.cs`, `public static class MemoFormat`):
  - `MemoDocument Parse(string fileText, Guid fallbackId)` — 머리말 없음/깨짐(닫는 `---` 없음, `id` 가 Guid 아님)이면 `HasHeader=false`, 전체를 본문, `Id=fallbackId`.
  - `string Serialize(MemoDocument doc)` — 언제나 머리말을 쓴다. 키 순서 `id, pinned, daily, reset, deleted, (Extra 순서대로)`.
  - `string TitleOf(string body)`, `IReadOnlyList<string> TagsOf(string body)` (나온 순서, 중복 제거, `#` 뺀 이름), `string RevOf(string body)`, `string PreviewOf(string body)`
  - `IReadOnlyList<MemoLine> Lines(string body)` / `MemoLine Classify(int index, string line)`
  - `string SetChecked(string body, int lineIndex, bool isChecked)` — 그 줄이 Check 가 아니면 본문 그대로.
  - `string UncheckAll(string body)`
  - `string FileStem(string title)` — 확장자 없는 이름.
  - `Guid IdFromPath(string relativePath)` — 경로(소문자)의 MD5 로 만든 결정적 Guid. 머리말 없는 파일의 임시 id.
  - `string AppendToTitle(string body, string suffix)` — 첫 비어 있지 않은 줄 끝에 붙임(본문이 비면 `제목 없음` + suffix).

- [ ] **Step 1: 실패하는 시험 작성** (`MemoFormatTests()` 안, `Check` 사용)
  ```csharp
  var id = Guid.Parse("3f2a9c1e-0000-4000-8000-000000000001");
  var text = "---\r\nid: 3f2a9c1e-0000-4000-8000-000000000001\r\npinned: true\r\ndaily: true\r\nreset: 2026-10-29\r\ncolor: blue\r\n---\r\nAK넷 일별 정책서 배포 #AK넷\r\n\r\n# 준비\r\n1. 정책서 엑셀 받기\r\n[x] 단가표 확인\r\n- [ ] 오전 11~12시 사이 배포\r\n";
  var doc = MemoFormat.Parse(text, Guid.Empty);
  Check("머리말 id", doc.Id, id);
  Check("고정", doc.Pinned, true);
  Check("매일", doc.Daily, true);
  Check("reset", doc.Reset, (DateOnly?)new DateOnly(2026, 10, 29));
  Check("모르는 키 보존", MemoFormat.Serialize(doc).Contains("color: blue"), true);
  Check("왕복", MemoFormat.Serialize(MemoFormat.Parse(MemoFormat.Serialize(doc), Guid.Empty)), MemoFormat.Serialize(doc));
  Check("제목", MemoFormat.TitleOf(doc.Body), "AK넷 일별 정책서 배포 #AK넷");
  Check("빈 첫 줄 건너뜀", MemoFormat.TitleOf("\n\n  배포 절차\n본문"), "배포 절차");
  Check("빈 본문", MemoFormat.TitleOf("  \n"), "제목 없음");
  Check("태그", string.Join(",", MemoFormat.TagsOf("배포 #AK넷 #정산\n# 준비\n문장#붙음 #AK넷")), "AK넷,정산");
  Check("머리말 없는 파일", MemoFormat.Parse("그냥 메모\n본문", id).HasHeader, false);
  Check("닫는 --- 없음 = 본문", MemoFormat.Parse("---\nid: x\n본문", id).Body, "---\nid: x\n본문");
  Check("판: 줄바꿈 무관", MemoFormat.RevOf("a\r\nb"), MemoFormat.RevOf("a\nb"));
  Check("판: 글이 다르면 다름", MemoFormat.RevOf("a\nb") == MemoFormat.RevOf("a\nc"), false);
  Check("판 길이", MemoFormat.RevOf("a").Length, 16);
  var lines = MemoFormat.Lines(doc.Body);
  Check("소제목", lines[2].Kind, MemoLineKind.Heading1);
  Check("작은 소제목", MemoFormat.Classify(0, "## 세부").Kind, MemoLineKind.Heading2);
  Check("번호", (lines[3].Kind, lines[3].Marker, lines[3].Text), (MemoLineKind.Numbered, "1.", "정책서 엑셀 받기"));
  Check("체크됨", (lines[4].Kind, lines[4].Checked), (MemoLineKind.Check, true));
  Check("- [ ] 도 체크", (lines[5].Kind, lines[5].Checked, lines[5].Text), (MemoLineKind.Check, false, "오전 11~12시 사이 배포"));
  Check("점 목록", MemoFormat.Classify(0, "* 항목").Kind, MemoLineKind.Bullet);
  Check("태그로 시작하는 줄은 글", MemoFormat.Classify(0, "#AK넷 메모").Kind, MemoLineKind.Text);
  Check("체크 켜기", MemoFormat.SetChecked("[ ] a\r\n[x] b", 0, true), "[x] a\r\n[x] b");
  Check("체크 아닌 줄은 그대로", MemoFormat.SetChecked("a\n[ ] b", 0, true), "a\n[ ] b");
  Check("모두 풀기", MemoFormat.UncheckAll("[x] a\n- [X] b\n글 [x]"), "[ ] a\n- [ ] b\n글 [x]");
  Check("이름: 못 쓰는 글자", MemoFormat.FileStem("배포/정산: 절차?"), "배포정산 절차");
  Check("이름: 60자", MemoFormat.FileStem(new string('가', 80)).Length, 60);
  Check("이름: 예약어", MemoFormat.FileStem("con"), "메모");
  Check("이름: 다 지워짐", MemoFormat.FileStem("???"), "메모");
  Check("이름: 앞뒤 점·공백", MemoFormat.FileStem(" .절차. "), "절차");
  Check("첫머리", MemoFormat.PreviewOf("제목\n\n  첫째   줄\n둘째"), "첫째 줄 둘째");
  Check("제목 뒤에 붙이기", MemoFormat.AppendToTitle("\n제목\n본문", " (충돌 사본)"), "\n제목 (충돌 사본)\n본문");
  Check("경로 id 는 결정적", MemoFormat.IdFromPath("memos/a.md"), MemoFormat.IdFromPath("MEMOS/A.md"));
  ```
- [ ] **Step 2:** 푸시 → `test.yml` 이 컴파일 오류(`MemoFormat` 없음)로 실패하는지 확인.
- [ ] **Step 3:** `Memo.cs`·`MemoFormat.cs` 구현. 분류 규칙(위에서부터 처음 맞는 것): 공백뿐 → Blank / `^#\s+` Heading1 / `^##\s+` Heading2 / `^(-\s+)?\[( |x|X)\]\s?` Check / `^\d{1,3}\.\s+` Numbered / `^[-*]\s+` Bullet / 그 밖 Text. (`##` 를 `#` 보다 먼저 검사.) 태그 정규식 `(?<=^|\s)#([^\s#]+)`. `SetChecked`·`UncheckAll` 은 줄바꿈 문자를 그대로 둔다(줄을 나눌 때 구분자를 보존).
- [ ] **Step 4:** 푸시 → `메모 · 형식` 전부 OK, 전체 실패 0.
- [ ] **Step 5:** 커밋 `메모: 파일 형식(머리말·제목·태그·판·줄 분류)`.

---

### Task 2: `MemoStore` — 폴더·저장·충돌·휴지통

**Files:**
- Create: `Flow/Services/MemoStore.cs`
- Modify: `Tests/Flow.Tests/MemoTests.cs` (`MemoStoreTests()`), `Flow.Tests.csproj`, `Program.cs` (`Section("메모 · 파일"); MemoStoreTests();`)

**Interfaces:**
- Consumes: Task 1 전부.
- Produces:
  ```csharp
  public sealed record MemoSummary(Guid Id, string Path, string Title, string Preview,
      IReadOnlyList<string> Tags, bool Pinned, bool Daily, DateTime Updated, string Rev, bool Unreadable);
  public sealed record MemoFile(MemoDocument Doc, string Path, string Rev);
  public enum SaveOutcome { Saved, Unchanged, Conflict, Missing, Failed }
  public sealed record SaveResult(SaveOutcome Outcome, string? Rev, Guid? CopyId);

  public sealed class MemoStore
  {
      public MemoStore(string dataDirectory, Func<bool> canWrite, Func<DateTime> now);
      public string Folder { get; }          // <data>\memos
      public string TrashFolder { get; }     // <data>\memos\휴지통
      public IReadOnlyList<MemoSummary> Memos { get; }   // 휴지통 제외
      public bool Scan();                    // 바뀐 게 있으면 true
      public MemoFile? Load(Guid id);
      public MemoFile? Create(string body);  // canWrite 거짓이면 null
      public SaveResult Save(Guid id, string body, string baseRev);
      public bool SetPinned(Guid id, bool pinned);
      public bool Delete(Guid id);                          // 휴지통으로, deleted=오늘
      public IReadOnlyList<MemoSummary> Trash();
      public bool RestoreFromTrash(Guid id);
      public int PurgeTrash(DateOnly today);                // deleted < today-30 삭제
  }
  ```
  `Save` 규칙: 파일 없음 → `Missing`(본문을 새 메모로 만들고 `CopyId`) / 지금 판 ≠ `baseRev` → `Conflict`(파일 그대로, 본문에 ` (충돌 사본 MM-dd HH:mm)` 붙여 새 메모, `CopyId`) / 본문이 같은 판 → `Unchanged` / 그 밖 → 쓰고 `Saved`(새 `Rev`). 제목이 바뀌어 `FileStem` 이 달라지면 이름 바꾸기. 예외 → `Failed`. `canWrite()` 거짓 → `Failed`(아무것도 안 씀).
  `Scan` 규칙: `Folder` 의 `*.md`·`*.txt`(휴지통 제외) — 기억한 (크기, 고친 시각)과 같으면 다시 읽지 않는다. 읽다 예외 → `Unreadable=true` 요약(제목은 파일 이름), 표시를 기억하지 않아 다음에 다시 읽음. 같은 id 둘 → 고친 시각이 늦은 쪽이 원본, 다른 쪽은 새 id + 제목 뒤 ` (충돌 사본)` 으로 그 자리에서 다시 씀(`canWrite` 일 때만). 머리말 없는 파일은 `IdFromPath(Folder 기준 상대 경로)`.
  쓰기는 늘 `이름.tmp` 에 쓴 뒤 `File.Replace`/`File.Move`(같은 폴더).

- [ ] **Step 1: 실패하는 시험 작성** (`FLOW_DATA_DIR` 없으면 SKIP — `PhoneStoreTests` 와 같은 방식, 루트 `Path.Combine(DataStore.Directory, "memotest")` 를 매번 지우고 시작)
  ```csharp
  var clock = new DateTime(2026, 10, 29, 14, 3, 0);
  var store = new MemoStore(root, () => true, () => clock);
  Check("처음엔 폴더 없음", Directory.Exists(store.Folder), false);
  var a = store.Create("배포 절차 #AK넷\n1. 받기")!;
  Check("파일 이름", Path.GetFileName(a.Path), "배포 절차.md");
  Check("머리말이 붙음", File.ReadAllText(a.Path).StartsWith("---\r\nid: "), true);
  var b = store.Create("배포 절차\n다른 것")!;
  Check("같은 이름은 (2)", Path.GetFileName(b.Path), "배포 절차 (2).md");
  var c = store.Create("배포 절차\n셋째")!;
  Check("(3)", Path.GetFileName(c.Path), "배포 절차 (3).md");
  store.Scan();
  Check("목록 3개", store.Memos.Count, 3);
  Check("태그", store.Memos.First(m => m.Id == a.Doc.Id).Tags[0], "AK넷");

  var saved = store.Save(a.Doc.Id, "배포 절차 v2\n1. 받기", a.Rev);
  Check("저장", saved.Outcome, SaveOutcome.Saved);
  Check("제목 바뀌면 이름도", File.Exists(Path.Combine(store.Folder, "배포 절차 v2.md")), true);
  Check("옛 이름은 없음", File.Exists(a.Path), false);
  Check("같은 글은 Unchanged", store.Save(a.Doc.Id, "배포 절차 v2\n1. 받기", saved.Rev!).Outcome, SaveOutcome.Unchanged);

  // 다른 PC 가 먼저 고침
  var path = store.Load(a.Doc.Id)!.Path;
  File.WriteAllText(path, File.ReadAllText(path).Replace("1. 받기", "1. 받기(다른 PC)"));
  var conflict = store.Save(a.Doc.Id, "배포 절차 v2\n1. 받기(이 PC)", saved.Rev!);
  Check("충돌", conflict.Outcome, SaveOutcome.Conflict);
  Check("원본은 그대로", File.ReadAllText(path).Contains("다른 PC"), true);
  Check("사본 제목", MemoFormat.TitleOf(store.Load(conflict.CopyId!.Value)!.Doc.Body), "배포 절차 v2 (충돌 사본 10-29 14:03)");

  // 대소문자만 다른 이름은 겹침
  var lower = store.Create("배포 절차 V2\n대문자")!;
  Check("대소문자 무시 겹침", Path.GetFileName(lower.Path), "배포 절차 V2 (2).md");

  // 없어진 메모에 저장
  File.Delete(store.Load(b.Doc.Id)!.Path);
  var missing = store.Save(b.Doc.Id, "배포 절차\n살려 줘", "0000000000000000");
  Check("없어진 메모", (missing.Outcome, missing.CopyId.HasValue), (SaveOutcome.Missing, true));

  // 머리말 없는 파일, 같은 id 둘
  File.WriteAllText(Path.Combine(store.Folder, "직접 넣음.txt"), "직접 넣은 메모\n본문");
  File.Copy(store.Load(c.Doc.Id)!.Path, Path.Combine(store.Folder, "배포 절차 (3) (1).md"));
  File.SetLastWriteTime(Path.Combine(store.Folder, "배포 절차 (3) (1).md"), DateTime.Now.AddMinutes(-5));
  Check("훑으면 바뀜", store.Scan(), true);
  Check("직접 넣은 파일도 목록에", store.Memos.Any(m => m.Title == "직접 넣은 메모"), true);
  Check("같은 id 는 하나만", store.Memos.Count(m => m.Id == c.Doc.Id), 1);
  Check("다른 쪽은 충돌 사본", store.Memos.Any(m => m.Title == "배포 절차 (충돌 사본)"), true);
  Check("안 바뀌었으면 false", store.Scan(), false);

  // 잠긴 파일
  var cPath = store.Load(c.Doc.Id)!.Path;
  File.SetLastWriteTime(cPath, DateTime.Now.AddMinutes(1));   // 바뀐 것으로 보이게 (잠그기 전에)
  using (new FileStream(cPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
  {
      store.Scan();
      Check("잠기면 읽지 못함", store.Memos.First(m => m.Id == c.Doc.Id).Unreadable, true);
  }
  store.Scan();
  Check("풀리면 다시 읽음", store.Memos.First(m => m.Id == c.Doc.Id).Unreadable, false);

  // 휴지통
  Check("삭제", store.Delete(c.Doc.Id), true);
  Check("목록에서 빠짐", store.Memos.Any(m => m.Id == c.Doc.Id), false);
  Check("휴지통에", store.Trash().Count(m => m.Id == c.Doc.Id), 1);
  Check("deleted 기록", File.ReadAllText(store.Trash().First().Path).Contains("deleted: 2026-10-29"), true);
  Check("되살리기", store.RestoreFromTrash(c.Doc.Id), true);
  Check("deleted 지워짐", File.ReadAllText(store.Load(c.Doc.Id)!.Path).Contains("deleted:"), false);
  store.Delete(c.Doc.Id);
  Check("30일 안은 남김", store.PurgeTrash(new DateOnly(2026, 11, 28)), 0);
  Check("30일 지나면 지움", store.PurgeTrash(new DateOnly(2026, 11, 29)), 1);

  // 고정
  store.SetPinned(a.Doc.Id, true);
  Check("고정은 판을 안 바꿈", store.Load(a.Doc.Id)!.Rev, store.Memos.First(m => m.Id == a.Doc.Id).Rev);

  // 쓰면 안 될 때
  var waiting = new MemoStore(root, () => false, () => clock);
  Check("기다리는 중엔 안 만듦", waiting.Create("x"), null);
  Check("기다리는 중엔 저장 실패", waiting.Save(a.Doc.Id, "x", "y").Outcome, SaveOutcome.Failed);
  ```
- [ ] **Step 2:** 푸시 → 컴파일 오류로 실패 확인.
- [ ] **Step 3:** `MemoStore` 구현. 같은 이름 찾기는 `Directory.EnumerateFiles` 결과를 `StringComparer.OrdinalIgnoreCase` 로 비교(자기 자신은 제외). `PurgeTrash`: `deleted` 가 `today.AddDays(-30)` **보다 이전**인 것만(2026-10-29 삭제 → 11-28 남김, 11-29 삭제).
- [ ] **Step 4:** 푸시 → `메모 · 파일` 전부 OK.
- [ ] **Step 5:** 커밋 `메모: 파일 저장소(저장·충돌 사본·휴지통·훑기)`.

---

### Task 3: 백업을 zip 으로 (메모 포함)

**Files:**
- Modify: `Flow/Services/BackupService.cs`, `Tests/Flow.Tests/Program.cs` (`BackupTests()` 에 메모 시험 추가, `using System.IO.Compression;`)

**Interfaces:**
- Consumes: `MemoStore(DataStore.Directory, () => true, () => DateTime.Now)`, `Scan`, `Memos`, `Delete`, `MemoFormat.Parse/Serialize`.
- Produces: `BackupEntry` 에 `int Memos` 추가(마지막 위치 매개변수). `Create(tag)` 는 `data-yyyyMMdd-HHmmssfff[-tag].zip` 을 만든다(엔트리 `data.json`, `memos/<파일 이름>` — 휴지통 제외). `List()`·`Summary()`·`Prune()` 은 `data-*.zip` 과 `data-*.json` 을 함께 본다. `Restore(path)`: `.json` 이면 지금처럼 `data.json` 만. `.zip` 이면 `data.json` 을 되돌리고, 메모는 백업의 id 마다 같은 id 의 지금 파일 자리에(없으면 백업의 파일 이름으로, 겹치면 `(2)`) 덮어쓰고, 백업에 없는 지금 메모는 `Delete`(휴지통). `CreateFromJson` 은 `.json` 그대로.

- [ ] **Step 1: 실패하는 시험 추가** (`BackupTests()` 끝)
  ```csharp
  var memos = new MemoStore(DataStore.Directory, () => true, () => DateTime.Now);
  var kept = memos.Create("남을 메모\n백업 전 내용")!;
  var zip = BackupService.Create("memo")!;
  Check("zip 으로 만듦", Path.GetExtension(zip), ".zip");
  using (var archive = ZipFile.OpenRead(zip))
      Check("메모가 들어감", archive.Entries.Any(e => e.FullName == "memos/남을 메모.md"), true);
  Check("목록에 메모 수", BackupService.List().First(e => e.Path == zip).Memos, 1);

  memos.Save(kept.Doc.Id, "남을 메모\n백업 뒤에 고침", kept.Rev);
  var extra = memos.Create("백업 뒤 메모\n새것")!;
  Check("zip 되돌리기", BackupService.Restore(zip), true);
  memos.Scan();
  Check("메모 내용 되돌아옴", memos.Load(kept.Doc.Id)!.Doc.Body.Contains("백업 전 내용"), true);
  Check("백업에 없던 메모는 휴지통", memos.Trash().Any(m => m.Id == extra.Doc.Id), true);
  Check("휴지통은 백업에 안 들어감", ZipFile.OpenRead(BackupService.Create("again")!).Entries.Any(e => e.FullName.Contains("휴지통")), false);
  ```
  기존 `.json` 백업 시험(되돌리기 등)은 그대로 통과해야 한다 — 기존 시험이 `.json` 파일 이름을 가정하는 곳이 있으면 확장자만 고친다.
- [ ] **Step 2:** 푸시 → 실패 확인.
- [ ] **Step 3:** 구현 (`System.IO.Compression.ZipFile`/`ZipArchive`, 이름 인코딩 UTF-8). zip 은 임시 파일에 다 쓴 뒤 옮긴다.
- [ ] **Step 4:** 푸시 → `백업 · 되돌리기` 전부 OK.
- [ ] **Step 5:** 커밋 `백업: data.json 과 메모를 zip 하나로`.

---

### Task 4: `MemoQuery` — 검색·태그·정렬 (순수 함수)

**Files:**
- Create: `Flow/Services/MemoQuery.cs`
- Modify: `Tests/Flow.Tests/MemoTests.cs` (`MemoQueryTests()`), `Flow.Tests.csproj`, `Program.cs`

**Interfaces:**
- Consumes: `MemoSummary`.
- Produces: `public static class MemoQuery`
  - `IReadOnlyList<MemoSummary> Filter(IEnumerable<MemoSummary> memos, string? search, string? tag, Func<Guid, string?> bodyOf)` — `search` 를 공백으로 나눈 낱말이 모두 (제목 또는 본문)에 들어 있어야 한다(대소문자 무시). `#` 로 시작하는 낱말은 태그 조건. `tag` 인자도 태그 조건. 정렬: 고정 먼저, 그다음 `Updated` 내림차순, 같으면 제목.
  - `IReadOnlyList<string> AllTags(IEnumerable<MemoSummary> memos)` — 메모 수 많은 순, 같으면 이름순.
  (`bodyOf` 는 본문 검색을 위해 필요할 때만 파일을 읽게 한다 — 위젯은 본문을 들고 있지 않다.)

- [ ] **Step 1: 실패하는 시험**
  ```csharp
  MemoSummary S(string title, bool pinned, int minutes, params string[] tags) =>
      new(Guid.NewGuid(), "", title, "", tags, pinned, false, new DateTime(2026, 10, 29).AddMinutes(minutes), "", false);
  var list = new[] { S("정산 절차", false, 10, "정산"), S("AK넷 배포", true, 1, "AK넷"), S("회의", false, 20), S("AK넷 정산", false, 5, "AK넷", "정산") };
  var bodies = list.ToDictionary(m => m.Id, m => m.Title == "회의" ? "단가표 확인" : "");
  string Titles(IEnumerable<MemoSummary> r) => string.Join(",", r.Select(m => m.Title));
  Check("정렬: 고정 먼저, 최근 순", Titles(MemoQuery.Filter(list, null, null, id => bodies[id])), "AK넷 배포,회의,정산 절차,AK넷 정산");
  Check("검색: 제목", Titles(MemoQuery.Filter(list, "정산", null, id => bodies[id])), "정산 절차,AK넷 정산");
  Check("검색: 본문", Titles(MemoQuery.Filter(list, "단가표", null, id => bodies[id])), "회의");
  Check("검색: 낱말 모두", Titles(MemoQuery.Filter(list, "ak넷 정산", null, id => bodies[id])), "AK넷 정산");
  Check("검색칸의 #태그", Titles(MemoQuery.Filter(list, "#정산", null, id => bodies[id])), "정산 절차,AK넷 정산");
  Check("태그 인자", Titles(MemoQuery.Filter(list, null, "AK넷", id => bodies[id])), "AK넷 배포,AK넷 정산");
  Check("모든 태그", string.Join(",", MemoQuery.AllTags(list)), "AK넷,정산");
  ```
  (`AllTags`: AK넷 2·정산 2 → 이름순 `AK넷,정산`.)
- [ ] **Step 2–4:** 푸시 → 실패 확인 → 구현 → 통과.
- [ ] **Step 5:** 커밋 `메모: 검색·태그·정렬`.

---

### Task 5: 위젯 `메모` 탭

**Files:**
- Modify: `Flow/ViewModels/MainViewModel.cs`, `Flow/Views/MainWindow.axaml`, `Flow/Views/MainWindow.axaml.cs`, `Flow/Styles/Controls.axaml`
- Create: `Flow/ViewModels/MemoRow.cs`

**Interfaces:**
- Consumes: `MemoStore`, `MemoQuery`, `DataStore.WaitingForFolder`.
- Produces (MainViewModel):
  - `public MemoStore Memos { get; }` — 생성자에서 `new MemoStore(DataStore.Directory, () => !_store.WaitingForFolder, () => DateTime.Now)`, 시작 시 `Scan()` + `PurgeTrash(_today)`.
  - `SelectedTab == 3` → `IsMemoTab`. 탭 줄에 `메모` 버튼(`CommandParameter="3"`).
  - `ObservableCollection<MemoRow> MemoRows`, `ObservableCollection<string> MemoTags`, `[ObservableProperty] string MemoSearch`, `[ObservableProperty] string? MemoTag` — 바뀌면 `RefreshMemos()`.
  - `MemoRow`: `Id, Title, Preview, IsPinned, IsDaily, IsUnreadable`.
  - `public event Action<Guid?>? MemoOpenRequested;` `[RelayCommand] void OpenMemo(Guid id)`.
  - 메모 탭에서 빠른 추가(Enter) → `Memos.Create(입력)` → `RefreshMemos()` → `MemoOpenRequested(id)`. 입력칸 안내 글 `새 메모 제목`.
  - 10초 `DispatcherTimer` → `if (Memos.Scan()) { RefreshMemos(); MemosChanged?.Invoke(); }` — `public event Action? MemosChanged;`(메모 창이 듣는다).
  - `bodyOf` 는 `id => Memos.Load(id)?.Doc.Body` (검색어가 있을 때만 불린다).
- MainWindow.axaml.cs: `MemoOpenRequested` 를 받아 Task 6 의 `MemoWindow` 를 하나만 만들어 보여 주고 그 메모를 고른다. 위젯을 끝낼 때 메모 창도 닫는다.

- [ ] **Step 1:** `MemoRow.cs` 와 MainViewModel 속성·명령·타이머 추가, 탭 버튼과 `IsMemoTab` 패널(검색칸, 태그 칩 줄 — 누르면 `MemoTag` 토글, 목록 — 📌/↻ 표시와 흐린 첫머리, `IsUnreadable` 은 흐리게 `읽지 못함`) 추가. 컴팩트 모드에서는 다른 탭과 같은 규칙을 따른다.
- [ ] **Step 2:** 푸시 → `test.yml` 빌드 통과(UI 는 컴파일로만 확인).
- [ ] **Step 3:** 커밋 `위젯: 메모 탭(검색·태그·목록·새 메모)`.

(화면 확인은 Task 8 릴리스 후 사용자 PC 에서.)

---

### Task 6: 메모 창 (읽기/쓰기, 자동 저장, 충돌 띠)

**Files:**
- Create: `Flow/ViewModels/MemoEditorState.cs` (순수 상태 기계), `Flow/ViewModels/MemoEditorViewModel.cs`, `Flow/Views/MemoWindow.axaml`, `Flow/Views/MemoWindow.axaml.cs`
- Modify: `Flow/Models/AppData.cs`(Settings 에 `[JsonIgnore] double? MemoWindowLeft/Top`, `double MemoWindowWidth = 820`, `MemoWindowHeight = 600`), `Flow/Models/DeviceState.cs`(같은 4개), `Flow/Services/DeviceStore.cs`(Apply/Capture 에 4개), `Tests/Flow.Tests/MemoTests.cs`(`MemoEditorTests()`), `Flow.Tests.csproj`(MemoEditorState.cs)

**Interfaces:**
- Consumes: `MemoStore`(Load/Save/SetPinned/Delete/Trash/RestoreFromTrash), `MemoFormat.Lines/SetChecked`, `MemoQuery`, MainViewModel 의 `MemoRows`·`MemoSearch`·`MemoTag`·`MemosChanged`.
- Produces:
  - `MemoEditorState`(UI 없음, 시험 대상):
    ```csharp
    public sealed class MemoEditorState
    {
        public MemoEditorState(MemoStore store);
        public Guid? CurrentId { get; }  public string Body { get; }  public bool IsDirty { get; }
        public bool IsEditing { get; set; }
        public string? Banner { get; }   // 충돌·지워짐·저장 실패 안내 (null = 없음)
        public void Open(Guid id);       // 먼저 Flush()
        public void Edit(string body);   // Body 갱신, IsDirty=true
        public SaveResult? Flush();      // dirty 면 store.Save(CurrentId, Body, baseRev)
        public void ToggleCheck(int lineIndex);  // SetChecked → 바로 Flush
        public void OnExternalChange();  // 폴더 훑기 뒤 불림
    }
    ```
    규칙: `Flush` 결과 `Saved` → baseRev 갱신, `IsDirty=false`, `Banner=null`. `Conflict` → 원본을 다시 읽어 보여 주고 `IsDirty=false`, `Banner="다른 곳에서 먼저 고쳐서, 내 글은 충돌 사본으로 따로 남겼습니다"`. `Missing` → `CurrentId=CopyId`, `Banner="이 메모는 다른 곳에서 지워졌습니다. 쓰던 글은 새 메모로 남겼습니다"`. `Failed` → `IsDirty` 유지, `Banner="저장하지 못했습니다 · 다시 시도 중"`. `OnExternalChange`: `IsEditing || IsDirty` 면 **아무것도 하지 않는다**; 아니면 다시 읽고, 파일이 없으면 `Banner="이 메모는 다른 곳에서 지워졌습니다"`.
  - `MemoEditorViewModel`: `MemoEditorState` 를 감싼다. 본문 `TextBox` 의 `Text` 바뀜 → `Edit` + 1초 `DispatcherTimer` 재시작 → `Flush`. 실패면 5초 타이머로 다시. `Lines`(읽기 모드 `ItemsControl` 원본), `Tags`(제목 아래 칩), 명령 `ToggleEdit`(Ctrl+E, Esc 는 읽기로), `TogglePin`, `Delete`(휴지통 → 목록 다음 메모), `ShowTrash`/`Restore`, `ToggleCheck(int)`. 목록은 MainViewModel 의 `MemoRows` 를 같이 쓴다.
  - `MemoWindow`: 왼쪽(검색칸·태그 칩·목록·맨 아래 `휴지통 n`), 오른쪽(도구 줄 📌 · 삭제 · 띠, 본문). 읽기 모드: 줄 종류별 `DataTemplate`(Heading1 18pt 굵게, Heading2 15pt 굵게, Numbered 는 `Marker`+글, Bullet 는 `•`+글, Check 는 누를 수 있는 동그란 체크 + 글(체크면 흐리게·취소선), Blank 는 반 줄 높이). 본문을 누르면 쓰기 모드. 쓰기 모드: `TextBox`(AcceptsReturn, 줄바꿈 Wrap, 고정폭 아님). 창 위치·크기는 Settings/DeviceState 의 MemoWindow* 로 복원·저장(`MainWindow.SaveWindowPlacement` 와 같은 방식, 화면 밖이면 가운데). `Closing` 에서 `Flush`. 휴지통 보기에서는 본문 읽기 전용 + `되살리기` 버튼.
  - (스펙의 '태그를 줄 안 칩으로'는 1단계에서 제목 아래 칩 줄 + 본문 안 `#태그` 글자 색으로 대신한다.)

- [ ] **Step 1: 실패하는 시험** (`FLOW_DATA_DIR` 필요, 루트 `memoeditortest`)
  ```csharp
  var store = new MemoStore(root, () => true, () => new DateTime(2026, 10, 29, 14, 3, 0));
  var memo = store.Create("절차\n[ ] 하나")!;
  var editor = new MemoEditorState(store);
  editor.Open(memo.Doc.Id);
  editor.ToggleCheck(1);
  Check("체크는 바로 저장", store.Load(memo.Doc.Id)!.Doc.Body.Contains("[x] 하나"), true);

  editor.IsEditing = true;
  editor.Edit("절차\n[x] 하나\n둘");
  var p = store.Load(memo.Doc.Id)!.Path;
  File.WriteAllText(p, File.ReadAllText(p).Replace("하나", "하나(다른 PC)"));
  store.Scan(); editor.OnExternalChange();
  Check("쓰는 중엔 본문을 안 바꿈", editor.Body, "절차\n[x] 하나\n둘");
  var r = editor.Flush()!;
  Check("저장 때 충돌", r.Outcome, SaveOutcome.Conflict);
  Check("띠", editor.Banner, "다른 곳에서 먼저 고쳐서, 내 글은 충돌 사본으로 따로 남겼습니다");
  Check("원본을 보여 줌", editor.Body.Contains("다른 PC"), true);

  editor.IsEditing = false;
  File.WriteAllText(p, File.ReadAllText(p).Replace("절차", "절차 v3"));
  store.Scan(); editor.OnExternalChange();
  Check("보기만 할 땐 새 내용으로", MemoFormat.TitleOf(editor.Body), "절차 v3");

  editor.IsEditing = true; editor.Edit("절차 v3\n살려 줘");
  File.Delete(store.Load(memo.Doc.Id)!.Path);
  Check("지워진 메모에 저장", editor.Flush()!.Outcome, SaveOutcome.Missing);
  Check("새 메모로 남김", store.Load(editor.CurrentId!.Value)!.Doc.Body.Replace("\r\n", "\n"), "절차 v3\n살려 줘");

  var blocked = new MemoEditorState(new MemoStore(root, () => false, () => DateTime.Now));
  blocked.Open(editor.CurrentId!.Value); blocked.Edit("x");
  blocked.Flush();
  Check("못 쓰면 dirty 유지", (blocked.IsDirty, blocked.Banner), (true, "저장하지 못했습니다 · 다시 시도 중"));
  ```
  (`Load` 가 돌려주는 `Body` 는 `\r\n` 이 아닌 원래 줄바꿈일 수 있다 — 시험에서 비교할 때는 `MemoEditorState.Body` 를 `\n` 으로 맞춰 둔다: `Open`/다시 읽기에서 줄바꿈을 `\n` 으로 정규화.)
- [ ] **Step 2:** 푸시 → 실패 확인.
- [ ] **Step 3:** `MemoEditorState` 구현 → 푸시 → `메모 · 편집` OK.
- [ ] **Step 4:** `MemoEditorViewModel`·`MemoWindow`·창 위치 저장 구현, Task 5 의 `MemoOpenRequested` 연결, `MemosChanged` → `OnExternalChange`. 푸시 → 빌드·시험 통과.
- [ ] **Step 5:** 커밋 `메모 창: 읽기·쓰기·자동 저장·충돌 띠·휴지통`.

---

### Task 7: 사용법·릴리스 (v1.6.0)

**Files:**
- Modify: `사용법.txt`, `README.md`(화면 표에 `메모` 줄, '메모' 절), 앱 안 사용법(`MainWindow.axaml` 의 도움말 패널), `Flow/Flow.csproj`(`<Version>1.6.0</Version>`)

- [ ] **Step 1:** 사용법에 메모 절 추가: 메모 탭에서 새 메모, 메모 창(읽기/쓰기, Ctrl+E), 서식 표(스펙 '가벼운 서식' 표 그대로), `#태그`·검색·📌, 휴지통 30일, 메모는 `데이터 폴더\memos` 에 `.md` 로 저장되어 메모장으로도 열린다, 백업이 zip 이 됨, 충돌 사본이 생기는 경우.
- [ ] **Step 2:** 버전 올려 커밋·푸시 → `test.yml` 초록 → PR → main 병합 → `release.yml` 수동 실행(version `1.6.0`, 노트: "메모가 생겼습니다. 메모 탭에서 업무 절차처럼 긴 글을 쓰고, 검색·태그·고정으로 찾습니다. 메모는 데이터 폴더의 memos 에 파일로 저장되고 백업에 함께 들어갑니다.") → 릴리스에 `Flow.exe` 확인.
- [ ] **Step 3:** 사용자에게 PC 에서 확인할 것 목록 전달: 새 메모 → 쓰기 → 1초 뒤 `memos` 폴더에 파일, 제목 바꾸면 파일 이름 바뀜, 체크 칸 누르기, 검색·태그·고정, 삭제·되살리기, 백업 만들고 되돌리기, (PC 두 대면) 같은 메모 동시 고치기 → 충돌 사본.
