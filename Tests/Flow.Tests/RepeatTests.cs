using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    // ───────────────────────── 반복 · 하루 넘기기

    private static void RepeatDayTests()
    {
        // 옛 방식은 그대로
        var daily = new Routine { Title = "물" };
        var mwf = new Routine { Title = "운동", Days = { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday } };
        Check("매일은 그대로", DayEngine.IsDue(daily, D(2026, 11, 24)), true);
        Check("요일도 그대로", (DayEngine.IsDue(mwf, D(2026, 11, 23)), DayEngine.IsDue(mwf, D(2026, 11, 24))), (true, false));
        Check("다음 날(옛 방식)", DayEngine.NextDay(mwf, D(2026, 11, 24)), (DateOnly?)D(2026, 11, 25));
        Check("다음 날: 오늘이 차례면 오늘", DayEngine.NextDay(mwf, D(2026, 11, 23)), (DateOnly?)D(2026, 11, 23));
        Check("옛 방식엔 규칙 · 남기기 · 밀림이 없음",
            (DayEngine.HasRule(mwf), DayEngine.IsSticky(mwf), DayEngine.IsRepeatMissing(mwf), DayEngine.OverdueDays(mwf, D(2026, 11, 24)), DayEngine.DueSince(mwf, D(2026, 11, 24))),
            (false, false, false, 0, (DateOnly?)null));
        Check("옛 방식의 직전 차례", DayEngine.PreviousScheduledDay(mwf, D(2026, 11, 25)), (DateOnly?)D(2026, 11, 23));

        // 남는 매월
        var pay = new Routine { Title = "정산", Archived = true, Repeat = M25() };   // 매월 25일, Sticky
        Check("규칙이 있는 루틴", (DayEngine.HasRule(pay), DayEngine.IsSticky(pay), DayEngine.IsRepeatMissing(pay)), (true, true, false));
        Check("차례 전엔 안 보임", (DayEngine.IsDue(pay, D(2026, 11, 24)), DayEngine.OverdueDays(pay, D(2026, 11, 24))), (false, 0));
        Check("차례 날", (DayEngine.IsDue(pay, D(2026, 11, 25)), DayEngine.OverdueDays(pay, D(2026, 11, 25))), (true, 0));
        Check("못 하면 남고 밀림 3일", (DayEngine.IsDue(pay, D(2026, 11, 28)), DayEngine.OverdueDays(pay, D(2026, 11, 28))), (true, 3));
        Check("남은 날은 '차례'가 아님(알림 · 기록의 기준)", (DayEngine.IsScheduled(pay, D(2026, 11, 25)), DayEngine.IsScheduled(pay, D(2026, 11, 28))), (true, false));
        // Review Focus 2: 두 차례가 지나도 하나, 가장 오래된 것부터
        Check("두 차례 지나도 가장 오래된 차례부터", DayEngine.DueSince(pay, D(2027, 1, 3)), (DateOnly?)D(2026, 11, 25));
        DayEngine.CompleteRoutine(pay, D(2027, 1, 3));
        Check("체크한 날은 체크된 채 보임", (DayEngine.IsDue(pay, D(2027, 1, 3)), pay.DoneToday, pay.Streak), (true, true, 0));
        Check("체크한 뒤엔 밀림 없음", (DayEngine.OverdueDays(pay, D(2027, 1, 3)), pay.LastDoneDate), (0, (DateOnly?)D(2027, 1, 3)));
        DayEngine.UncompleteRoutine(pay);
        Check("같은 날 풀면 다시 밀림", DayEngine.DueSince(pay, D(2027, 1, 3)), (DateOnly?)D(2026, 11, 25));
        DayEngine.CompleteRoutine(pay, D(2027, 1, 3));
        pay.DoneToday = false;   // 다음 날 정산이 푼다
        Check("다음 날엔 사라짐", DayEngine.IsDue(pay, D(2027, 1, 4)), false);
        Check("체크 한 번으로 지난 차례가 다 끝남", DayEngine.IsDue(pay, D(2027, 1, 24)), false);
        Check("다음 차례에 다시", DayEngine.IsDue(pay, D(2027, 1, 25)), true);
        Check("남는 항목은 최고 기록도 안 건드림", pay.BestStreak, 0);

        // 정산이 아직 안 돈 채(DoneToday 가 어제 값으로 남음) 날짜만 넘어간 경우
        var stale = new Routine { Archived = true, Repeat = M25(), DoneToday = true, LastDoneDate = D(2026, 11, 25) };
        Check("어제 체크는 오늘 '체크된 채 보임'이 아님", DayEngine.IsDue(stale, D(2026, 11, 26)), false);

        // Review Focus 5: 첫 차례가 앞날
        var later = new Routine { Archived = true, Repeat = new RepeatRule { Unit = RepeatUnit.Weekly, Every = 2, Days = { 1 }, Anchor = D(2026, 12, 7), Sticky = true } };
        Check("첫 차례 전엔 안 보이고 밀림도 없음", (DayEngine.IsDue(later, D(2026, 11, 30)), DayEngine.OverdueDays(later, D(2026, 12, 6))), (false, 0));
        Check("루틴 탭의 '다음'", DayEngine.NextDay(later, D(2026, 11, 24)), (DateOnly?)D(2026, 12, 7));

        // 규칙 없음
        var lost = new Routine { Archived = true };
        Check("규칙 없음: 오늘엔 안 보임", (DayEngine.IsRepeatMissing(lost), DayEngine.IsDue(lost, D(2026, 11, 24)), DayEngine.NextDay(lost, D(2026, 11, 24))), (true, false, (DateOnly?)null));
        var unknown = new Routine { Archived = true, Repeat = new RepeatRule { Unit = 9 } };
        Check("모르는 Unit 도 규칙 없음", (DayEngine.IsRepeatMissing(unknown), DayEngine.HasRule(unknown), DayEngine.IsDue(unknown, D(2026, 11, 24))), (true, false, false));
        DayEngine.CompleteRoutine(lost, D(2026, 11, 24));
        Check("규칙 없는 루틴은 체크해도 그대로", (lost.DoneToday, lost.LastDoneDate, lost.Streak), (false, (DateOnly?)null, 0));
        // Archived 가 아니면 규칙이 달려 있어도 옛 방식이다(data.json 이 기준)
        var plain = new Routine { Repeat = M25() };
        Check("Archived 아닌 루틴의 규칙은 안 쓴다", (DayEngine.HasRule(plain), DayEngine.IsDue(plain, D(2026, 11, 24))), (false, true));

        // 지나가는 매월(Sticky 끔): 연속 기록이 한 달 간격으로 이어진다
        var habit = new Routine { Archived = true, Repeat = new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 25 }, Anchor = D(2026, 11, 25) } };
        Check("지나가는 매월: 차례 날만", (DayEngine.IsDue(habit, D(2026, 11, 25)), DayEngine.IsDue(habit, D(2026, 11, 26)), DayEngine.OverdueDays(habit, D(2026, 11, 26))), (true, false, 0));
        DayEngine.CompleteRoutine(habit, D(2026, 11, 25)); habit.DoneToday = false;
        DayEngine.CompleteRoutine(habit, D(2026, 12, 25));
        Check("한 달 간격 연속 2", habit.Streak, 2);
        habit.DoneToday = false;
        DayEngine.CompleteRoutine(habit, D(2027, 2, 25));   // 1월을 건너뜀
        Check("한 번 건너뛰면 1부터", habit.Streak, 1);

        // 정산: 남는 항목은 연속 기록을 끊지 않고, 끝낸 날에만 센다
        var app = new AppData { LastLogicalDate = D(2026, 11, 25) };
        var sticky = new Routine { Title = "정산", Archived = true, Repeat = M25(), Streak = 0 };
        var water = new Routine { Title = "물", Streak = 5, LastDoneDate = D(2026, 11, 25) };
        var skipped = new Routine { Title = "지나가는 매월", Archived = true, Streak = 4, LastDoneDate = D(2026, 10, 25),
            Repeat = new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 25 }, Anchor = D(2026, 9, 25) } };
        var missing = new Routine { Title = "규칙 없음", Archived = true, Streak = 9 };
        app.Routines.AddRange(new[] { sticky, water, skipped, missing });
        DayEngine.Rollover(app, D(2026, 11, 27));
        Check("못 한 남는 항목은 그날 기록에 안 들어감",
            (app.History.First(h => h.Date == D(2026, 11, 25)).RoutinesScheduled, app.History.First(h => h.Date == D(2026, 11, 25)).RoutinesDone), (2, 1));
        Check("차례가 아닌 날엔 규칙 루틴을 안 센다", app.History.First(h => h.Date == D(2026, 11, 26)).RoutinesScheduled, 1);
        Check("며칠 건너뛰어도 여전히 남음", DayEngine.OverdueDays(sticky, D(2026, 11, 27)), 2);
        Check("매일 루틴은 지금처럼 끊김", water.Streak, 0);
        Check("지나가는 매월은 차례 날 못 하면 끊김", skipped.Streak, 0);
        Check("규칙 없는 루틴의 기록은 손대지 않음", missing.Streak, 9);

        // 남는 항목을 밀린 날 끝냈으면 그날 기록에 1 로 들어간다
        DayEngine.CompleteRoutine(sticky, D(2026, 11, 27));
        DayEngine.SyncToday(app, D(2026, 11, 27));
        var todayRecord = app.History.First(h => h.Date == D(2026, 11, 27));
        Check("오늘 기록: 끝낸 남는 항목 + 매일", (todayRecord.RoutinesScheduled, todayRecord.RoutinesDone), (2, 1));
        DayEngine.Rollover(app, D(2026, 11, 28));
        var closed = app.History.First(h => h.Date == D(2026, 11, 27));
        Check("정산 뒤에도 같은 값", (closed.RoutinesScheduled, closed.RoutinesDone, sticky.DoneToday), (2, 1, false));
        DayEngine.SyncToday(app, D(2026, 11, 28));
        Check("못 끝낸 남는 항목은 오늘 기록에 안 들어감", app.History.First(h => h.Date == D(2026, 11, 28)).RoutinesScheduled, 1);

        // 알림: 차례 날에만 울린다. 밀려 남은 날은 다시 울리지 않는다
        var ring = new Routine { Title = "정산", Archived = true, Repeat = M25(), Time = new TimeOnly(9, 0), Remind = true };
        Check("알림: 차례 날", ReminderEngine.OccurrenceOn(ring, D(2026, 11, 25), 4), (DateTime?)new DateTime(2026, 11, 25, 9, 0, 0));
        Check("알림: 밀린 날은 아님", ReminderEngine.OccurrenceOn(ring, D(2026, 11, 26), 4), (DateTime?)null);
        var ringData = Data(ring);
        ringData.LastLogicalDate = D(2026, 11, 25);
        Check("알림: 차례 날 정시에 1건", Pending(ringData, D(2026, 11, 25), 9, 0).Count, 1);
        Check("알림: 다음 날 같은 시각엔 0건", Pending(ringData, D(2026, 11, 26), 9, 0).Count, 0);
        var silent = new Routine { Archived = true, Time = new TimeOnly(9, 0), Remind = true };
        Check("알림: 규칙 없는 루틴은 조용함", ReminderEngine.OccurrenceOn(silent, D(2026, 11, 25), 4), (DateTime?)null);
    }

    // ───────────────────────── 반복 · 문장과 옮기기

    private static void RepeatParseTests()
    {
        // 오늘은 2026-11-23(월)
        QuickAddResult P(string s) => QuickAddParser.Parse(s, D(2026, 11, 23));
        string R(string s) => P(s).Repeat is { } r ? $"{RepeatEngine.Describe(r)} @{r.Anchor:yyyy-MM-dd} | {P(s).Title}" : "없음 | " + P(s).Title;

        Check("매월 25일", R("매월 25일 정산"), "매월 25일 @2026-11-25 | 정산");
        Check("매달 말일", R("매달 말일 결산"), "매월 말일 @2026-11-30 | 결산");
        Check("날짜 둘", R("매월 10일 25일 송금"), "매월 10·25일 @2026-11-25 | 송금");
        Check("날짜 둘(쉼표)", R("매월 10일, 25일 송금"), "매월 10·25일 @2026-11-25 | 송금");
        Check("날짜와 말일", R("매월 15일 말일 급여"), "매월 15일·말일 @2026-11-30 | 급여");
        Check("격주 월요일", R("격주 월요일 회의"), "격주 월 @2026-11-23 | 회의");
        Check("2주마다 화목", R("2주마다 화목 운동"), "격주 화 목 @2026-11-24 | 운동");
        Check("3주마다 금요일", R("3주마다 금요일 대청소"), "3주마다 금 @2026-11-27 | 대청소");
        Check("요일 없는 격주는 오늘 요일", R("격주 회의"), "격주 월 @2026-11-23 | 회의");
        Check("3일마다", R("3일마다 화분 물"), "3일마다 @2026-11-23 | 화분 물");
        Check("매년", R("매년 5월 31일 종합소득세"), "매년 5/31 @2027-05-31 | 종합소득세");
        Check("매년(빗금)", R("매년 5/31 종합소득세"), "매년 5/31 @2027-05-31 | 종합소득세");
        Check("날짜 없는 매년은 오늘", R("매년 창립기념"), "매년 11/23 @2026-11-23 | 창립기념");
        Check("분기마다 = 달력 분기", R("분기마다 10일 부가세"), "3개월마다 10일 @2027-01-10 | 부가세");
        Check("반기마다", R("반기마다 말일 점검"), "6개월마다 말일 @2027-01-31 | 점검");
        Check("둘째 화요일", R("매월 둘째 화요일 정기회의"), "매월 둘째 화 @2026-12-08 | 정기회의");
        Check("마지막 금요일", R("매월 마지막 금요일 회식"), "매월 마지막 금 @2026-11-27 | 회식");
        Check("둘째 주 화요일", R("매월 둘째 주 화요일 정기회의"), "매월 둘째 화 @2026-12-08 | 정기회의");
        Check("3개월마다", R("3개월마다 15일 점검"), "3개월마다 15일 @2026-12-15 | 점검");
        Check("날짜 없는 매월은 오늘 날짜", R("매월 정산"), "매월 23일 @2026-11-23 | 정산");
        Check("반복 말이 뒤에 와도", R("정산 매월 25일"), "매월 25일 @2026-11-25 | 정산");
        Check("반복 말이 맨 끝", R("화분 물 3일마다"), "3일마다 @2026-11-23 | 화분 물");
        Check("읽은 반복은 남기기", (P("매월 25일 정산").IsRoutine, P("매월 25일 정산").Repeat!.Sticky, P("매월 25일 정산").Days.Count), (true, true, 0));
        Check("반복 루틴엔 마감일이 없음", P("매월 25일 정산").Due, (DateOnly?)null);
        Check("시각도 같이", (P("매월 25일 오후 3시 정산").DueTime, P("매월 25일 오후 3시 정산").Title), ((TimeOnly?)new TimeOnly(15, 0), "정산"));
        Check("중요도 표시는 그대로 읽고 뗀다", R("매월 25일 정산 !1"), "매월 25일 @2026-11-25 | 정산");
        Check("날짜 뒤의 '3일간'은 제목", R("매월 10일 3일간 출장 준비"), "매월 10일 @2026-12-10 | 3일간 출장 준비");

        // Review Focus 4: 제목 속 낱말
        Check("매월당은 제목", R("매월당 방문"), "없음 | 매월당 방문");
        Check("격주간지는 제목", R("격주간지 읽기"), "없음 | 격주간지 읽기");
        Check("붙여 쓴 '3일마다가스카'는 제목", R("3일마다가스카 여행"), "없음 | 3일마다가스카 여행");
        Check("앞에 붙은 글자", R("격매월 25일 정산").StartsWith("없음"), true);
        Check("격주 뒤의 '일정'은 요일이 아님", R("격주 일정 확인"), "격주 월 @2026-11-23 | 일정 확인");
        Check("없는 날짜는 반복으로 읽지 않음", R("매월 45일 정산").StartsWith("없음"), true);
        Check("1일마다 · 0개월마다는 반복 아님", (R("1일마다 물").StartsWith("없음"), R("0개월마다 10일 점검").StartsWith("없음")), (true, true));

        // 지금 것은 그대로
        Check("매주는 옛 방식 그대로", (P("매주 월수금 운동").Repeat, P("매주 월수금 운동").Days.Count), ((RepeatRule?)null, 3));
        Check("매일도 그대로", (P("매일 물 2L").Repeat, P("매일 물 2L").IsRoutine, P("매일 물 2L").Title), ((RepeatRule?)null, true, "물 2L"));
        Check("그냥 25일은 지금처럼 마감일", (P("25일 정산").IsRoutine, P("25일 정산").Due), (false, (DateOnly?)D(2026, 11, 25)));
        Check("루틴 탭에서 적어도 문장이 이긴다",
            QuickAddParser.Parse("매월 25일 정산", D(2026, 11, 23), assumeRoutine: true).Repeat is { } inTab ? RepeatEngine.Describe(inTab) : "없음", "매월 25일");

        // 만들기
        var made = ItemFactory.CreateRoutine(P("매월 25일 오후 3시 정산"), "정산", 4);
        Check("문장으로 만든 반복 루틴", (made.Archived, RepeatEngine.Describe(made.Repeat!), made.Days.Count, made.Time, made.Remind, made.Order),
            (true, "매월 25일", 0, (TimeOnly?)new TimeOnly(15, 0), true, 4));
        var plainMade = ItemFactory.CreateRoutine(P("매주 월수금 운동"), "운동", 0);
        Check("옛 방식 문장은 지금처럼", (plainMade.Archived, plainMade.Repeat, plainMade.Days.Count), (false, (RepeatRule?)null, 3));

        // 옮기기
        var task = new TaskItem { Title = "정산", Priority = Priority.High, Due = D(2026, 11, 20), DueTime = new TimeOnly(15, 0), Remind = true, MemoId = Guid.NewGuid(), CarryOverCount = 3 };
        var moved = ItemFactory.ToRoutine(task, P("매월 25일 x").Repeat!, 7);
        Check("할 일 → 루틴", (moved.Id == task.Id, moved.Title, moved.MemoId == task.MemoId, moved.Time, moved.Remind, moved.Archived, moved.Order),
            (true, "정산", true, (TimeOnly?)new TimeOnly(15, 0), true, true, 7));
        Check("옮긴 루틴의 반복", (RepeatEngine.Describe(moved.Repeat!), moved.Repeat!.Sticky, moved.Streak, moved.DoneToday), ("매월 25일", true, 0, false));
        Check("시각 없는 할 일은 알림도 없음", ItemFactory.ToRoutine(new TaskItem { Title = "물", Remind = true }, P("매월 25일 x").Repeat!, 0).Remind, false);
        var simple = ItemFactory.ToRoutine(new TaskItem { Title = "물" }, RepeatEngine.FromLegacy(new[] { DayOfWeek.Monday }), 0);
        Check("옛 방식으로 옮기면 파일 대상 아님", (simple.Archived, simple.Repeat, simple.Days.Count), (false, (RepeatRule?)null, 1));
        var everyDay = ItemFactory.ToRoutine(new TaskItem { Title = "물" }, new RepeatRule { Unit = RepeatUnit.Daily }, 0);
        Check("매일로 옮기기", (everyDay.Archived, everyDay.Repeat, everyDay.Days.Count), (false, (RepeatRule?)null, 0));

        var back = ItemFactory.ToTask(moved, D(2026, 11, 23), 2);
        Check("루틴 → 할 일(시각 있음)", (back.Id == moved.Id, back.Due, back.DueTime, back.Remind, back.MemoId == moved.MemoId),
            (true, (DateOnly?)D(2026, 11, 23), (TimeOnly?)new TimeOnly(15, 0), true, true));
        Check("옮긴 할 일의 나머지", (back.Title, back.CreatedDate, back.Order, back.Done, back.CarryOverCount), ("정산", D(2026, 11, 23), 2, false, 0));
        Check("루틴 → 할 일(시각 없음)", ItemFactory.ToTask(new Routine { Title = "물" }, D(2026, 11, 23), 0).Due, (DateOnly?)null);

        // 반복 바꾸기
        var r5 = new Routine { Streak = 5, StreakSnapshot = 5, Days = { DayOfWeek.Monday } };
        var choice = new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 1 }, Sticky = true, Anchor = D(2026, 11, 23) };
        ItemFactory.SetRepeat(r5, choice);
        Check("남기기만 켜면 기록 그대로", (r5.Streak, r5.Archived), (5, true));
        choice.Days.Add(3);
        Check("고른 규칙을 뒤에 고쳐도 루틴은 그대로", r5.Repeat!.Days.Count, 1);
        ItemFactory.SetRepeat(r5, P("매월 25일 x").Repeat!);
        Check("일정을 바꾸면 기록 0", (r5.Streak, r5.StreakSnapshot), (0, 0));
        r5.Streak = 3;
        ItemFactory.SetRepeat(r5, RepeatEngine.FromLegacy(new[] { DayOfWeek.Tuesday }));
        Check("옛 방식으로 바꾸면 Archived 가 풀림", (r5.Archived, r5.Repeat, string.Join(",", r5.Days), r5.Streak), (false, (RepeatRule?)null, "Tuesday", 0));
        var lostRule = new Routine { Archived = true, Streak = 2 };
        ItemFactory.SetRepeat(lostRule, P("매월 25일 x").Repeat!);
        Check("반복 정보 없던 루틴에 다시 정하기", (DayEngine.HasRule(lostRule), lostRule.Streak), (true, 0));
    }

    // ───────────────────────── 반복 · 폰 연동

    private static void RepeatPhoneTests()
    {
        var day28 = D(2026, 11, 28);

        AppData Fresh(out Routine pay, out Routine water, out Routine habit)
        {
            var data = new AppData { LastLogicalDate = day28 };
            pay = new Routine { Title = "정산", Archived = true, Repeat = M25(), Streak = 7, Order = 0 };
            water = new Routine { Title = "물", Order = 1, Streak = 3 };
            habit = new Routine { Title = "지나가는 매월", Archived = true, Order = 2,
                Repeat = new RepeatRule { Unit = RepeatUnit.MonthDates, MonthDays = { 25 }, Anchor = D(2026, 11, 25) } };
            data.Routines.AddRange(new[] { pay, water, habit });
            data.Routines.Add(new Routine { Title = "규칙 없음", Archived = true, Order = 3 });
            return data;
        }

        // ── 화면: 28일(밀림 3일)
        {
            var data = Fresh(out var pay, out var water, out _);
            var view = PhoneLinkEngine.BuildView(data, day28, day28.ToDateTime(new TimeOnly(9, 0)));
            Check("화면의 루틴: 남는 것과 매일만", string.Join(",", view.Routines.Select(r => r.Title)), "정산,물");
            var item = view.Routines.First(r => r.Id == pay.Id);
            Check("남는 항목의 밀림 · 남기기 · 연속 기록", (item.Overdue, item.Sticky, item.Streak, item.Done), (3, true, 0, false));
            var plain = view.Routines.First(r => r.Id == water.Id);
            Check("매일 루틴은 지금 그대로", (plain.Overdue, plain.Sticky, plain.Streak), (0, false, 3));

            var json = JsonSerializer.Serialize(view, AppJsonContext.Default.PhoneView);
            Check("화면 파일에 두 칸이 적힘", (json.Contains("\"Overdue\": 3"), json.Contains("\"Sticky\": true")), (true, true));
        }

        // ── 체크: repeat-done / repeat-undone
        {
            var data = Fresh(out var pay, out _, out _);
            var done = Op(PhoneOp.RepeatDone, day28, 10, target: pay.Id);
            var result = PhoneLinkEngine.Apply(data, [done]);
            Check("남는 항목 체크", (result.Checked, pay.DoneToday, pay.LastDoneDate, pay.Streak), (1, true, (DateOnly?)day28, 7));
            Check("반영 기록에 들어감", data.PhoneOpsApplied.Any(a => a.Id == done.Id), true);
            var after = PhoneLinkEngine.BuildView(data, day28, day28.ToDateTime(new TimeOnly(10, 1))).Routines.First(r => r.Id == pay.Id);
            Check("체크한 날엔 체크된 채 화면에 남음", (after.Done, after.Overdue), (true, 0));

            var undone = Op(PhoneOp.RepeatUndone, day28, 10, 5, target: pay.Id);
            var undo = PhoneLinkEngine.Apply(data, [done, undone]);
            Check("체크 풀기", (undo.Unchecked, pay.DoneToday, DayEngine.OverdueDays(pay, day28)), (1, false, 3));
        }

        // ── 옛 갈래(done)로 와도 받는다
        {
            var data = Fresh(out var pay, out _, out var habit);
            var result = PhoneLinkEngine.Apply(data, [Op(PhoneOp.Done, day28, 10, target: pay.Id), Op(PhoneOp.Done, day28, 10, 1, target: habit.Id)]);
            Check("done 으로 온 남는 항목", (pay.DoneToday, result.Checked), (true, 1));
            Check("차례가 아닌 지나가는 루틴은 건너뜀", (habit.DoneToday, result.Skipped), (false, 1));
        }

        // ── PC 가 며칠 꺼져 있던 사이 폰에서 끝낸 남는 항목
        {
            var data = Fresh(out var pay, out var water, out _);
            data.History.Add(new DayRecord { Date = D(2026, 11, 26), RoutinesScheduled = 1, RoutinesDone = 0 });
            var late = Op(PhoneOp.RepeatDone, D(2026, 11, 26), 20, target: pay.Id);
            var lateWater = Op(PhoneOp.Done, D(2026, 11, 26), 20, 1, target: water.Id);
            var result = PhoneLinkEngine.Apply(data, [late, lateWater]);
            Check("지난 날 끝낸 남는 항목은 받는다", (result.Checked, pay.LastDoneDate, pay.DoneToday), (1, (DateOnly?)D(2026, 11, 26), false));
            Check("그래서 더는 남지 않는다", DayEngine.IsDue(pay, day28), false);
            var record = data.History.First(h => h.Date == D(2026, 11, 26));
            Check("끝낸 날의 기록에 든다", (record.RoutinesScheduled, record.RoutinesDone), (2, 1));
            Check("지난 날 매일 루틴 체크는 지금처럼 건너뜀", (water.DoneToday, result.Skipped), (false, 1));

            // 그 뒤에 온 차례(12/25)는 다시 남는다
            Check("다음 차례는 그대로 온다", DayEngine.IsDue(pay, D(2026, 12, 25)), true);

            // 지난 날의 '풀기'는 받지 않는다
            var lateUndo = PhoneLinkEngine.Apply(data, [Op(PhoneOp.RepeatUndone, D(2026, 11, 26), 21, target: pay.Id)]);
            Check("지난 날 풀기는 건너뜀", (lateUndo.Skipped, pay.LastDoneDate), (1, (DateOnly?)D(2026, 11, 26)));

            // 더 뒤에 PC 에서 끝낸 기록을 지난 날 체크가 되돌리지 않는다
            pay.LastDoneDate = D(2026, 11, 27);
            PhoneLinkEngine.Apply(data, [Op(PhoneOp.RepeatDone, D(2026, 11, 26), 22, target: pay.Id)]);
            Check("더 늦은 완료일을 앞당기지 않음", pay.LastDoneDate, (DateOnly?)D(2026, 11, 27));
        }

        // ── 추가: add-repeat
        {
            var data = new AppData { LastLogicalDate = D(2026, 11, 23) };
            var add = Op(PhoneOp.AddRepeat, D(2026, 11, 23), 9, text: "매월 25일 정산");
            var result = PhoneLinkEngine.Apply(data, [add]);
            var routine = data.Routines.SingleOrDefault();
            Check("add-repeat 는 루틴 하나", (result.Added, data.Routines.Count, data.Tasks.Count), (1, 1, 0));
            Check("반복 루틴으로", (routine?.Id, routine?.Title, routine?.Archived, routine?.Repeat is { } r ? RepeatEngine.Describe(r) : ""),
                ((Guid?)add.Id, "정산", (bool?)true, "매월 25일"));

            data.PhoneOpsApplied.Clear();
            PhoneLinkEngine.Apply(data, [add]);
            Check("같은 일이 두 번 와도 하나", data.Routines.Count, 1);

            // add 로 와도(옛 폰 화면) 새 PC 는 같은 규칙으로 읽는다
            var viaAdd = new AppData { LastLogicalDate = D(2026, 11, 23) };
            PhoneLinkEngine.Apply(viaAdd, [Op(PhoneOp.Add, D(2026, 11, 23), 9, text: "격주 월요일 회의")]);
            Check("add 로 온 반복 문장", viaAdd.Routines.SingleOrDefault()?.Repeat is { } w ? RepeatEngine.Describe(w) : "", "격주 월");

            // 반복 말이 없는 글이 add-repeat 로 와도 죽지 않고 할 일이 된다
            var odd = new AppData { LastLogicalDate = D(2026, 11, 23) };
            PhoneLinkEngine.Apply(odd, [Op(PhoneOp.AddRepeat, D(2026, 11, 23), 9, text: "내일 회의")]);
            Check("반복 말 없는 add-repeat", (odd.Tasks.Count, odd.Routines.Count), (1, 0));
        }

        // ── 모르는 갈래는 지금처럼
        {
            var data = Fresh(out var pay, out _, out _);
            var result = PhoneLinkEngine.Apply(data, [Op("repeat-done-x", day28, 10, target: pay.Id)]);
            Check("모르는 갈래는 넣지도 적지도 않음", (result.NeedsSave, data.PhoneOpsApplied.Count, pay.DoneToday), (false, 0, false));
        }
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
