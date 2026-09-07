using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Flow.Models;
using Flow.Services;

namespace Flow.Tests;

/// <summary>
/// 롤오버 엔진과 빠른 추가 파서의 자체 검증.
/// 시간을 인자로 주입하므로 시스템 시계를 건드리지 않고 며칠치를 시뮬레이션할 수 있다.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 파서가 문장을 어떻게 읽는지 바로 확인하는 용도
        //   dotnet run --project Tests/Flow.Tests -- parse "오전 11시 회의"
        if (args.Length >= 2 && args[0] == "parse")
        {
            Explain(string.Join(' ', args[1..]));
            return 0;
        }

        Section("논리적 날짜");
        LogicalDateTests();

        Section("루틴 자동 초기화 · 스트릭");
        RolloverTests();

        Section("할 일 이월 · 정리");
        CarryOverTests();

        Section("빠른 추가 파서");
        ParserTests();

        Section("저장 · 직렬화");
        SerializationTests();

        Console.WriteLine();
        Console.WriteLine($"통과 {_passed} · 실패 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ───────────────────────── 논리적 날짜

    private static void LogicalDateTests()
    {
        Check("새벽 2시는 아직 전날",
            DayEngine.LogicalDate(new DateTime(2026, 9, 8, 2, 0, 0), 4),
            new DateOnly(2026, 9, 7));

        Check("오전 4시부터 새 날",
            DayEngine.LogicalDate(new DateTime(2026, 9, 8, 4, 0, 0), 4),
            new DateOnly(2026, 9, 8));

        Check("자정 기준이면 00시에 넘어감",
            DayEngine.LogicalDate(new DateTime(2026, 9, 8, 0, 30, 0), 0),
            new DateOnly(2026, 9, 8));

        Check("다음 롤오버 시각",
            DayEngine.NextRolloverAt(new DateTime(2026, 9, 7, 23, 0, 0), 4),
            new DateTime(2026, 9, 8, 4, 0, 0));

        Check("이미 지났으면 내일로",
            DayEngine.NextRolloverAt(new DateTime(2026, 9, 7, 5, 0, 0), 4),
            new DateTime(2026, 9, 8, 4, 0, 0));
    }

    // ───────────────────────── 롤오버

    private static void RolloverTests()
    {
        // 하루 지남: 체크 해제 + 스트릭 유지 + 기록 남음
        var yesterday = new DateOnly(2026, 9, 7);
        var today = new DateOnly(2026, 9, 8);

        var data = Seed(yesterday);
        var routine = data.Routines[0];
        DayEngine.CompleteRoutine(routine, yesterday);

        Check("체크 시 스트릭 1", routine.Streak, 1);
        Check("체크 상태", routine.DoneToday, true);

        var result = DayEngine.Rollover(data, today);

        Check("롤오버 발생", result.Changed, true);
        Check("경과 1일", result.DaysElapsed, 1);
        Check("체크가 자동으로 풀림", routine.DoneToday, false);
        Check("연속 기록은 유지", routine.Streak, 1);
        Check("어제 기록 저장됨", data.History.Any(h => h.Date == yesterday && h.RoutinesDone == 1), true);
        Check("기준일 갱신", data.LastLogicalDate, today);

        // 이어서 오늘도 체크하면 스트릭 2
        DayEngine.CompleteRoutine(routine, today);
        Check("연속 달성 시 스트릭 2", routine.Streak, 2);

        // 체크 해제는 정확히 되돌아감
        DayEngine.UncompleteRoutine(routine);
        Check("해제 시 스트릭 복원", routine.Streak, 1);
        Check("해제 시 마지막 완료일 복원", routine.LastDoneDate, yesterday);

        // PC를 3일간 꺼둔 경우
        var away = Seed(new DateOnly(2026, 9, 7));
        var awayRoutine = away.Routines[0];
        DayEngine.CompleteRoutine(awayRoutine, new DateOnly(2026, 9, 7));

        var backResult = DayEngine.Rollover(away, new DateOnly(2026, 9, 11));

        Check("4일치 한 번에 정산", backResult.DaysElapsed, 4);
        Check("빠진 날이 있으면 스트릭 0", awayRoutine.Streak, 0);
        Check("꺼져 있던 날도 기록됨", away.History.Count(h => h.Date >= new DateOnly(2026, 9, 7)
                                                          && h.Date <= new DateOnly(2026, 9, 10)), 4);
        Check("체크 해제", awayRoutine.DoneToday, false);

        // 요일 지정 루틴: 금요일에 하고 다음 예정일인 월요일에 하면 연속
        var weekly = new AppData { LastLogicalDate = new DateOnly(2026, 9, 11) };  // 금요일
        var mwf = new Routine
        {
            Title = "운동",
            Days = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]
        };
        weekly.Routines.Add(mwf);

        Check("금요일은 예정일", DayEngine.IsScheduled(mwf, new DateOnly(2026, 9, 11)), true);
        Check("토요일은 예정일 아님", DayEngine.IsScheduled(mwf, new DateOnly(2026, 9, 12)), false);

        DayEngine.CompleteRoutine(mwf, new DateOnly(2026, 9, 11));
        Check("금요일 스트릭 1", mwf.Streak, 1);

        DayEngine.Rollover(weekly, new DateOnly(2026, 9, 14));   // 월요일
        Check("주말을 건너뛰어도 스트릭 유지", mwf.Streak, 1);

        DayEngine.CompleteRoutine(mwf, new DateOnly(2026, 9, 14));
        Check("다음 예정일 달성 시 스트릭 2", mwf.Streak, 2);

        // 예정일을 빼먹으면 끊긴다
        DayEngine.Rollover(weekly, new DateOnly(2026, 9, 17));   // 목요일 (수요일을 빼먹음)
        Check("예정일을 빼먹으면 스트릭 0", mwf.Streak, 0);
    }

    // ───────────────────────── 이월

    private static void CarryOverTests()
    {
        var today = new DateOnly(2026, 9, 10);

        var data = Seed(new DateOnly(2026, 9, 8));
        data.Tasks.Add(new TaskItem { Title = "밀린 일", Due = new DateOnly(2026, 9, 8) });
        data.Tasks.Add(new TaskItem { Title = "먼 일", Due = new DateOnly(2026, 9, 20) });
        data.Tasks.Add(new TaskItem { Title = "언젠가" });

        DayEngine.Rollover(data, today);

        var moved = data.Tasks.First(t => t.Title == "밀린 일");
        Check("미완료 할 일이 오늘로 이월", moved.Due, today);
        Check("밀린 일수 누적", moved.CarryOverCount, 2);
        Check("미래 마감은 그대로", data.Tasks.First(t => t.Title == "먼 일").Due, new DateOnly(2026, 9, 20));
        Check("마감 없는 일은 그대로", data.Tasks.First(t => t.Title == "언젠가").Due, null);

        // 이월 끄기
        var noCarry = Seed(new DateOnly(2026, 9, 8));
        noCarry.Settings.CarryOverIncomplete = false;
        noCarry.Tasks.Add(new TaskItem { Title = "밀린 일", Due = new DateOnly(2026, 9, 8) });

        DayEngine.Rollover(noCarry, today);
        Check("이월 끄면 마감일 유지", noCarry.Tasks[0].Due, new DateOnly(2026, 9, 8));

        // 오래된 완료 항목 정리
        var purge = Seed(new DateOnly(2026, 9, 8));
        purge.Settings.CompletedRetentionDays = 7;
        purge.Tasks.Add(new TaskItem { Title = "옛날에 끝냄", Done = true, CompletedDate = new DateOnly(2026, 8, 1) });
        purge.Tasks.Add(new TaskItem { Title = "어제 끝냄", Done = true, CompletedDate = new DateOnly(2026, 9, 8) });

        DayEngine.Rollover(purge, today);
        Check("보관 기간 지난 완료 항목 정리", purge.Tasks.Any(t => t.Title == "옛날에 끝냄"), false);
        Check("최근 완료 항목은 유지", purge.Tasks.Any(t => t.Title == "어제 끝냄"), true);

        // 같은 날 두 번 호출해도 아무 일도 없어야 한다
        var idem = Seed(today);
        var before = idem.Routines[0].Streak;
        var again = DayEngine.Rollover(idem, today);
        Check("같은 날 재호출은 무시", again.Changed, false);
        Check("스트릭 변화 없음", idem.Routines[0].Streak, before);
    }

    // ───────────────────────── 파서

    private static void ParserTests()
    {
        var today = new DateOnly(2026, 9, 7);   // 월요일

        var a = QuickAddParser.Parse("내일 오후 3시 보고서 제출 !1", today);
        Check("[할 일] 제목", a.Title, "보고서 제출");
        Check("[할 일] 내일로 마감", a.Due, new DateOnly(2026, 9, 8));
        Check("[할 일] 오후 3시 → 15:00", a.DueTime, new TimeOnly(15, 0));
        Check("[할 일] !1 → 최우선", a.Priority, Priority.High);
        Check("[할 일] 루틴 아님", a.IsRoutine, false);

        var b = QuickAddParser.Parse("매주 월수금 운동 30분", today);
        Check("[루틴] 인식", b.IsRoutine, true);
        Check("[루틴] 요일 3개", b.Days.Count, 3);
        Check("[루틴] 월 포함", b.Days.Contains(DayOfWeek.Monday), true);
        Check("[루틴] 금 포함", b.Days.Contains(DayOfWeek.Friday), true);
        Check("[루틴] 화 미포함", b.Days.Contains(DayOfWeek.Tuesday), false);
        Check("[루틴] 제목", b.Title, "운동 30분");

        var c = QuickAddParser.Parse("매일 물 2L 마시기", today);
        Check("[매일] 루틴", c.IsRoutine, true);
        Check("[매일] 요일 지정 없음", c.Days.Count, 0);
        Check("[매일] 제목", c.Title, "물 2L 마시기");

        var d = QuickAddParser.Parse("평일 스탠드업", today);
        Check("[평일] 5일", d.Days.Count, 5);
        Check("[평일] 토요일 제외", d.Days.Contains(DayOfWeek.Saturday), false);

        var e = QuickAddParser.Parse("매주 월요일 주간보고", today);
        Check("[요일 표기] 월요일 1개", e.Days.Count, 1);
        Check("[요일 표기] 월요일", e.Days[0], DayOfWeek.Monday);
        Check("[요일 표기] 제목", e.Title, "주간보고");

        var f = QuickAddParser.Parse("9/15 세금 신고", today);
        Check("[날짜] 9/15", f.Due, new DateOnly(2026, 9, 15));
        Check("[날짜] 제목", f.Title, "세금 신고");

        var g = QuickAddParser.Parse("9월 20일 정산 마감 p2", today);
        Check("[날짜] 9월 20일", g.Due, new DateOnly(2026, 9, 20));
        Check("[날짜] p2 → 보통", g.Priority, Priority.Medium);
        Check("[날짜] 제목", g.Title, "정산 마감");

        var h = QuickAddParser.Parse("수요일 팀 회의", today);
        Check("[요일] 다음 수요일", h.Due, new DateOnly(2026, 9, 9));
        Check("[요일] 루틴 아님", h.IsRoutine, false);

        var i = QuickAddParser.Parse("14:30 고객 통화", today);
        Check("[시각] 14:30", i.DueTime, new TimeOnly(14, 30));
        Check("[시각] 시간만 주면 오늘로", i.Due, today);

        var j = QuickAddParser.Parse("사은품 리스트 발송", today);
        Check("[평문] 마감 없음", j.Due, null);
        Check("[평문] 제목 그대로", j.Title, "사은품 리스트 발송");
        Check("[평문] 우선순위 없음", j.Priority, Priority.None);

        var k = QuickAddParser.Parse("지난달 지난 날짜는 다음 달로 25일 결산", today);
        Check("[지난 날짜] 다음 달로", k.Due, new DateOnly(2026, 9, 25));

        // ── 루틴 탭에서 입력할 때 (assumeRoutine)
        var r1 = QuickAddParser.Parse("물 2L 마시기", today, assumeRoutine: true);
        Check("[루틴탭] 그냥 적어도 루틴", r1.IsRoutine, true);
        Check("[루틴탭] 요일 없으면 매일", r1.Days.Count, 0);
        Check("[루틴탭] 제목 보존", r1.Title, "물 2L 마시기");

        var r2 = QuickAddParser.Parse("월수금 운동 30분", today, assumeRoutine: true);
        Check("[루틴탭] 요일만 앞에 적어도 인식", r2.Days.Count, 3);
        Check("[루틴탭] 월 포함", r2.Days.Contains(DayOfWeek.Monday), true);
        Check("[루틴탭] 목 미포함", r2.Days.Contains(DayOfWeek.Thursday), false);
        Check("[루틴탭] 요일은 제목에서 제거", r2.Title, "운동 30분");

        var r3 = QuickAddParser.Parse("토요일 대청소", today, assumeRoutine: true);
        Check("[루틴탭] 토요일", r3.Days.Count, 1);
        Check("[루틴탭] 토요일 값", r3.Days[0], DayOfWeek.Saturday);

        // "일찍"의 '일'을 일요일로 오해하면 안 된다
        var r4 = QuickAddParser.Parse("일찍 자기", today, assumeRoutine: true);
        Check("[루틴탭] 일찍은 요일이 아님", r4.Days.Count, 0);
        Check("[루틴탭] 제목 그대로", r4.Title, "일찍 자기");

        var r5 = QuickAddParser.Parse("매주 화목 재고 점검", today, assumeRoutine: true);
        Check("[루틴탭] 명시한 요일이 우선", r5.Days.Count, 2);
        Check("[루틴탭] 화 포함", r5.Days.Contains(DayOfWeek.Tuesday), true);

        // 오늘/예정 탭에서는 그냥 적으면 할 일이어야 한다
        var r6 = QuickAddParser.Parse("물 2L 마시기", today);
        Check("[오늘탭] 그냥 적으면 할 일", r6.IsRoutine, false);

        // ── 요일 두 글자 이상은 어느 탭에서든 루틴
        var m1 = QuickAddParser.Parse("화목 재고 점검", today);
        Check("[연속요일] 루틴으로 인식", m1.IsRoutine, true);
        Check("[연속요일] 화·목 2일", m1.Days.Count, 2);
        Check("[연속요일] 화 포함", m1.Days.Contains(DayOfWeek.Tuesday), true);
        Check("[연속요일] 제목에서 요일 제거", m1.Title, "재고 점검");

        var m2 = QuickAddParser.Parse("토일 대청소", today);
        Check("[연속요일] 토·일", m2.Days.Count, 2);
        Check("[연속요일] 일요일 포함", m2.Days.Contains(DayOfWeek.Sunday), true);

        // 낱말 속에 우연히 들어간 요일 글자는 걸리면 안 된다
        var m3 = QuickAddParser.Parse("수목원 가기", today);
        Check("[오탐방지] 수목원은 루틴 아님", m3.IsRoutine, false);
        Check("[오탐방지] 제목 그대로", m3.Title, "수목원 가기");

        var m4 = QuickAddParser.Parse("금요일 정산", today);
        Check("[오탐방지] 금요일은 한 번뿐인 할 일", m4.IsRoutine, false);
        Check("[오탐방지] 다음 금요일", m4.Due, new DateOnly(2026, 9, 11));

        var m5 = QuickAddParser.Parse("일찍 자기", today);
        Check("[오탐방지] 일찍은 요일 아님", m5.IsRoutine, false);

        // ── 낱말 한가운데의 숫자를 훔쳐가면 안 된다
        // 사용자가 실제로 겪은 사례: 제목에서 "12시"만 뜯겨 "11~ 사이"가 됐다
        const string range = "AK넷 일별 정책서 반영하여 배포 오전 11~12시 사이";
        var b1 = QuickAddParser.Parse(range, today);
        Check("[낱말경계] 시간 범위는 건드리지 않음", b1.DueTime, null);
        Check("[낱말경계] 제목이 그대로 남음", b1.Title, range);

        var b2 = QuickAddParser.Parse(range, today, assumeRoutine: true);
        Check("[낱말경계] 루틴 탭에서도 제목 보존", b2.Title, range);

        var b3 = QuickAddParser.Parse("2시간 작업", today);
        Check("[낱말경계] 2시간의 '2시'를 떼가지 않음", b3.DueTime, null);
        Check("[낱말경계] 제목 그대로", b3.Title, "2시간 작업");

        var b4 = QuickAddParser.Parse("3일차 회고", today);
        Check("[낱말경계] 3일차를 날짜로 보지 않음", b4.Due, null);
        Check("[낱말경계] 제목 그대로", b4.Title, "3일차 회고");

        var b5 = QuickAddParser.Parse("일별 정책서 정리", today);
        Check("[낱말경계] 일별은 날짜가 아님", b5.Due, null);
        Check("[낱말경계] 제목 그대로", b5.Title, "일별 정책서 정리");

        var b6 = QuickAddParser.Parse("5분기 실적 정리", today);
        Check("[낱말경계] 분기를 시각으로 보지 않음", b6.DueTime, null);
        Check("[낱말경계] 제목 그대로", b6.Title, "5분기 실적 정리");

        // ── 조사가 붙어도 날짜·시간은 정상 인식
        var c1 = QuickAddParser.Parse("9월 20일에 계약 갱신", today);
        Check("[조사] 9월 20일에", c1.Due, new DateOnly(2026, 9, 20));
        Check("[조사] 제목에서 날짜 제거", c1.Title, "계약 갱신");

        var c2 = QuickAddParser.Parse("오후 3시에 거래처 미팅", today);
        Check("[조사] 오후 3시에", c2.DueTime, new TimeOnly(15, 0));
        Check("[조사] 제목", c2.Title, "거래처 미팅");

        var c3 = QuickAddParser.Parse("내일까지 정산 마감", today);
        Check("[조사] 내일까지", c3.Due, today.AddDays(1));
        Check("[조사] 제목", c3.Title, "정산 마감");

        // ── 기존 형태는 그대로 동작해야 한다
        var d1 = QuickAddParser.Parse("3시 반 미팅", today);
        Check("[유지] 3시 반", d1.DueTime, new TimeOnly(3, 30));
        Check("[유지] 제목", d1.Title, "미팅");
    }

    // ───────────────────────── 직렬화

    private static void SerializationTests()
    {
        // 기본 설정에는 저장된 창 위치가 없다. 여기에 NaN 같은 값이 들어가면
        // 직렬화 전체가 터지면서 저장이 통째로 실패한다.
        var fresh = new AppData();
        Check("기본 데이터 직렬화 성공", TrySerialize(fresh, out var freshJson), true);
        Check("창 위치는 null로 기록", freshJson.Contains("\"WindowLeft\": null"), true);

        var full = new AppData
        {
            LastLogicalDate = new DateOnly(2026, 9, 7),
            Routines =
            {
                new Routine
                {
                    Title = "운동",
                    Days = [DayOfWeek.Monday, DayOfWeek.Friday],
                    Streak = 5,
                    LastDoneDate = new DateOnly(2026, 9, 6)
                }
            },
            Tasks =
            {
                new TaskItem
                {
                    Title = "보고서",
                    Due = new DateOnly(2026, 9, 8),
                    DueTime = new TimeOnly(15, 30),
                    Priority = Priority.High
                }
            },
            History = { new DayRecord { Date = new DateOnly(2026, 9, 6), RoutinesScheduled = 3, RoutinesDone = 2 } }
        };
        full.Settings.WindowLeft = 1548;
        full.Settings.WindowTop = 48;

        Check("전체 데이터 직렬화 성공", TrySerialize(full, out var json), true);

        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppData)!;

        Check("기준일 왕복", back.LastLogicalDate, new DateOnly(2026, 9, 7));
        Check("루틴 요일 왕복", back.Routines[0].Days.Count, 2);
        Check("스트릭 왕복", back.Routines[0].Streak, 5);
        Check("마감 시각 왕복", back.Tasks[0].DueTime, new TimeOnly(15, 30));
        Check("우선순위 왕복", back.Tasks[0].Priority, Priority.High);
        Check("히스토리 왕복", back.History[0].RoutinesDone, 2);
        Check("창 위치 왕복", back.Settings.WindowLeft, 1548d);
        Check("우선순위는 문자열로 기록", json.Contains("\"High\""), true);
    }

    private static bool TrySerialize(AppData data, out string json)
    {
        try
        {
            json = JsonSerializer.Serialize(data, AppJsonContext.Default.AppData);
            return true;
        }
        catch (Exception)
        {
            json = "";
            return false;
        }
    }

    private static void Explain(string input)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var routineMode in new[] { false, true })
        {
            var r = QuickAddParser.Parse(input, today, routineMode);
            Console.WriteLine();
            Console.WriteLine(routineMode ? "── 루틴 탭에서 입력했을 때" : "── 오늘/예정 탭에서 입력했을 때");
            Console.WriteLine($"   입력   : {input}");
            Console.WriteLine($"   제목   : {r.Title}");
            Console.WriteLine($"   루틴?  : {r.IsRoutine}");
            Console.WriteLine($"   요일   : {(r.Days.Count == 0 ? "(없음)" : string.Join(",", r.Days))}");
            Console.WriteLine($"   마감   : {(r.Due?.ToString() ?? "(없음)")}");
            Console.WriteLine($"   시각   : {(r.DueTime?.ToString() ?? "(없음)")}");
            Console.WriteLine($"   우선   : {r.Priority}");
        }
    }

    // ───────────────────────── 헬퍼

    private static AppData Seed(DateOnly lastDate)
    {
        var data = new AppData { LastLogicalDate = lastDate };
        data.Routines.Add(new Routine { Title = "매일 루틴" });
        return data;
    }

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"── {name}");
    }

    private static void Check<T>(string label, T actual, T expected)
    {
        var ok = EqualityComparer<T>.Default.Equals(actual, expected);
        if (ok)
        {
            _passed++;
            Console.WriteLine($"   OK   {label}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"   FAIL {label}  기대={Fmt(expected)}  실제={Fmt(actual)}");
        }
    }

    private static string Fmt(object? value) => value switch
    {
        null => "(없음)",
        bool b => b ? "참" : "거짓",
        _ => value.ToString() ?? "(없음)"
    };
}
