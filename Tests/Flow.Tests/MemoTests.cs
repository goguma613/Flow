using System;
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
}
