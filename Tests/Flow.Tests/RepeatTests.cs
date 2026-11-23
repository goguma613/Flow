using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Flow.Models;
using Flow.Services;

namespace Flow.Tests;

internal static partial class Program
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>매월 25일, 남는 방식. 첫 차례 2026-11-25.</summary>
    private static RepeatRule M25() => new()
    {
        Unit = RepeatUnit.MonthDates,
        MonthDays = { 25 },
        Anchor = D(2026, 11, 25),
        Sticky = true
    };

    // ───────────────────────── 반복 · 계산

    private static void RepeatEngineTests()
    {
        var m25 = M25();
        Check("매월 25일: 그날", RepeatEngine.OccursOn(m25, D(2026, 12, 25)), true);
        Check("매월 25일: 다른 날", RepeatEngine.OccursOn(m25, D(2026, 12, 24)), false);
        Check("첫 차례 앞은 아님", RepeatEngine.OccursOn(m25, D(2026, 10, 25)), false);
        Check("다음", RepeatEngine.Next(m25, D(2026, 11, 26)), (DateOnly?)D(2026, 12, 25));
        Check("다음: 그날 포함", RepeatEngine.Next(m25, D(2026, 12, 25)), (DateOnly?)D(2026, 12, 25));
        Check("다음: 첫 차례 앞에서 물으면 첫 차례", RepeatEngine.Next(m25, D(2026, 1, 1)), (DateOnly?)D(2026, 11, 25));
        Check("직전", RepeatEngine.Previous(m25, D(2026, 12, 25)), (DateOnly?)D(2026, 11, 25));
        Check("직전 없음", RepeatEngine.Previous(m25, D(2026, 11, 25)), (DateOnly?)null);

        // Review Focus 1: 짧은 달에 겹침
        var ends = new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 30, 31, 32 }, Anchor = D(2027, 1, 30) };
        Check("2월엔 28일 하루만", Enumerable.Range(1, 28).Count(d => RepeatEngine.OccursOn(ends, D(2027, 2, d))), 1);
        Check("그 하루는 28일", RepeatEngine.OccursOn(ends, D(2027, 2, 28)), true);
        Check("윤년 2월은 29일", RepeatEngine.OccursOn(ends, D(2028, 2, 29)), true);
        Check("윤년 2월 28일은 아님", RepeatEngine.OccursOn(ends, D(2028, 2, 28)), false);
        Check("4월은 30일 하루", Enumerable.Range(1, 30).Count(d => RepeatEngine.OccursOn(ends, D(2027, 4, d))), 1);
        Check("1월은 30 · 31일 이틀", Enumerable.Range(1, 31).Count(d => RepeatEngine.OccursOn(ends, D(2027, 1, d))), 2);
        Check("2월 28일 다음은 3월 30일", RepeatEngine.Next(ends, D(2027, 3, 1)), (DateOnly?)D(2027, 3, 30));

        var biweek = new RepeatRule { Unit = RepeatUnit.Weekly, Every = 2, Days = { 1 }, Anchor = D(2026, 11, 23), Sticky = true };
        Check("격주 월: 첫 주", RepeatEngine.OccursOn(biweek, D(2026, 11, 23)), true);
        Check("격주 월: 다음 주는 쉼", RepeatEngine.OccursOn(biweek, D(2026, 11, 30)), false);
        Check("격주 월: 그다음 주", RepeatEngine.OccursOn(biweek, D(2026, 12, 7)), true);

        // 격주 화 · 일: 첫 차례가 주 가운데(화)여도 같은 주의 일요일이 한 묶음이다(주는 월요일 시작)
        var tueSun = new RepeatRule { Unit = RepeatUnit.Weekly, Every = 2, Days = { 2, 0 }, Anchor = D(2026, 11, 24) };
        Check("격주: 같은 주 일요일", RepeatEngine.OccursOn(tueSun, D(2026, 11, 29)), true);
        Check("격주: 다음 주 화요일은 쉼", RepeatEngine.OccursOn(tueSun, D(2026, 12, 1)), false);
        Check("격주: 그다음 주 화요일", RepeatEngine.OccursOn(tueSun, D(2026, 12, 8)), true);

        var q10 = new RepeatRule { Unit = RepeatUnit.MonthDates, Every = 3, MonthDays = { 10 }, Anchor = D(2027, 1, 10), Sticky = true };
        Check("분기: 4월", RepeatEngine.OccursOn(q10, D(2027, 4, 10)), true);
        Check("분기: 2월 아님", RepeatEngine.OccursOn(q10, D(2027, 2, 10)), false);
        Check("분기: 해를 넘겨도", RepeatEngine.OccursOn(q10, D(2028, 1, 10)), true);

        var tue2 = new RepeatRule { Unit = RepeatUnit.MonthNth, Nth = 2, NthDay = 2, Anchor = D(2026, 12, 8) };
        Check("둘째 화", RepeatEngine.OccursOn(tue2, D(2027, 1, 12)), true);
        Check("첫째 화는 아님", RepeatEngine.OccursOn(tue2, D(2027, 1, 5)), false);
        var lastFri = new RepeatRule { Unit = RepeatUnit.MonthNth, Nth = RepeatUnit.LastWeek, NthDay = 5, Anchor = D(2026, 11, 27) };
        Check("마지막 금", RepeatEngine.OccursOn(lastFri, D(2026, 12, 25)), true);
        Check("마지막이 아닌 금", RepeatEngine.OccursOn(lastFri, D(2026, 12, 18)), false);

        var yearly = new RepeatRule { Unit = RepeatUnit.Yearly, Month = 2, Day = 29, Anchor = D(2028, 2, 29) };
        Check("매년 2/29 → 평년 2/28", RepeatEngine.OccursOn(yearly, D(2029, 2, 28)), true);
        Check("매년 2/29 → 윤년엔 29일", (RepeatEngine.OccursOn(yearly, D(2032, 2, 29)), RepeatEngine.OccursOn(yearly, D(2032, 2, 28))), (true, false));
        Check("매년: 다음", RepeatEngine.Next(yearly, D(2028, 3, 1)), (DateOnly?)D(2029, 2, 28));
        var every3 = new RepeatRule { Unit = RepeatUnit.EveryDays, Every = 3, Anchor = D(2026, 11, 23) };
        Check("3일마다", (RepeatEngine.OccursOn(every3, D(2026, 11, 26)), RepeatEngine.OccursOn(every3, D(2026, 11, 25))), (true, false));

        Check("남은 차례: 처음", RepeatEngine.PendingSince(m25, null, D(2026, 11, 25)), (DateOnly?)D(2026, 11, 25));
        Check("남은 차례: 첫 차례 전", RepeatEngine.PendingSince(m25, null, D(2026, 11, 24)), (DateOnly?)null);
        Check("남은 차례: 끝냈음", RepeatEngine.PendingSince(m25, D(2026, 11, 26), D(2026, 12, 24)), (DateOnly?)null);
        Check("남은 차례: 가장 오래된 것", RepeatEngine.PendingSince(m25, D(2026, 11, 26), D(2027, 2, 3)), (DateOnly?)D(2026, 12, 25));
        Check("남은 차례: 차례 날 끝냈으면 그날은 없음", RepeatEngine.PendingSince(m25, D(2026, 11, 25), D(2026, 11, 25)), (DateOnly?)null);
        Check("남은 차례: 첫 차례보다 앞선 완료일", RepeatEngine.PendingSince(m25, D(2026, 3, 1), D(2026, 11, 26)), (DateOnly?)D(2026, 11, 25));

        var draft = new RepeatRule { Unit = RepeatUnit.Weekly, Every = 2, Days = { 1 } };
        Check("첫 차례 기본값", RepeatEngine.WithFirst(draft, D(2026, 11, 24)).Anchor, D(2026, 11, 30));
        Check("첫 차례: 오늘이 차례면 오늘", RepeatEngine.WithFirst(draft, D(2026, 11, 23)).Anchor, D(2026, 11, 23));
        Check("첫 차례를 정해도 원본은 그대로", draft.Anchor, default(DateOnly));
        Check("한 차례 뒤로", RepeatEngine.ShiftFirst(RepeatEngine.WithFirst(draft, D(2026, 11, 24)), 1, D(2026, 11, 24)).Anchor, D(2026, 12, 7));
        Check("오늘 앞으로는 안 감", RepeatEngine.ShiftFirst(RepeatEngine.WithFirst(draft, D(2026, 11, 24)), -1, D(2026, 11, 24)).Anchor, D(2026, 11, 30));
        Check("뒤로 갔다 앞으로", RepeatEngine.ShiftFirst(RepeatEngine.ShiftFirst(RepeatEngine.WithFirst(draft, D(2026, 11, 24)), 2, D(2026, 11, 24)), -1, D(2026, 11, 24)).Anchor, D(2026, 12, 7));
        var q = new RepeatRule { Unit = RepeatUnit.MonthDates, Every = 3, MonthDays = { 10 } };
        Check("3개월마다: 첫 차례는 가장 가까운 10일", RepeatEngine.WithFirst(q, D(2026, 11, 23)).Anchor, D(2026, 12, 10));
        Check("3개월마다: 한 달 뒤로", RepeatEngine.ShiftFirst(RepeatEngine.WithFirst(q, D(2026, 11, 23)), 1, D(2026, 11, 23)).Anchor, D(2027, 1, 10));

        Check("옛 방식: 매일", RepeatEngine.IsLegacy(new RepeatRule { Unit = RepeatUnit.Daily }), true);
        Check("옛 방식: 지나가는 매주", RepeatEngine.IsLegacy(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 1, 3 } }), true);
        Check("남기는 매주는 새 방식", RepeatEngine.IsLegacy(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 1 }, Sticky = true }), false);
        Check("격주는 새 방식", RepeatEngine.IsLegacy(new RepeatRule { Unit = RepeatUnit.Weekly, Every = 2, Days = { 1 } }), false);
        Check("요일 일곱 개 = 매일", RepeatEngine.LegacyDays(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 0, 1, 2, 3, 4, 5, 6 } }).Count, 0);
        Check("옛 요일은 월요일부터", string.Join(",", RepeatEngine.LegacyDays(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 0, 5, 1, 1 } })), "Monday,Friday,Sunday");
        Check("옛 방식에서 오기: 매일", RepeatEngine.FromLegacy(Array.Empty<DayOfWeek>()).Unit, RepeatUnit.Daily);
        var fromDays = RepeatEngine.FromLegacy(new[] { DayOfWeek.Monday, DayOfWeek.Friday });
        Check("옛 방식에서 오기: 요일", (fromDays.Unit, fromDays.Every, fromDays.Sticky, string.Join(",", fromDays.Days)), (RepeatUnit.Weekly, 1, false, "1,5"));
        Check("모르는 Unit", RepeatEngine.IsKnown(new RepeatRule { Unit = 9 }), false);
        Check("매일(0)은 파일에 둘 수 없는 값", RepeatEngine.IsKnown(new RepeatRule { Unit = RepeatUnit.Daily }), false);
        Check("범위 밖 날짜", RepeatEngine.IsKnown(new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 40 } }), false);
        Check("간격 0", RepeatEngine.IsKnown(new RepeatRule { Unit = RepeatUnit.EveryDays, Every = 0 }), false);
        Check("모르는 규칙은 아무 날도 아님", (RepeatEngine.OccursOn(new RepeatRule { Unit = 9 }, D(2026, 11, 23)), RepeatEngine.Next(new RepeatRule { Unit = 9 }, D(2026, 11, 23))), (false, (DateOnly?)null));
        Check("요일 없는 매주는 저장 못 함", RepeatEngine.IsValid(new RepeatRule { Unit = RepeatUnit.Weekly }), false);
        Check("매일은 저장할 수 있음", RepeatEngine.IsValid(new RepeatRule { Unit = RepeatUnit.Daily }), true);
        Check("기본 남기기", (RepeatEngine.DefaultSticky(RepeatUnit.Weekly, 1), RepeatEngine.DefaultSticky(RepeatUnit.Weekly, 2), RepeatEngine.DefaultSticky(RepeatUnit.MonthDates, 1)), (false, true, true));
        Check("매일은 남기지 않음", RepeatEngine.DefaultSticky(RepeatUnit.Daily, 1), false);

        var a = M25();
        var b = M25(); b.Sticky = false;
        Check("남기기만 다르면 같은 일정", RepeatEngine.SameSchedule(a, b), true);
        b.Anchor = D(2026, 12, 25);
        Check("첫 차례가 다르면 다른 일정", RepeatEngine.SameSchedule(a, b), false);
        Check("첫 차례를 빼고 보면 같은 주기", RepeatEngine.SameCadence(a, b), true);
        Check("요일 순서는 상관없음", RepeatEngine.SameSchedule(
            new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 3, 1 } },
            new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 1, 3, 3 } }), true);
        Check("날짜가 다르면 다른 주기", RepeatEngine.SameCadence(a, new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 10 } }), false);
        var copy = a.Clone(); copy.MonthDays.Add(10);
        Check("사본을 고쳐도 원본은 그대로", a.MonthDays.Count, 1);

        string Desc(RepeatRule r) => RepeatEngine.Describe(r);
        Check("글: 매일", Desc(new RepeatRule { Unit = RepeatUnit.Daily }), "매일");
        Check("글: 월 수 금", Desc(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 5, 1, 3 } }), "월 수 금");
        Check("글: 일요일은 맨 뒤", Desc(new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 0, 6 } }), "토 일");
        Check("글: 격주 월", Desc(biweek), "격주 월");
        Check("글: 3주마다 월 수", Desc(new RepeatRule { Unit = RepeatUnit.Weekly, Every = 3, Days = { 1, 3 } }), "3주마다 월 수");
        Check("글: 매월 25일", Desc(m25), "매월 25일");
        Check("글: 매월 10·25일", Desc(new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 25, 10 } }), "매월 10·25일");
        Check("글: 매월 말일", Desc(new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 32 } }), "매월 말일");
        Check("글: 날짜와 말일", Desc(new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 32, 10 } }), "매월 10일·말일");
        Check("글: 매월 둘째 화", Desc(tue2), "매월 둘째 화");
        Check("글: 매월 마지막 금", Desc(lastFri), "매월 마지막 금");
        Check("글: 3개월마다 10일", Desc(q10), "3개월마다 10일");
        Check("글: 3개월마다 둘째 화", Desc(new RepeatRule { Unit = RepeatUnit.MonthNth, Every = 3, Nth = 2, NthDay = 2 }), "3개월마다 둘째 화");
        Check("글: 매년 5/31", Desc(new RepeatRule { Unit = RepeatUnit.Yearly, Month = 5, Day = 31 }), "매년 5/31");
        Check("글: 3일마다", Desc(every3), "3일마다");
        Check("글: 모르는 규칙", Desc(new RepeatRule { Unit = 9 }), "반복 정보 없음");
    }

    // ───────────────────────── 반복 · 파일

    private static void RepeatStoreTests()
    {
        // data.json 에는 규칙이 들어가지 않는다 — 들어가면 옛 버전이 저장하며 지운다
        var plain = new AppData();
        plain.Routines.Add(new Routine { Title = "정산", Archived = true, Repeat = M25() });
        Check("Repeat 는 data.json 에 없음", DataStore.Serialize(plain).Contains("Anchor"), false);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 반복 파일 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = Path.Combine(DataStore.Directory, "repeattest-store");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        try
        {
            var store = new RepeatStore(root, () => true);
            Check("폴더 자리", store.Folder, Path.Combine(root, "repeats"));
            Check("폴더가 없어도 훑기는 조용함", (store.Scan(), Directory.Exists(store.Folder)), (false, false));

            var data = new AppData();
            var legacy = new Routine { Title = "매일" };
            var monthly = new Routine { Title = "정산", Archived = true, Repeat = M25() };
            data.Routines.AddRange(new[] { legacy, monthly });

            var onlyLegacy = new AppData();
            onlyLegacy.Routines.Add(new Routine { Title = "물" });
            Check("옛 방식뿐이면 폴더도 안 생김", (store.Sync(onlyLegacy), Directory.Exists(store.Folder)), (true, false));

            Check("쓰기", store.Sync(data), true);
            var path = Path.Combine(store.Folder, monthly.Id.ToString("D") + ".json");
            Check("옛 방식 루틴은 파일 없음", File.Exists(Path.Combine(store.Folder, legacy.Id + ".json")), false);
            Check("새 반복은 파일 하나", Directory.GetFiles(store.Folder).Length, 1);
            Check("파일 이름은 루틴 Id", File.Exists(path), true);
            Check("BOM 없음", File.ReadAllBytes(path)[0], (byte)'{');
            Check("숫자로 적힘", File.ReadAllText(path).Contains("\"Unit\": 3"), true);
            Check("임시 파일을 남기지 않음", Directory.GetFiles(store.Folder, "*.tmp").Length, 0);

            var stamp = File.GetLastWriteTimeUtc(path);
            System.Threading.Thread.Sleep(30);
            Check("같으면 다시 안 씀", (store.Sync(data), File.GetLastWriteTimeUtc(path) == stamp), (true, true));
            Check("스스로 쓴 파일은 바뀜이 아님", store.Scan(), false);

            var mine = store.Get(monthly.Id)!;
            mine.MonthDays.Add(1);
            Check("읽어 둔 규칙은 사본으로 내준다", store.Get(monthly.Id)!.MonthDays.Count, 1);

            var other = new RepeatStore(root, () => true);   // 다른 PC
            Check("다른 PC 의 첫 훑기", other.Scan(), true);
            var copy = new AppData();
            copy.Routines.Add(new Routine { Id = monthly.Id, Archived = true });
            other.Attach(copy);
            Check("다른 PC 가 읽음", RepeatEngine.Describe(copy.Routines[0].Repeat!), "매월 25일");
            Check("남기기 · 첫 차례도 같이", (copy.Routines[0].Repeat!.Sticky, copy.Routines[0].Repeat!.Anchor), (true, D(2026, 11, 25)));
            Check("바뀐 것이 없으면 조용함", other.Scan(), false);

            // 반복을 바꾸면 그 파일만 다시 쓴다
            monthly.Repeat!.MonthDays[0] = 10;
            Check("바꾼 규칙 쓰기", store.Sync(data), true);
            Check("다른 PC 가 바뀜을 알아챔", other.Scan(), true);
            other.Attach(copy);
            Check("바뀐 규칙", RepeatEngine.Describe(copy.Routines[0].Repeat!), "매월 10일");

            // Review Focus 3: 덜 받아진 파일
            File.WriteAllText(path, "{ \"Unit\": 3, \"Mon");
            Check("잘린 파일은 바뀜으로 알림", other.Scan(), true);
            other.Attach(copy);
            Check("잘린 파일 = 규칙 없음", copy.Routines[0].Repeat, null);
            Check("그 뒤 저장이 파일을 덮지 않음", (other.Sync(copy), File.ReadAllText(path)), (true, "{ \"Unit\": 3, \"Mon"));
            File.WriteAllText(path, "");
            other.Scan(); other.Attach(copy);
            Check("0바이트도 규칙 없음", copy.Routines[0].Repeat, null);
            File.WriteAllText(path, "null");
            other.Scan(); other.Attach(copy);
            Check("null 도 규칙 없음", copy.Routines[0].Repeat, null);
            File.WriteAllText(path, "{\"Unit\":3,\"Days\":null,\"MonthDays\":null,\"Anchor\":\"2026-11-25\"}");
            other.Scan(); other.Attach(copy);
            Check("목록이 null 이어도 죽지 않음", (copy.Routines[0].Repeat?.MonthDays.Count, RepeatEngine.IsKnown(copy.Routines[0].Repeat!)), (0, false));
            File.WriteAllText(path, "{\"Unit\":9,\"Every\":1,\"Anchor\":\"2026-11-25\"}");
            other.Scan(); other.Attach(copy);
            Check("모르는 Unit 은 읽기만", (copy.Routines[0].Repeat?.Unit, RepeatEngine.IsKnown(copy.Routines[0].Repeat!)), (9, false));
            other.Sync(copy);
            Check("모르는 Unit 파일을 덮지 않음", File.ReadAllText(path).Contains("\"Unit\":9"), true);

            // 뒤늦게 온 파일: 저절로 돌아온다
            File.Delete(path);
            Check("없어진 파일도 바뀜", other.Scan(), true);
            other.Attach(copy);
            Check("파일이 없으면 규칙 없음", copy.Routines[0].Repeat, null);
            store.Scan();
            Check("내 규칙으로 다시 쓰기", (store.Sync(data), File.Exists(path)), (true, true));
            Check("뒤늦게 온 파일을 알아챔", other.Scan(), true);
            other.Attach(copy);
            Check("돌아옴", RepeatEngine.Describe(copy.Routines[0].Repeat!), "매월 10일");

            // 엉뚱한 파일
            File.WriteAllText(Path.Combine(store.Folder, "메모.json"), "{}");
            File.WriteAllText(Path.Combine(store.Folder, Guid.NewGuid().ToString("D") + ".json.tmp"), "{}");
            Check("이름이 Id 가 아닌 파일 · 임시 파일은 무시", other.Scan(), false);

            // 루틴 없는 파일은 안 지운다
            var empty = new AppData();
            store.Scan(); store.Attach(empty); store.Sync(empty);
            Check("루틴 없는 파일은 그대로", File.Exists(path), true);

            Routine AttachTo(Routine routine)
            {
                var holder = new AppData();
                holder.Routines.Add(routine);
                store.Attach(holder);
                return routine;
            }
            Check("Archived 아닌 루틴엔 안 단다", AttachTo(new Routine { Id = monthly.Id }).Repeat, null);
            Check("Archived 가 풀린 루틴의 규칙은 뗀다", AttachTo(new Routine { Id = monthly.Id, Repeat = M25() }).Repeat, null);
            Check("파일 없는 Archived 루틴", AttachTo(new Routine { Archived = true }).Repeat, null);

            var blocked = new RepeatStore(root, () => false);
            blocked.Scan();
            Check("못 쓰는 동안엔 지우지 않음", (blocked.Delete(monthly.Id), File.Exists(path)), (false, true));
            Check("지우기", (store.Delete(monthly.Id), File.Exists(path)), (true, false));
            Check("없는 것을 지워도 참", store.Delete(Guid.NewGuid()), true);
            Check("지운 뒤엔 규칙 없음", store.Get(monthly.Id), null);
            Check("못 쓰는 동안엔 안 씀", (new RepeatStore(root, () => false).Sync(data), File.Exists(path)), (false, false));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
