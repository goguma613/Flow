using System;
using System.IO;
using System.Linq;
using Flow.Models;
using Flow.Services;

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
}
