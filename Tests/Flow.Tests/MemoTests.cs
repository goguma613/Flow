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
        // 머리말 tags
        var tagText = "---\r\nid: 3f2a9c1e-0000-4000-8000-000000000001\r\ndeleted: 2026-10-30\r\ntags: AK넷, #정산 , ak넷,, 정산\r\ncolor: blue\r\nshape: round\r\n---\r\n본문 #정산 #ak넷\r\n";
        var tagDoc = MemoFormat.Parse(tagText, Guid.Empty);
        Check("tags: # 떼고 중복 뺌", string.Join(",", tagDoc.Tags!), "AK넷,정산");
        var tagOut = MemoFormat.Serialize(tagDoc);
        Check("tags 쓰기: deleted 뒤 모르는 키 앞", tagOut,
            "---\r\nid: 3f2a9c1e-0000-4000-8000-000000000001\r\ndeleted: 2026-10-30\r\ntags: AK넷, 정산\r\ncolor: blue\r\nshape: round\r\n---\r\n본문 #정산 #ak넷\r\n");
        Check("tags 왕복", MemoFormat.Serialize(MemoFormat.Parse(tagOut, Guid.Empty)), tagOut);
        Check("빈 tags 는 안 씀", MemoFormat.Serialize(MemoFormat.Parse("---\r\nid: 3f2a9c1e-0000-4000-8000-000000000001\r\ntags: , #\r\n---\r\n본문", Guid.Empty)).Contains("tags:"), false);
        Check("tags 없는 머리말", doc.Tags!.Count, 0);
        Check("머리말+본문 태그 합침", string.Join(",", MemoFormat.TagsOfDoc(tagDoc with { Tags = new[] { "AK넷" }, Body = "#정산 #ak넷" })), "AK넷,정산");
        Check("태그 정리 #AK넷", MemoFormat.NormalizeTag("#AK넷"), "AK넷");
        Check("태그 정리 공백", MemoFormat.NormalizeTag(" 정산 "), "정산");
        Check("태그 정리 안의 공백", MemoFormat.NormalizeTag("a b"), (string?)null);
        Check("태그 정리 쉼표", MemoFormat.NormalizeTag("a,b"), (string?)null);
        Check("태그 정리 # 만", MemoFormat.NormalizeTag("#"), (string?)null);
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
        Check("들여쓴 체크", (MemoFormat.Classify(0, "  [ ] 들여쓴 체크").Kind, MemoFormat.Classify(0, "  [ ] 들여쓴 체크").Checked, MemoFormat.Classify(0, "  [ ] 들여쓴 체크").Text), (MemoLineKind.Check, false, "들여쓴 체크"));
        Check("[] 도 빈 체크", (MemoFormat.Classify(0, "[] 붙인 체크").Kind, MemoFormat.Classify(0, "[] 붙인 체크").Checked, MemoFormat.Classify(0, "[] 붙인 체크").Text), (MemoLineKind.Check, false, "붙인 체크"));
        Check("들여쓴 번호", (MemoFormat.Classify(0, "\t1. 단계").Kind, MemoFormat.Classify(0, "\t1. 단계").Marker), (MemoLineKind.Numbered, "1."));
        Check("들여쓴 점", (MemoFormat.Classify(0, "  - 점").Kind, MemoFormat.Classify(0, "  - 점").Text), (MemoLineKind.Bullet, "점"));
        Check("들여쓴 # 는 소제목 아님", MemoFormat.Classify(0, "  # 제목").Kind, MemoLineKind.Text);
        Check("[] 켜기·들여쓰기 보존", MemoFormat.SetChecked("  [] a", 0, true), "  [x] a");
        Check("들여쓴 모두 풀기", MemoFormat.UncheckAll("  - [X] b"), "  - [ ] b");
        Check("[] 는 풀어도 그대로", MemoFormat.UncheckAll("[] a"), "[] a");
        Check("체크 켜기", MemoFormat.SetChecked("[ ] a\r\n[x] b", 0, true), "[x] a\r\n[x] b");
        Check("체크 아닌 줄은 그대로", MemoFormat.SetChecked("a\n[ ] b", 0, true), "a\n[ ] b");
        Check("모두 풀기", MemoFormat.UncheckAll("[x] a\n- [X] b\n글 [x]"), "[ ] a\n- [ ] b\n글 [x]");
        Check("이름: 못 쓰는 글자", MemoFormat.FileStem("배포/정산: 절차?"), "배포정산 절차");
        Check("이름: 60자", MemoFormat.FileStem(new string('가', 80)).Length, 60);
        Check("이름: 예약어", MemoFormat.FileStem("con"), "메모");
        Check("이름: 다 지워짐", MemoFormat.FileStem("???"), "메모");
        Check("이름: 앞뒤 점·공백", MemoFormat.FileStem(" .절차. "), "절차");
        Check("첫머리", MemoFormat.PreviewOf("제목\n\n  첫째   줄\n둘째"), "첫째 줄 둘째");
        // 목록 줄에 보일 때는 줄 머리 기호를 뗀다
        Check("기호 떼기: #", MemoFormat.PlainLine("# 큰소제목"), "큰소제목");
        Check("기호 떼기: ##", MemoFormat.PlainLine("## 소제목"), "소제목");
        Check("기호 떼기: 번호", MemoFormat.PlainLine("12. 번호"), "번호");
        Check("기호 떼기: 점 -", MemoFormat.PlainLine("  - 점"), "점");
        Check("기호 떼기: 점 *", MemoFormat.PlainLine("* 별"), "별");
        Check("기호 떼기: [ ]", MemoFormat.PlainLine("[ ] 체크"), "체크");
        Check("기호 떼기: [x]", MemoFormat.PlainLine("[x] 끝냄"), "끝냄");
        Check("기호 떼기: []", MemoFormat.PlainLine("[] 붙인 체크"), "붙인 체크");
        Check("기호 떼기: - [ ]", MemoFormat.PlainLine("\t- [X] 들여쓴 체크"), "들여쓴 체크");
        Check("기호 떼기: 기호만", MemoFormat.PlainLine("[ ]"), "");
        Check("기호 떼기: ## 만", MemoFormat.PlainLine("## "), "");
        Check("기호 떼기: 태그는 그대로", MemoFormat.PlainLine("#AK넷 메모"), "#AK넷 메모");
        Check("기호 떼기: ### 는 그대로", MemoFormat.PlainLine("### 셋"), "### 셋");
        Check("기호 떼기: 소수는 그대로", MemoFormat.PlainLine("3.14 원주율"), "3.14 원주율");
        Check("기호 떼기: 줄표는 그대로", MemoFormat.PlainLine("-5도"), "-5도");
        Check("기호 떼기: 보통 글", MemoFormat.PlainLine("  보통 글  "), "보통 글");
        Check("목록 제목: # 뗌", MemoFormat.PlainTitle("# 배포 절차"), "배포 절차");
        Check("목록 제목: 떼서 비면 그대로", MemoFormat.PlainTitle("[ ]"), "[ ]");
        Check("첫머리: 기호 뗌", MemoFormat.PreviewOf("# 제목\n## 소제목\n# 큰소제목\n1. 번호\n- 점\n- [ ] 체크\n[x] 끝\n[]\n#태그"),
            "소제목 큰소제목 번호 점 체크 끝 #태그");
        Check("제목은 원래 줄 그대로(파일 이름·검색)", MemoFormat.TitleOf("# 배포 절차\n본문"), "# 배포 절차");
        Check("들여쓰기 단계", (MemoFormat.Classify(0, "- 점").Indent, MemoFormat.Classify(0, "  - 점").Indent,
            MemoFormat.Classify(0, "\t[ ] 체크").Indent, MemoFormat.Classify(0, "    1. 번호").Indent, MemoFormat.Classify(0, "   - 점").Indent),
            (0, 1, 2, 2, 1));
        Check("들여쓰기 6단계까지", MemoFormat.IndentOf(new string(' ', 40) + "- 점"), 6);
        Check("보통 글은 들여쓰기 0", MemoFormat.Classify(0, "    글").Indent, 0);
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

        // 다른 휴지통 파일 하나가 안 읽혀도 되살린다
        var stuck = store.Create("잠길 휴지통 메모\n내용")!;
        store.Delete(stuck.Doc.Id);
        store.Delete(c.Doc.Id);
        var stuckPath = store.Trash().First(m => m.Id == stuck.Doc.Id).Path;
        using (new FileStream(stuckPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check("다른 휴지통 파일이 잠겨도 되살림", store.RestoreFromTrash(c.Doc.Id), true);
        Check("되살린 메모가 목록에", store.Memos.Count(m => m.Id == c.Doc.Id), 1);

        // 되살린 뒤 휴지통 파일을 못 지워도 성공 · 다시 눌러도 사본 없이 휴지통만 비움
        store.Delete(c.Doc.Id);
        var cTrash = store.Trash().First(m => m.Id == c.Doc.Id).Path;
        using (new FileStream(cTrash, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check("휴지통 파일을 못 지워도 성공", store.RestoreFromTrash(c.Doc.Id), true);
        Check("휴지통에 남음", store.Trash().Count(m => m.Id == c.Doc.Id), 1);
        Check("다시 되살리기도 성공", store.RestoreFromTrash(c.Doc.Id), true);
        store.Scan();
        Check("사본 없이 하나", store.Memos.Count(m => m.Id == c.Doc.Id), 1);
        Check("휴지통에서 빠짐", store.Trash().Count(m => m.Id == c.Doc.Id), 0);
        store.RestoreFromTrash(stuck.Doc.Id);
        store.Delete(stuck.Doc.Id);
        store.PurgeTrash(new DateOnly(2027, 1, 1));

        store.Delete(c.Doc.Id);
        Check("30일 안은 남김", store.PurgeTrash(new DateOnly(2026, 11, 28)), 0);
        Check("30일 지나면 지움", store.PurgeTrash(new DateOnly(2026, 11, 29)), 1);

        // 고정
        store.SetPinned(a.Doc.Id, true);
        Check("고정은 판을 안 바꿈", store.Load(a.Doc.Id)!.Rev, store.Memos.First(m => m.Id == a.Doc.Id).Rev);

        // 머리말 태그
        var tagBefore = store.Load(a.Doc.Id)!;
        Check("태그 고치기", store.SetTags(a.Doc.Id, new[] { "#정산", "정산", "a b", "AK넷", "ak넷", "새태그" }), true);
        var tagged = store.Load(a.Doc.Id)!;
        Check("머리말에 tags", File.ReadAllText(tagged.Path).Contains("\r\ntags: 정산, AK넷, 새태그\r\n"), true);
        Check("태그는 판을 안 바꿈", tagged.Rev, tagBefore.Rev);
        Check("본문은 그대로", tagged.Doc.Body, tagBefore.Doc.Body);
        Check("목록 태그에 머리말 태그", string.Join(",", store.Memos.First(m => m.Id == a.Doc.Id).Tags), "정산,AK넷,새태그");
        var bodySaved = store.Save(a.Doc.Id, "배포 절차 v2\n1. 받기\n더", tagged.Rev);
        Check("본문 저장", bodySaved.Outcome, SaveOutcome.Saved);
        var afterSave = File.ReadAllText(store.Load(a.Doc.Id)!.Path);
        Check("저장해도 tags·pinned 남음", (afterSave.Contains("tags: 정산, AK넷, 새태그"), afterSave.Contains("pinned: true")), (true, true));
        Check("태그 비우기", store.SetTags(a.Doc.Id, Array.Empty<string>()), true);
        Check("비우면 tags 줄 없음", File.ReadAllText(store.Load(a.Doc.Id)!.Path).Contains("tags:"), false);

        // 쓰면 안 될 때
        var waiting = new MemoStore(root, () => false, () => clock);
        Check("기다리는 중엔 안 만듦", waiting.Create("x"), null);
        Check("기다리는 중엔 태그 안 고침", waiting.SetTags(a.Doc.Id, new[] { "x" }), false);
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

        // 찾다가 다시 훑으며 본 바뀜도 다음 Scan() 이 알린다(그래야 목록과 메모 창이 다시 읽는다)
        var sc = store.Create("훑기 알림 메모\n본문")!;
        store.Scan();
        var scMoved = Path.Combine(store.Folder, "훑기 알림 다른 이름.md");
        File.Move(sc.Path, scMoved);
        Check("안쪽 훑기로 찾음", store.Load(sc.Doc.Id)?.Path, scMoved);
        Check("다음 Scan 이 바뀜을 알림", store.Scan(), true);
        Check("그다음 Scan 은 조용함", store.Scan(), false);

        // 다른 PC 가 X 를 옮기고 그 자리에 다른 메모 Y 를 둠. 이 PC 는 아직 훑지 않았다.
        (string Moved, Guid Other) Swap(MemoFile x, string movedName)
        {
            store.Scan();
            var movedPath = Path.Combine(store.Folder, movedName);
            File.Move(x.Path, movedPath);
            var other = Guid.NewGuid();
            File.WriteAllText(x.Path, "---\r\nid: " + other + "\r\n---\r\n남의 메모\r\n와이");
            return (movedPath, other);
        }
        Guid IdAt(string p) => MemoFormat.Parse(File.ReadAllText(p), Guid.Empty).Id;

        var lx = store.Create("자리 바뀔 메모\n엑스")!;
        var (lxMoved, _) = Swap(lx, "옮겨진 엑스.md");
        var lxLoaded = store.Load(lx.Doc.Id);
        Check("그 자리의 남의 메모를 X 로 읽지 않음", (lxLoaded?.Path, lxLoaded?.Doc.Id), (lxMoved, (Guid?)lx.Doc.Id));

        var px = store.Create("고정할 메모\n엑스")!;
        var (pxMoved, pxOther) = Swap(px, "옮겨진 고정.md");
        Check("고정은 옮겨진 X 에", store.SetPinned(px.Doc.Id, true), true);
        Check("남의 메모 id 는 그대로", IdAt(px.Path), pxOther);
        Check("X 가 고정됨", MemoFormat.Parse(File.ReadAllText(pxMoved), Guid.Empty).Pinned, true);

        var dx = store.Create("지울 메모\n엑스")!;
        var (dxMoved, dxOther) = Swap(dx, "옮겨진 지울.md");
        Check("지우기는 옮겨진 X 를", store.Delete(dx.Doc.Id), true);
        Check("남의 메모는 그대로", (File.Exists(dx.Path), IdAt(dx.Path)), (true, dxOther));
        Check("X 가 휴지통에", (File.Exists(dxMoved), store.Trash().Any(m => m.Id == dx.Doc.Id)), (false, true));

        var vx = store.Create("저장할 메모\n엑스")!;
        var (_, vxOther) = Swap(vx, "옮겨진 저장.md");
        Check("저장은 옮겨진 X 에", store.Save(vx.Doc.Id, "저장할 메모\n엑스 고침", vx.Rev).Outcome, SaveOutcome.Saved);
        Check("남의 메모를 덮지 않음", IdAt(vx.Path), vxOther);

        var gx = store.Create("사라질 메모\n엑스")!;
        store.Scan();
        File.Delete(gx.Path);
        File.WriteAllText(gx.Path, "---\r\nid: " + Guid.NewGuid() + "\r\n---\r\n남의 메모\r\n와이");
        Check("X 가 없으면 남의 메모를 X 로 안 봄", store.Load(gx.Doc.Id), (MemoFile?)null);

        // 앱이 끝날 때 저장하지 못한 글은 머리말+본문 파일로 따로 남긴다
        var rescue = MemoStore.WriteRecovery(Path.Combine(root, "unsaved"), "급한 메모 #태그\n못 쓴 글", clock);
        Check("남긴 파일 이름", Path.GetFileName(rescue), "급한 메모 20261029-140300.md");
        var rescued = MemoFormat.Parse(File.ReadAllText(rescue!), Guid.Empty);
        Check("머리말+본문으로 남김", (rescued.HasHeader, rescued.Body.Replace("\r\n", "\n")), (true, "급한 메모 #태그\n못 쓴 글"));

        MemoDailyTests();
    }

    // PhoneLinkTests 의 Mon·Tue(2026-10-05·06)에 이어지는 날. 정적 초기화 순서에 기대지 않으려고 값을 직접 쓴다.
    private static readonly DateOnly Wed = new(2026, 10, 7);
    private static readonly DateOnly Thu = new(2026, 10, 8);

    /// <summary>매일 초기화·모두 풀기. 다른 시험과 섞이지 않게 따로 만든 폴더에서 한다.</summary>
    private static void MemoDailyTests()
    {
        var root = Path.Combine(DataStore.Directory, "memotest-daily");
        if (Directory.Exists(root)) Directory.Delete(root, true);

        var clock = new DateTime(2026, 10, 5, 9, 0, 0);
        var store = new MemoStore(root, () => true, () => clock);
        var waitingStore = new MemoStore(root, () => false, () => clock);

        string Body(Guid memo) => store.Load(memo)!.Doc.Body.Replace("\r\n", "\n");
        string Header(Guid memo)
        {
            var text = File.ReadAllText(store.Load(memo)!.Path);
            return text[..text.IndexOf("\r\n---\r\n", 3, StringComparison.Ordinal)];
        }
        string ResetLine(DateOnly day) => "reset: " + day.ToString("yyyy-MM-dd");

        var m = store.Create("절차\n[x] 하나\n  - [X] 둘\n글 [x]")!;
        var other = store.Create("다른 절차\n[x] 하나\n- [X] 둘")!;
        var plain = store.Create("평범한 메모\n[x] 그대로")!;
        var empty = store.Create("빈 절차\n[ ] 하나\n글")!;
        var trashed = store.Create("버린 메모\n[x] 하나")!;
        store.Scan();

        // 켜기: 켠 날은 안 풀리고 판은 그대로
        var revBefore = store.Load(m.Doc.Id)!.Rev;
        Check("켜기", store.SetDaily(m.Doc.Id, true, Mon), true);
        Check("머리말에 daily·reset", (Header(m.Doc.Id).Contains("daily: true"), Header(m.Doc.Id).Contains(ResetLine(Mon))), (true, true));
        Check("켜도 판은 그대로", store.Load(m.Doc.Id)!.Rev, revBefore);
        Check("켜도 본문은 그대로", Body(m.Doc.Id), "절차\n[x] 하나\n  - [X] 둘\n글 [x]");
        Check("목록에 매일", store.Memos.First(x => x.Id == m.Doc.Id).Daily, true);
        Check("없는 메모는 켜지 못함", store.SetDaily(Guid.NewGuid(), true, Mon), false);
        Check("기다리는 중엔 안 켬", waitingStore.SetDaily(empty.Doc.Id, true, Mon), false);
        Check("기다리는 중엔 머리말 그대로", Header(empty.Doc.Id).Contains("daily:"), false);
        store.SetDaily(empty.Doc.Id, true, Mon);
        store.SetDaily(trashed.Doc.Id, true, Mon);
        store.Delete(trashed.Doc.Id);
        Check("켠 날은 안 풀림", store.ResetDaily(Mon).Written, 0);
        Check("켠 날 본문 그대로", Body(m.Doc.Id), "절차\n[x] 하나\n  - [X] 둘\n글 [x]");

        // 다음 날: 줄 머리 체크만 풀고 reset 을 적는다. 풀 것이 없어도 날짜는 적는다.
        Check("다음 날 풀림 (체크 있는 것·없는 것)", store.ResetDaily(Tue).Written, 2);
        Check("줄 머리만 풂", Body(m.Doc.Id), "절차\n[ ] 하나\n  - [ ] 둘\n글 [x]");
        Check("reset 기록", Header(m.Doc.Id).Contains(ResetLine(Tue)), true);
        Check("daily 는 남음", Header(m.Doc.Id).Contains("daily: true"), true);
        Check("풀 것이 없어도 reset 기록", (Header(empty.Doc.Id).Contains(ResetLine(Tue)), Body(empty.Doc.Id)), (true, "빈 절차\n[ ] 하나\n글"));
        Check("푼 메모는 판이 바뀜", store.Load(m.Doc.Id)!.Rev == revBefore, false);
        Check("같은 날 두 번째는 0", store.ResetDaily(Tue).Written, 0);
        Check("daily 아니면 그대로", (Body(plain.Doc.Id), Header(plain.Doc.Id).Contains("reset:")), ("평범한 메모\n[x] 그대로", false));
        Check("daily 아닌 다른 메모도 그대로", Body(other.Doc.Id).Contains("[x]"), true);
        var trashFile = store.Trash().First(x => x.Id == trashed.Doc.Id).Path;
        var trashText = File.ReadAllText(trashFile);
        Check("휴지통은 안 건드림", (trashText.Contains("[x] 하나"), trashText.Contains(ResetLine(Tue))), (true, false));
        Check("ResetDaily 뒤 Scan 은 바뀜 없음", store.Scan(), false);

        // 열린 메모처럼 건너뛰기
        var current = store.Load(m.Doc.Id)!;
        store.Save(m.Doc.Id, current.Doc.Body.Replace("[ ] 하나", "[x] 하나"), current.Rev);
        Check("다시 체크함", Body(m.Doc.Id).Contains("[x] 하나"), true);
        var skipped = store.ResetDaily(Wed, id => id == m.Doc.Id);
        Check("skip 은 건너뜀 (나머지는 풂)", skipped.Written, 1);
        Check("skip 한 메모는 Pending", (string.Join(",", skipped.Pending), skipped.Complete), (m.Doc.Id.ToString(), false));
        Check("건너뛴 메모는 체크·reset 그대로", (Body(m.Doc.Id).Contains("[x] 하나"), Header(m.Doc.Id).Contains(ResetLine(Tue))), (true, true));
        Check("skip 한 메모는 다음에 풂", store.ResetDaily(Wed).Written, 1);
        Check("그때 풀림", (Body(m.Doc.Id).Contains("[x] 하나"), Header(m.Doc.Id).Contains(ResetLine(Wed))), (false, true));
        Check("skip 이 거짓이면 풂", store.ResetDaily(Wed, _ => false).Written, 0);

        // 못 쓰면 아무것도
        Check("못 쓰면 아무것도", waitingStore.ResetDaily(Thu).Written, 0);
        Check("못 쓰면 끝나지 않은 것으로", (waitingStore.ResetDaily(Thu).IsBlocked, waitingStore.ResetDaily(Thu).Complete), (true, false));
        Check("못 쓰면 reset 도 그대로", (Header(m.Doc.Id).Contains(ResetLine(Wed)), Header(empty.Doc.Id).Contains(ResetLine(Wed))), (true, true));
        Check("기다리는 중엔 모두 풀기도 안 함", waitingStore.UncheckAll(other.Doc.Id), false);
        Check("ResetDaily 뒤에도 Scan 은 바뀜 없음", store.Scan(), false);

        // 읽지 못하는 메모는 건너뛰고 다음에 푼다
        current = store.Load(m.Doc.Id)!;
        store.Save(m.Doc.Id, current.Doc.Body.Replace("[ ] 하나", "[x] 하나"), current.Rev);
        using (new FileStream(store.Load(m.Doc.Id)!.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedResult = store.ResetDaily(Thu);
            Check("잠긴 메모는 건너뜀", lockedResult.Written, 1);
            Check("잠긴 메모는 Pending", (string.Join(",", lockedResult.Pending), lockedResult.Complete), (m.Doc.Id.ToString(), false));
        }
        var unlocked = store.ResetDaily(Thu);
        Check("풀리면 다음에 풂", unlocked.Written, 1);
        Check("다 풀면 끝", (unlocked.Pending.Count, unlocked.Complete), (0, true));
        Check("그때 풀림 (잠겼던 메모)", (Body(m.Doc.Id).Contains("[x] 하나"), Header(m.Doc.Id).Contains(ResetLine(Thu))), (false, true));

        // 모두 풀기: 날짜와 상관없이 지금, reset 은 그대로
        var otherRev = store.Load(other.Doc.Id)!.Rev;
        Check("모두 풀기", store.UncheckAll(other.Doc.Id) && !Body(other.Doc.Id).Contains("[x]") && !Body(other.Doc.Id).Contains("[X]"), true);
        Check("모두 풀기 결과", Body(other.Doc.Id), "다른 절차\n[ ] 하나\n- [ ] 둘");
        Check("모두 풀기는 판을 바꿈", store.Load(other.Doc.Id)!.Rev == otherRev, false);
        Check("모두 풀기는 reset 을 안 적음", Header(other.Doc.Id).Contains("reset:"), false);
        Check("풀 것이 없으면 false", store.UncheckAll(other.Doc.Id), false);
        Check("없는 메모는 false", store.UncheckAll(Guid.NewGuid()), false);
        Check("모두 풀기 뒤 Scan 은 바뀜 없음", store.Scan(), false);

        // 끄기: reset 은 둔다. 더는 풀지 않는다.
        var offRev = store.Load(m.Doc.Id)!.Rev;
        Check("끄기", store.SetDaily(m.Doc.Id, false, Thu), true);
        Check("끄면 daily 줄 없음·reset 남음", (Header(m.Doc.Id).Contains("daily:"), Header(m.Doc.Id).Contains(ResetLine(Thu))), (false, true));
        Check("끄기도 판은 그대로", store.Load(m.Doc.Id)!.Rev, offRev);
        Check("목록에서 매일 아님", store.Memos.First(x => x.Id == m.Doc.Id).Daily, false);
        Check("끈 메모는 더 안 풂", store.ResetDaily(Thu.AddDays(5)).Written, 1);   // 빈 절차 하나만
        Check("이미 꺼진 것을 또 꺼도 참", store.SetDaily(m.Doc.Id, false, Thu), true);

        MemoDailyRetryTests();
    }

    /// <summary>같은 날 다시 해 보기 · 풀 것이 없을 때 시각 지키기 · 롤오버에서 열린 메모 저장 여부.</summary>
    private static void MemoDailyRetryTests()
    {
        var root = Path.Combine(DataStore.Directory, "memotest-daily-retry");
        if (Directory.Exists(root)) Directory.Delete(root, true);

        var clock = new DateTime(2026, 10, 5, 9, 0, 0);
        var store = new MemoStore(root, () => true, () => clock);
        string Body(Guid memo) => store.Load(memo)!.Doc.Body.Replace("\r\n", "\n");
        bool ResetOn(Guid memo, DateOnly day) => File.ReadAllText(store.Load(memo)!.Path).Contains("reset: " + day.ToString("yyyy-MM-dd"));

        var locked = store.Create("잠길 절차\n[x] 하나")!;
        var done = store.Create("끝낼 절차\n[x] 하나")!;
        store.SetDaily(locked.Doc.Id, true, Mon);
        store.SetDaily(done.Doc.Id, true, Mon);

        // 잠긴 매일 메모: 이번엔 Pending, 같은 날 다시 하면 그것만 푼다
        DailyResetResult first;
        using (new FileStream(store.Load(locked.Doc.Id)!.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            first = store.ResetDaily(Tue);
        }
        Check("잠긴 매일 메모는 Pending", (first.Written, string.Join(",", first.Pending), first.Complete), (1, locked.Doc.Id.ToString(), false));
        Check("잠긴 메모는 아직 체크", (Body(locked.Doc.Id), ResetOn(locked.Doc.Id, Tue)), ("잠길 절차\n[x] 하나", false));
        Check("다른 메모는 풀림", Body(done.Doc.Id), "끝낼 절차\n[ ] 하나");

        // 오늘 푼 메모에 오늘 다시 체크한다
        store.Save(done.Doc.Id, "끝낼 절차\n[x] 하나", store.Load(done.Doc.Id)!.Rev);
        var retry = store.ResetDaily(Tue);
        Check("같은 날 다시: 남은 것만 풂", (retry.Written, retry.Pending.Count, retry.Complete), (1, 0, true));
        Check("잠겼던 메모 풀림", (Body(locked.Doc.Id), ResetOn(locked.Doc.Id, Tue)), ("잠길 절차\n[ ] 하나", true));
        Check("오늘 푼 메모는 다시 안 풂", Body(done.Doc.Id), "끝낼 절차\n[x] 하나");
        Check("한 번 더 해도 아무것도", store.ResetDaily(Tue).Written, 0);
        var restarted = new MemoStore(root, () => true, () => clock);
        var again = restarted.ResetDaily(Tue);
        Check("같은 날 다시 켜도 오늘 체크는 그대로", (again.Written, again.Complete, Body(done.Doc.Id)), (0, true, "끝낼 절차\n[x] 하나"));

        // 풀 체크가 없어 reset 만 바뀌면 고친 시각을 지킨다(매일 아침 '최근' 맨 위로 뛰지 않게)
        var quiet = store.Create("조용한 절차\n[ ] 하나")!;
        store.SetDaily(quiet.Doc.Id, true, Tue);
        var old = new DateTime(2026, 10, 1, 8, 0, 0);
        File.SetLastWriteTime(store.Load(quiet.Doc.Id)!.Path, old);
        File.SetLastWriteTime(store.Load(done.Doc.Id)!.Path, old);
        store.Scan();
        Check("시각을 옛날로", store.Memos.First(x => x.Id == quiet.Doc.Id).Updated, old);
        var wed = store.ResetDaily(Wed);
        Check("다음 날 셋 다 씀", (wed.Written, wed.Complete), (3, true));
        Check("reset 은 적힘", (ResetOn(quiet.Doc.Id, Wed), Body(quiet.Doc.Id)), (true, "조용한 절차\n[ ] 하나"));
        // 딱 그대로가 아니라 2초 뒤 — 같은 길이로 reset 만 바뀐 것을 다른 PC 훑기가 알아채게
        Check("reset 만 바뀌면 Updated 는 2초 뒤", store.Memos.First(x => x.Id == quiet.Doc.Id).Updated, old.AddSeconds(2));
        Check("파일 시각도 2초 뒤", File.GetLastWriteTime(store.Load(quiet.Doc.Id)!.Path), old.AddSeconds(2));
        var otherPc = new MemoStore(root, () => true, () => clock);
        otherPc.Scan();
        var quietPath = store.Load(quiet.Doc.Id)!.Path;
        var quietText = File.ReadAllText(quietPath);
        string ResetLineOf(DateOnly day) => "reset: " + day.ToString("yyyy-MM-dd");
        File.WriteAllText(quietPath, quietText.Replace(ResetLineOf(Wed), ResetLineOf(Thu)));
        File.SetLastWriteTime(quietPath, old.AddSeconds(4));   // 다음 날 다른 PC 가 푼 모양: 같은 길이, 2초 뒤
        Check("같은 길이로 reset 만 바뀐 것을 다른 PC 가 알아챔", otherPc.Scan(), true);
        File.WriteAllText(quietPath, quietText);               // 되돌려 둔다(아래 시험은 Wed 상태에서)
        File.SetLastWriteTime(quietPath, old.AddSeconds(2));
        Check("체크를 푼 메모는 시각이 바뀜", store.Memos.First(x => x.Id == done.Doc.Id).Updated == old, false);
        Check("시각을 되돌린 뒤 Scan 은 바뀜 없음", store.Scan(), false);

        // 롤오버: 열린 메모가 오늘 풀 매일 메모일 때만 먼저 저장하게 한다
        var flushed = 0;
        var plain = store.Create("상관없는 메모\n[x] 그대로")!;
        var thu = MemoLinks.ResetAtRollover(store, Thu, () => flushed++, () => plain.Doc.Id, null);
        Check("상관없는 메모가 열려 있으면 저장 안 시킴", (flushed, thu.Written), (0, 3));
        var fri = Thu.AddDays(1);
        MemoLinks.ResetAtRollover(store, fri, () => flushed++, () => null, null);
        Check("열린 메모가 없으면 저장 안 시킴", flushed, 0);
        var sat = fri.AddDays(1);
        store.Save(done.Doc.Id, "끝낼 절차\n[x] 하나", store.Load(done.Doc.Id)!.Rev);
        var held = MemoLinks.ResetAtRollover(store, sat, () => flushed++, () => done.Doc.Id, id => id == done.Doc.Id);
        Check("풀 차례인 메모가 열려 있으면 저장 시킴", flushed, 1);
        Check("쓰는 중이면 건너뛰고 Pending", (held.Written, string.Join(",", held.Pending), Body(done.Doc.Id)), (2, done.Doc.Id.ToString(), "끝낼 절차\n[x] 하나"));
        MemoLinks.ResetAtRollover(store, sat, () => flushed++, () => quiet.Doc.Id, id => id == done.Doc.Id);
        Check("이미 오늘 푼 메모가 열려 있으면 저장 안 시킴", flushed, 1);
        var heldRetry = store.ResetDaily(sat, _ => false);
        Check("쓰기가 끝나면 다시 해서 풂", (heldRetry.Written, heldRetry.Complete, Body(done.Doc.Id)), (1, true, "끝낼 절차\n[ ] 하나"));
        Check("쓰는 중이 아닌 메모는 skip 이 참이어도 안 셈", store.ResetDaily(sat, _ => true).Complete, true);

        // 백업 되살리기: 어제 체크가 든 파일이 돌아와도 오늘 끝낸 것으로 기억하면 안 푼다 → 잊으면 다시 푼다
        var restored = store.Load(done.Doc.Id)!;
        var restoredText = File.ReadAllText(restored.Path)
            .Replace("reset: " + sat.ToString("yyyy-MM-dd"), "reset: " + fri.ToString("yyyy-MM-dd"))
            .Replace("[ ] 하나", "[x] 하나");
        File.WriteAllText(restored.Path, restoredText);
        store.Scan();
        Check("되살린 뒤 잊기 전엔 안 풂", store.ResetDaily(sat).Written, 0);
        store.ForgetDailyProgress();
        var afterRestore = store.ResetDaily(sat);
        Check("잊으면 되살린 메모를 다시 풂", (afterRestore.Written, afterRestore.Complete, Body(done.Doc.Id), ResetOn(done.Doc.Id, sat)),
            (1, true, "끝낼 절차\n[ ] 하나", true));

        MemoDriveGoneTests();
    }

    /// <summary>날이 바뀔 때 클라우드 드라이브가 잠깐 빠져 데이터 폴더째 안 보이면 지운 것으로 보지 않는다.</summary>
    private static void MemoDriveGoneTests()
    {
        var root = Path.Combine(DataStore.Directory, "memotest-drive-gone");
        var away = root + "-away";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (Directory.Exists(away)) Directory.Delete(away, true);

        var clock = new DateTime(2026, 10, 5, 9, 0, 0);
        var store = new MemoStore(root, () => true, () => clock);
        var daily = store.Create("드라이브 절차\n[x] 하나")!;
        store.Create("평범한 메모");
        store.SetDaily(daily.Doc.Id, true, Mon);
        store.Scan();

        Directory.Move(root, away);   // 드라이브가 빠짐
        Check("데이터 폴더가 없으면 훑어도 바뀜 없음", store.Scan(), false);
        Check("목록은 그대로", store.Memos.Count, 2);
        var gone = store.ResetDaily(Tue);
        Check("데이터 폴더가 없으면 초기화는 Blocked", (gone.IsBlocked, gone.Complete), (true, false));
        Check("없을 때 읽기는 null 이지만 목록은 그대로", (store.Load(daily.Doc.Id) is null, store.Memos.Count), (true, 2));

        Directory.Move(away, root);   // 돌아옴
        var back = store.ResetDaily(Tue);
        Check("돌아오면 풂", (back.Written, back.Complete), (1, true));
        Check("그때 체크 풀림", store.Load(daily.Doc.Id)!.Doc.Body.Replace("\r\n", "\n"), "드라이브 절차\n[ ] 하나");

        // memos 폴더만 없고 데이터 폴더는 있으면 정말 다 지운 것이다
        Directory.Delete(store.Folder, true);
        Check("memos 폴더만 지우면 목록 비움", (store.Scan(), store.Memos.Count), (true, 0));
        Directory.Delete(root, true);
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
        Check("모든 태그 (같은 수면 폰처럼 한글 먼저)", string.Join(",", MemoQuery.AllTags(list)), "정산,AK넷");
        Check("이름 순서는 문화권과 상관없이 한글 먼저 · 대소문자 무시", string.Join(",", new[] { "beta", "정산", "Alpha", "가나" }.OrderBy(x => x, MemoQuery.NameOrder)), "가나,정산,Alpha,beta");

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

        // 메모를 바꾸거나 지우기 앞의 저장이 충돌이면 띠가 남고 사본이 목록에 있다
        var sw = new MemoEditorState(store);
        var ga = store.Create("갑 메모\n하나")!;
        var eul = store.Create("을 메모")!;
        sw.Open(ga.Doc.Id);
        sw.IsEditing = true; sw.Edit("갑 메모\n하나\n내 글");
        File.WriteAllText(ga.Path, File.ReadAllText(ga.Path).Replace("하나", "하나(다른 PC)"));
        var pre = sw.Open(eul.Doc.Id);
        Check("바꿀 때 저장이 충돌", pre?.Outcome, (SaveOutcome?)SaveOutcome.Conflict);
        Check("충돌 띠가 새 메모 위에 남음", (sw.CurrentId, sw.Banner), ((Guid?)eul.Doc.Id, MemoEditorState.ConflictBanner));
        Check("충돌 사본이 목록에 보임", store.Memos.Any(x => x.Title.StartsWith("갑 메모 (충돌 사본")), true);

        sw.Edit("을 메모\n내 글");
        File.WriteAllText(eul.Path, File.ReadAllText(eul.Path).Replace("을 메모", "을 메모 v2"));
        Check("지우기 앞 저장이 충돌이어도 지움", sw.Delete(), true);
        Check("지운 뒤에도 충돌 띠", (sw.CurrentId, sw.Banner), ((Guid?)null, MemoEditorState.ConflictBanner));
        Check("을 사본이 목록에 보임", store.Memos.Any(x => x.Title.StartsWith("을 메모 (충돌 사본")), true);
        sw.Open(ga.Doc.Id);
        Check("지운 뒤 연 메모에도 띠가 남음", sw.Banner, MemoEditorState.ConflictBanner);
        sw.Open(other.Doc.Id);
        Check("그다음 열기에서는 띠가 사라짐", sw.Banner, (string?)null);

        // 잠긴 파일은 지워졌다고 하지 않는다
        var locked = store.Create("잠긴 메모")!;
        using (new FileStream(locked.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lk = new MemoEditorState(store);
            lk.Open(locked.Doc.Id);
            Check("잠긴 메모를 열면 읽지 못함 띠", lk.Banner, MemoEditorState.UnreadableBanner);
        }

        // 닫기를 거절한 안내는 다시 시도가 덮지 않는다
        var cl = new MemoEditorState(new MemoStore(root, () => false, () => DateTime.Now));
        cl.Open(other.Doc.Id); cl.Edit("못 쓰는 글"); cl.Flush();
        cl.RefuseClose();
        cl.Flush();
        Check("닫기 거절 띠는 남고 글도 남음", (cl.IsDirty, cl.Banner), (true, MemoEditorState.CloseRefusedBanner));

        // 태그 칸: 머리말 tags 만 바꾸고, 뒤이은 본문 저장이 그것을 지킨다
        var tg = new MemoEditorState(store);
        var tagged = store.Create("태그 메모\n본문 #본문태그")!;
        tg.Open(tagged.Doc.Id);
        Check("연 메모의 태그는 비어 있음", tg.Tags.Count, 0);
        Check("태그 붙이기", tg.SetTags(new[] { "#정산", "AK넷", "정산" }), true);
        Check("태그 정리돼 들어감", string.Join(",", tg.Tags), "정산,AK넷");
        tg.Edit("태그 메모\n본문 고침 #본문태그");
        Check("태그 뒤 본문 저장은 그냥 저장", tg.Flush()?.Outcome, (SaveOutcome?)SaveOutcome.Saved);
        Check("본문 저장이 머리말 태그를 지킴", string.Join(",", store.Load(tagged.Doc.Id)!.Doc.Tags!), "정산,AK넷");
        var tg2 = new MemoEditorState(store);
        tg2.Open(tagged.Doc.Id);
        Check("다시 열면 태그가 보임", string.Join(",", tg2.Tags), "정산,AK넷");
        tg2.SetTags(new[] { "AK넷" });
        Check("태그 빼기", string.Join(",", store.Load(tagged.Doc.Id)!.Doc.Tags!), "AK넷");
        Check("목록 요약에도 머리말+본문 태그", string.Join(",", store.Memos.First(m => m.Id == tagged.Doc.Id).Tags), "AK넷,본문태그");
    }

    /// <summary>메모 창의 ↻ · 모두 풀기. 쓰던 글을 먼저 저장하고, 못 하면 아무것도 하지 않는다.</summary>
    private static void MemoEditorDailyTests()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 메모 창 매일 초기화 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = Path.Combine(DataStore.Directory, "memotest-editordaily");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var store = new MemoStore(root, () => true, () => new DateTime(2026, 10, 5, 9, 0, 0));
        string Body(Guid id) => store.Load(id)!.Doc.Body.Replace("\r\n", "\n");
        string Header(Guid id)
        {
            var text = File.ReadAllText(store.Load(id)!.Path);
            return text[..text.IndexOf("\r\n---\r\n", 3, StringComparison.Ordinal)];
        }

        // 쓰는 중 '모두 풀기': 먼저 저장하고 푼다
        var memo = store.Create("절차\n[x] 하나\n  - [X] 둘\n[ ] 셋")!;
        var editor = new MemoEditorState(store);
        editor.Open(memo.Doc.Id);
        Check("체크된 칸이 있음", editor.HasChecked, true);
        editor.IsEditing = true;
        editor.Edit("절차\n[x] 하나\n  - [X] 둘\n[ ] 셋\n쓰던 줄");
        Check("쓰는 중 모두 풀기", editor.UncheckAll(), true);
        Check("쓰던 글은 저장되고 체크만 풀림", Body(memo.Doc.Id), "절차\n[ ] 하나\n  - [ ] 둘\n[ ] 셋\n쓰던 줄");
        Check("창 본문도 풀린 글", editor.Body, "절차\n[ ] 하나\n  - [ ] 둘\n[ ] 셋\n쓰던 줄");
        Check("본문에 [x] 없음", editor.Body.Contains("[x]") || editor.Body.Contains("[X]"), false);
        Check("모두 푼 뒤 dirty 아님", editor.IsDirty, false);
        Check("모두 푼 뒤 체크된 칸 없음", editor.HasChecked, false);
        editor.Edit(editor.Body + "\n이어서");
        Check("푼 뒤 저장은 충돌 없음", editor.Flush()?.Outcome, (SaveOutcome?)SaveOutcome.Saved);
        Check("풀 것이 없으면 거짓", editor.UncheckAll(), false);
        editor.ToggleCheck(1);
        Check("체크하면 다시 HasChecked", editor.HasChecked, true);
        editor.Edit("절차\n[ ] 하나\n  - [ ] 둘\n[ ] 셋\n쓰던 줄\n이어서");
        Check("쓰던 글로 체크를 지우면 HasChecked 거짓", editor.HasChecked, false);
        editor.Flush();

        // 저장이 안 되는 동안(폴더를 기다림)은 풀지 않고 띠도 그대로
        var blockedMemo = store.Create("막힌 절차\n[x] 하나")!;
        var blocked = new MemoEditorState(new MemoStore(root, () => false, () => DateTime.Now));
        blocked.Open(blockedMemo.Doc.Id);
        blocked.Edit("막힌 절차\n[x] 하나\n못 쓴 줄");
        blocked.Flush();
        Check("저장 실패 띠", blocked.Banner, MemoEditorState.FailedBanner);
        Check("저장 못 하면 풀지 않음", blocked.UncheckAll(), false);
        Check("풀지 않은 채 쓰던 글 그대로", (blocked.Body, blocked.IsDirty), ("막힌 절차\n[x] 하나\n못 쓴 줄", true));
        Check("띠도 그대로", blocked.Banner, MemoEditorState.FailedBanner);
        Check("파일도 그대로", Body(blockedMemo.Doc.Id), "막힌 절차\n[x] 하나");
        Check("저장 못 하면 매일 초기화도 안 켬", blocked.SetDaily(true, Tue), false);
        Check("머리말도 그대로", Header(blockedMemo.Doc.Id).Contains("daily: true"), false);

        // ↻ 켜기: 머리말 daily·reset 오늘, 본문은 그대로(켠 날 바로 풀리지 않음)
        var daily = store.Create("매일 절차\n[x] 하나")!;
        var de = new MemoEditorState(store);
        de.Open(daily.Doc.Id);
        Check("처음엔 꺼짐", de.IsDaily, false);
        de.IsEditing = true;
        de.Edit("매일 절차\n[x] 하나\n쓰던 줄");
        Check("매일 초기화 켜기", de.SetDaily(true, Tue), true);
        Check("켜짐", (de.IsDaily, de.IsDirty), (true, false));
        Check("머리말 daily: true", Header(daily.Doc.Id).Contains("daily: true"), true);
        Check("머리말 reset: 오늘", Header(daily.Doc.Id).Contains("reset: 2026-10-06"), true);
        Check("본문 그대로(쓰던 글은 저장됨)", Body(daily.Doc.Id), "매일 절차\n[x] 하나\n쓰던 줄");
        Check("목록 요약도 매일", store.Memos.First(m => m.Id == daily.Doc.Id).Daily, true);
        de.Edit("매일 절차\n[x] 하나\n쓰던 줄\n더");
        Check("켠 뒤 저장도 충돌 없음", de.Flush()?.Outcome, (SaveOutcome?)SaveOutcome.Saved);
        Check("저장이 머리말을 지킴", Header(daily.Doc.Id).Contains("daily: true"), true);
        Check("같은 날 롤오버는 안 풂", store.ResetDaily(Tue).Written, 0);
        Check("매일 초기화 끄기", de.SetDaily(false, Wed), true);
        Check("꺼짐", (de.IsDaily, Header(daily.Doc.Id).Contains("daily: true")), (false, false));
        var reopened = new MemoEditorState(store);
        reopened.Open(daily.Doc.Id);
        Check("다시 열어도 꺼짐", reopened.IsDaily, false);
    }

    // ───────────────────────── 메모 · 줄 고치기 (서식 단추 · Enter)

    /// <summary>표시 글을 (글, 선택 시작, 선택 끝)으로. | 는 커서, { } 는 선택.</summary>
    private static (string Text, int Start, int End) Marked(string marked)
    {
        var caret = marked.IndexOf('|');
        if (caret >= 0) return (marked.Remove(caret, 1), caret, caret);
        var open = marked.IndexOf('{');
        var text = marked.Remove(open, 1);
        var close = text.IndexOf('}');
        return (text.Remove(close, 1), open, close);
    }

    private static string Shown(MemoEdit edit)
    {
        if (edit.SelectionStart == edit.SelectionEnd) return edit.Text.Insert(edit.Caret, "|");
        var (a, b) = (Math.Min(edit.SelectionStart, edit.SelectionEnd), Math.Max(edit.SelectionStart, edit.SelectionEnd));
        return edit.Text.Insert(b, "}").Insert(a, "{");
    }

    private static string Tog(string marked, MemoLineKind kind)
    {
        var (text, s, e) = Marked(marked);
        return Shown(MemoEditing.Toggle(text, s, e, kind));
    }

    private static string? Enter(string marked)
    {
        var (text, s, _) = Marked(marked);
        return MemoEditing.ContinueList(text, s) is { } edit ? Shown(edit) : null;
    }

    private static void MemoEditingTests()
    {
        // 켜기 · 끄기 · 바꾸기
        Check("점 켜기", Tog("제목\n사과|", MemoLineKind.Bullet), "제목\n- 사과|");
        Check("점 끄기", Tog("제목\n- 사과|", MemoLineKind.Bullet), "제목\n사과|");
        Check("점 → 체크", Tog("- 사|과", MemoLineKind.Check), "[ ] 사|과");
        Check("체크(x) → 번호", Tog("[x] 끝낸 일|", MemoLineKind.Numbered), "1. 끝낸 일|");
        Check("- [ ] 끄기", Tog("- [ ] 할 일|", MemoLineKind.Check), "할 일|");
        Check("큰 소제목 켜기", Tog("준비|", MemoLineKind.Heading1), "# 준비|");
        Check("큰 소제목 → 소제목", Tog("# 준비|", MemoLineKind.Heading2), "## 준비|");
        Check("소제목 끄기", Tog("## 준비|", MemoLineKind.Heading2), "준비|");
        Check("소제목은 들여쓰기를 뗌", Tog("  - 항목|", MemoLineKind.Heading1), "# 항목|");
        Check("목록끼리는 들여쓰기 유지", Tog("  - 항목|", MemoLineKind.Check), "  [ ] 항목|");
        Check("들여쓴 글에 점", Tog("  항목|", MemoLineKind.Bullet), "  - 항목|");
        Check("줄 맨 앞 커서는 기호 뒤로", Tog("|사과", MemoLineKind.Bullet), "- |사과");
        Check("기호 안 커서는 새 기호 뒤로", Tog("1|. 사과", MemoLineKind.Bullet), "- |사과");
        Check("빈 줄에 체크", Tog("제목\n|", MemoLineKind.Check), "제목\n[ ] |");
        Check("다른 줄은 그대로", Tog("가\n나|\n다", MemoLineKind.Bullet), "가\n- 나|\n다");
        Check("#태그 줄은 소제목이 아님", Tog("#태그|", MemoLineKind.Heading1), "# #태그|");

        // 번호
        Check("번호: 위 번호 다음", Tog("1. 가\n2. 나\n다|", MemoLineKind.Numbered), "1. 가\n2. 나\n3. 다|");
        Check("번호: 위가 목록이 아니면 1", Tog("글\n다|", MemoLineKind.Numbered), "글\n1. 다|");
        Check("번호: 빈 줄 너머는 이어지지 않음", Tog("1. 가\n\n다|", MemoLineKind.Numbered), "1. 가\n\n1. 다|");
        Check("번호: 사이의 체크 줄은 건너뜀", Tog("3. 가\n[ ] 나\n다|", MemoLineKind.Numbered), "3. 가\n[ ] 나\n4. 다|");
        Check("번호: 들여쓰기가 같아야", Tog("1. 가\n  다|", MemoLineKind.Numbered), "1. 가\n  1. 다|");

        // 여러 줄
        Check("여러 줄 점", Tog("{가\n나\n다}", MemoLineKind.Bullet), "{- 가\n- 나\n- 다}");
        Check("여러 줄 모두 점이면 끔", Tog("{- 가\n- 나}", MemoLineKind.Bullet), "{가\n나}");
        Check("섞여 있으면 모두 켬", Tog("{- 가\n나}", MemoLineKind.Bullet), "{- 가\n- 나}");
        Check("여러 줄 번호는 차례로", Tog("{가\n나\n다}", MemoLineKind.Numbered), "{1. 가\n2. 나\n3. 다}");
        Check("여러 줄의 빈 줄은 건너뜀", Tog("{가\n\n나}", MemoLineKind.Check), "{[ ] 가\n\n[ ] 나}");
        Check("다음 줄 맨 앞까지 고른 것은 그 줄 제외", Tog("{가\n}나", MemoLineKind.Bullet), "{- 가\n}나");
        Check("줄 가운데부터 고름", Tog("가{나\n다}라", MemoLineKind.Bullet), "- 가{나\n- 다}라");

        // CRLF
        Check("CRLF 줄 켜기", Tog("제목\r\n가|\r\n나", MemoLineKind.Check), "제목\r\n[ ] 가|\r\n나");
        Check("CRLF 여러 줄", Tog("{가\r\n나}", MemoLineKind.Numbered), "{1. 가\r\n2. 나}");

        // 읽기 모드: 끝에 붙이기
        Check("끝에 체크 줄", Shown(MemoEditing.Append("제목\n본문", MemoLineKind.Check)), "제목\n본문\n[ ] |");
        Check("끝이 줄바꿈이면 그대로", Shown(MemoEditing.Append("제목\n", MemoLineKind.Bullet)), "제목\n- |");
        Check("끝에 번호 이어서", Shown(MemoEditing.Append("1. 가\n2. 나", MemoLineKind.Numbered)), "1. 가\n2. 나\n3. |");
        Check("끝에 큰 소제목", Shown(MemoEditing.Append("제목", MemoLineKind.Heading1)), "제목\n# |");
        Check("CRLF 글 끝에 붙이기", Shown(MemoEditing.Append("제목\r\n본문", MemoLineKind.Heading2)), "제목\r\n본문\r\n## |");
        Check("빈 글에 붙이기", Shown(MemoEditing.Append("", MemoLineKind.Check)), "[ ] |");

        // Enter 이어 쓰기
        Check("체크 다음 줄", Enter("[x] 사과|"), "[x] 사과\n[ ] |");
        Check("- [ ] 꼴 유지", Enter("- [ ] 사과|"), "- [ ] 사과\n- [ ] |");
        Check("번호 다음 번호", Enter("1. 가\n2. 나|"), "1. 가\n2. 나\n3. |");
        Check("점 같은 점", Enter("* 가|"), "* 가\n* |");
        Check("들여쓰기 유지", Enter("\t- 가|"), "\t- 가\n\t- |");
        Check("들여쓴 번호", Enter("  9. 가|"), "  9. 가\n  10. |");
        Check("가운데서 나누기", Enter("- 사|과"), "- 사\n- |과");
        Check("빈 항목 Enter 는 목록 끝", Enter("- 가\n- |"), "- 가\n|");
        Check("빈 체크 항목도 끝", Enter("[ ] 가\n  [ ] |"), "[ ] 가\n|");
        Check("빈 번호 항목도 끝(뒷줄 유지)", Enter("1. 가\n2. |\n다음"), "1. 가\n|\n다음");
        Check("글 줄은 보통 Enter", Enter("그냥 글|"), (string?)null);
        Check("소제목 줄은 보통 Enter", Enter("# 제목|"), (string?)null);
        Check("기호 앞 커서는 보통 Enter", Enter("|- 가"), (string?)null);
        Check("CRLF 이어 쓰기", Enter("제목\r\n- 가|\r\n끝"), "제목\r\n- 가\r\n- |\r\n끝");
        Check("CRLF 빈 항목", Enter("- 가\r\n- |\r\n끝"), "- 가\r\n|\r\n끝");
    }

    /// <summary>할 일·루틴과 메모의 연결, 롤오버 때의 매일 초기화 순서.</summary>
    private static void MemoLinkTests()
    {
        var a = Guid.Parse("3f2a9c1e-0000-4000-8000-0000000000a1");
        var b = Guid.Parse("3f2a9c1e-0000-4000-8000-0000000000b2");
        var c = Guid.Parse("3f2a9c1e-0000-4000-8000-0000000000c3");
        MemoSummary Summary(Guid id, string title, bool pinned, int day)
            => new(id, "", title, "", Array.Empty<string>(), pinned, false, new DateTime(2026, 10, day), "", false);

        // 연결 메뉴: 메모 탭과 같은 순서, 기호를 뗀 제목
        var choices = MemoLinks.Choices([Summary(a, "# 오래된 절차", false, 1), Summary(b, "[ ] 최근 메모", false, 9), Summary(c, "고정 메모", true, 2)]);
        Check("연결 메뉴 순서 (고정 먼저, 그다음 최근)", string.Join(",", choices.Select(x => x.Id == a ? "a" : x.Id == b ? "b" : "c")), "c,b,a");
        Check("연결 메뉴 제목은 기호를 뗌", string.Join(",", choices.Select(x => x.Title)), "고정 메모,최근 메모,오래된 절차");
        Check("메모가 없으면 빈 메뉴", MemoLinks.Choices([]).Count, 0);
        var unreadable = Summary(b, "잠긴 메모", false, 9) with { Unreadable = true };
        Check("읽지 못한 메모는 메뉴에서 뺌", string.Join(",", MemoLinks.Choices([Summary(a, "보통", false, 1), unreadable]).Select(x => x.Title)), "보통");
        Check("빈 제목은 제목 없음", MemoLinks.Choices([Summary(a, "", false, 1), Summary(b, "  ", false, 2)]).Select(x => x.Title).Distinct().Single(), "제목 없음");

        // 📝: 연결이 있고 그 메모가 목록에 있을 때만
        var live = new HashSet<Guid> { a, b };
        Check("연결 없음 → 📝 없음", MemoLinks.IsAlive(null, live), false);
        Check("목록에 있음 → 📝", MemoLinks.IsAlive(a, live), true);
        Check("없거나 휴지통 → 📝 없음", MemoLinks.IsAlive(c, live), false);

        // 연결된 항목: 루틴 먼저, 각각 목록 순서. 여러 항목이 같은 메모를 가리킬 수 있다.
        var data = new AppData();
        data.Routines.Add(new Routine { Title = "AK넷 배포", Order = 1, MemoId = a });
        data.Routines.Add(new Routine { Title = "물 마시기", Order = 0 });
        data.Tasks.Add(new TaskItem { Title = "정산", Order = 2, MemoId = a });
        data.Tasks.Add(new TaskItem { Title = "보고", Order = 1, MemoId = a });
        data.Tasks.Add(new TaskItem { Title = "다른 일", Order = 0, MemoId = b });
        Check("연결된 항목", string.Join(", ", MemoLinks.LinkedItems(data, a)), "루틴 · AK넷 배포, 할 일 · 보고, 할 일 · 정산");
        Check("다른 메모의 항목", string.Join(", ", MemoLinks.LinkedItems(data, b)), "할 일 · 다른 일");
        Check("아무도 안 가리키면 빈 목록", MemoLinks.LinkedItems(data, c).Count, 0);
        data.Tasks[1].MemoId = null;   // '보고' 하나만 연결 끊기
        Check("하나를 끊어도 나머지는 그대로", string.Join(", ", MemoLinks.LinkedItems(data, a)), "루틴 · AK넷 배포, 할 일 · 정산");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 롤오버 매일 초기화 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = Path.Combine(DataStore.Directory, "memotest-rollover");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var store = new MemoStore(root, () => true, () => new DateTime(2026, 10, 5, 23, 0, 0));
        string Body(Guid id) => store.Load(id)!.Doc.Body.Replace("\r\n", "\n");

        var memo = store.Create("절차\n[ ] 하나")!;
        var other = store.Create("다른 절차\n[x] 둘")!;
        store.SetDaily(memo.Doc.Id, true, Mon);
        store.SetDaily(other.Doc.Id, true, Mon);
        var editor = new MemoEditorState(store);
        editor.Open(memo.Doc.Id);
        editor.ToggleCheck(1);
        Func<Guid, bool> dirty = id => editor.IsDirty && editor.CurrentId == id;

        // 새벽에 쓰는 중: 먼저 저장하고 푼다 → 쓰던 글은 남고, 충돌 사본이 생기지 않는다.
        editor.IsEditing = true;
        editor.Edit("절차\n[x] 하나\n새벽에 쓴 줄");
        var flushed = false;
        var count = MemoLinks.ResetAtRollover(store, Tue, () => { editor.Flush(); flushed = true; }, () => editor.CurrentId, dirty).Written;
        Check("롤오버는 먼저 저장하게 함", flushed, true);
        Check("저장한 뒤 풂 (둘 다)", count, 2);
        Check("쓰던 글은 남고 체크만 풀림", Body(memo.Doc.Id), "절차\n[ ] 하나\n새벽에 쓴 줄");
        editor.IsEditing = false;
        editor.OnExternalChange();
        Check("메모 창은 푼 글을 다시 읽음", editor.Body, "절차\n[ ] 하나\n새벽에 쓴 줄");
        editor.Edit("절차\n[ ] 하나\n새벽에 쓴 줄\n아침");
        Check("그 뒤 저장도 충돌 없음", editor.Flush()!.Outcome, SaveOutcome.Saved);
        Check("충돌 사본 없음", store.Memos.Count, 2);

        // 저장이 안 된 채(dirty)면 이번에는 그 메모를 건너뛰고, 다음 기회에 푼다.
        editor.ToggleCheck(1);
        editor.Edit("절차\n[x] 하나\n새벽에 쓴 줄\n아침\n저장 못 함");
        var other2 = store.Load(other.Doc.Id)!;
        store.Save(other.Doc.Id, other2.Doc.Body.Replace("[ ] 둘", "[x] 둘"), other2.Rev);
        Check("저장 못 한 메모는 건너뜀 (나머지는 풂)", MemoLinks.ResetAtRollover(store, Wed, null, () => editor.CurrentId, dirty).Written, 1);
        Check("건너뛴 메모는 체크 그대로", Body(memo.Doc.Id).Contains("[x] 하나"), true);
        Check("다른 매일 메모는 풀림", Body(other.Doc.Id), "다른 절차\n[ ] 둘");
        Check("쓰던 글도 그대로", editor.Body.EndsWith("저장 못 함"), true);
        Check("나중에 저장하면 충돌 없음", editor.Flush()!.Outcome, SaveOutcome.Saved);
        Check("다음 기회에 풂", MemoLinks.ResetAtRollover(store, Wed, () => editor.Flush(), () => editor.CurrentId, dirty).Written, 1);
        Check("그때 풀림", Body(memo.Doc.Id), "절차\n[ ] 하나\n새벽에 쓴 줄\n아침\n저장 못 함");
    }
}
