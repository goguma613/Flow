using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Flow.Models;
using Flow.Services;
using Flow.ViewModels;

namespace Flow.Tests;

internal static partial class Program
{
    private static void MemoFormatTests()
    {
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
    }

    private static void MemoStoreTests()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 메모 파일 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = Path.Combine(DataStore.Directory, "memotest");
        if (Directory.Exists(root)) Directory.Delete(root, true);

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

        // 다른 PC 가 이름을 바꿨는데 이 PC 는 아직 훑지 않음
        var r = store.Create("이름 바뀔 메모\n본문")!;
        var moved = Path.Combine(store.Folder, "다른 이름.md");
        File.Move(r.Path, moved);
        Check("이름이 바뀐 메모도 읽음", store.Load(r.Doc.Id)?.Path, moved);
        var r2 = store.Create("또 바뀔 메모\n본문")!;
        File.Move(r2.Path, Path.Combine(store.Folder, "또 다른 이름.md"));
        Check("이름이 바뀐 메모에 저장", store.Save(r2.Doc.Id, "또 바뀔 메모\n본문 고침", r2.Rev).Outcome, SaveOutcome.Saved);
        Check("tmp·bak 가 남지 않음", Directory.EnumerateFiles(store.Folder).Any(f => f.EndsWith(".tmp") || f.EndsWith(".bak")), false);
    }

    private static void MemoQueryTests()
    {
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

        // 추가: 태그 대소문자, 본문 읽기 최소화, 같은 시각 정렬
        Check("태그는 대소문자 무시", Titles(MemoQuery.Filter(list, "#ak넷", null, id => bodies[id])), "AK넷 배포,AK넷 정산");
        Check("태그 인자의 # 는 뗌", Titles(MemoQuery.Filter(list, null, "#정산", id => bodies[id])), "정산 절차,AK넷 정산");
        Check("검색칸 태그와 낱말", Titles(MemoQuery.Filter(list, "#ak넷 배포", null, id => bodies[id])), "AK넷 배포");
        var reads = 0;
        MemoQuery.Filter(list, "정산", null, id => { reads++; return ""; });
        Check("제목에 다 있으면 본문을 안 읽음", reads, 2);   // 제목에 없는 두 메모만 읽는다
        reads = 0;
        MemoQuery.Filter(list, "#정산", null, id => { reads++; return ""; });
        Check("태그만이면 본문을 안 읽음", reads, 0);
        var same = new[] { S("나", false, 5), S("가", false, 5) };
        Check("같은 시각이면 제목순", Titles(MemoQuery.Filter(same, null, null, _ => "")), "가,나");
    }
    private static void MemoEditorTests()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 메모 편집 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = Path.Combine(DataStore.Directory, "memoeditortest");
        if (Directory.Exists(root)) Directory.Delete(root, true);

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

        // 추가: 줄바꿈 맞춤, 깨끗하면 저장 안 함, 보기 중 사라짐, 실패하면 다른 메모로 넘어가지 않음
        var crlf = store.Create("가\r\n나")!;
        var plain = new MemoEditorState(store);
        plain.Open(crlf.Doc.Id);
        Check("연 본문은 \\n 으로", plain.Body, "가\n나");
        Check("고친 것 없으면 저장 안 함", plain.Flush(), (SaveResult?)null);
        File.Delete(crlf.Path);
        store.Scan(); plain.OnExternalChange();
        Check("보는 중 지워짐", plain.Banner, "이 메모는 다른 곳에서 지워졌습니다");
        Check("지워져도 글은 그대로 보임", plain.Body, "가\n나");
        Check("지워진 메모는 Flush 해도 되살리지 않음", plain.Flush(), (SaveResult?)null);

        blocked.Open(memo.Doc.Id);
        Check("저장 못 하면 다른 메모로 안 넘어감", (blocked.CurrentId, blocked.Body), (editor.CurrentId, "x"));

        var other = store.Create("다른 메모")!;
        editor.IsEditing = true; editor.Edit("절차 v3\n살려 줘\n더");
        editor.Open(other.Doc.Id);
        Check("메모를 바꿀 때 저장", store.Load(editor.CurrentId!.Value) is { } m && m.Doc.Id == other.Doc.Id, true);
        Check("앞 메모에 저장됨", store.Memos.Any(x => x.Preview == "살려 줘 더"), true);
        Check("바꾼 뒤엔 저장할 글 없음", editor.IsDirty, false);
    }
}
