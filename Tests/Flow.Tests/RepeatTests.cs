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
}
