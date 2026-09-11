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

        Section("할 일이 놓이는 화면");
        BucketTests();

        Section("알림");
        ReminderTests();

        Section("빠른 추가 파서");
        ParserTests();

        Section("단축키 조합");
        HotKeyTests();

        Section("기기별 상태 분리");
        DeviceStateTests();

        Section("저장 위치 옮기기");
        DataLocationTests();

        Section("저장 · 직렬화");
        SerializationTests();

        Section("백업 · 되돌리기");
        BackupTests();

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

        // ── 오전·오후를 안 적은 시각은 아직 오지 않은 쪽으로 읽는다.
        //    밤 9시에 "9시 22분"이라고 적은 사람은 아침 9시를 뜻하지 않았다.
        var night = new TimeOnly(21, 21);
        var morning = new TimeOnly(8, 0);

        Check("밤에 적은 맨 '9시 22분'은 오후로",
            QuickAddParser.Parse("9시 22분 테스트", today, now: night).DueTime, new TimeOnly(21, 22));
        Check("아침에 적은 맨 '9시 22분'은 그대로",
            QuickAddParser.Parse("9시 22분 테스트", today, now: morning).DueTime, new TimeOnly(9, 22));

        Check("오전이라고 적었으면 옮기지 않는다",
            QuickAddParser.Parse("오전 9시 22분 테스트", today, now: night).DueTime, new TimeOnly(9, 22));
        Check("오후라고 적었으면 그대로",
            QuickAddParser.Parse("오후 9시 22분 테스트", today, now: morning).DueTime, new TimeOnly(21, 22));
        Check("24시간 표기는 건드리지 않는다",
            QuickAddParser.Parse("09:22 테스트", today, now: night).DueTime, new TimeOnly(9, 22));

        // 날짜를 따로 적었으면 오늘이 아니므로 옮길 이유가 없다
        Check("내일 것은 옮기지 않는다",
            QuickAddParser.Parse("내일 9시 회의", today, now: night).DueTime, new TimeOnly(9, 0));

        // 루틴은 내일 아침에 다시 오므로 오전 9시도 멀쩡한 뜻이다
        Check("루틴은 옮기지 않는다",
            QuickAddParser.Parse("매일 9시 약 먹기", today, now: night).DueTime, new TimeOnly(9, 0));

        Check("12시는 애매하지 않으므로 그대로",
            QuickAddParser.Parse("12시 30분 점심", today, now: new TimeOnly(13, 0)).DueTime,
            new TimeOnly(12, 30));

        Check("지금 시각을 안 주면 적힌 그대로",
            QuickAddParser.Parse("9시 22분 테스트", today).DueTime, new TimeOnly(9, 22));

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
        // 예전에는 창 위치가 여기 실렸고, NaN 이 들어가 저장이 통째로 실패한 적이 있다.
        // 지금은 창 위치가 기기별 파일로 빠졌지만, 직렬화가 조용히 터지지 않는지는 계속 지킨다.
        var fresh = new AppData();
        Check("기본 데이터 직렬화 성공", TrySerialize(fresh, out var freshJson), true);
        Check("빈 데이터도 내용이 있다", freshJson.Contains("Settings"), true);

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
        Check("창 위치는 왕복하지 않는다 (기기별)", back.Settings.WindowLeft, (double?)null);
        Check("우선순위는 문자열로 기록", json.Contains("\"High\""), true);
    }

    private static void BackupTests()
    {
        // 이 테스트는 FLOW_DATA_DIR 로 지정된 임시 폴더에서만 돈다.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 백업 테스트 (FLOW_DATA_DIR 미지정)");
            return;
        }

        // 깨끗한 상태에서 시작
        if (System.IO.Directory.Exists(DataStore.Directory))
            System.IO.Directory.Delete(DataStore.Directory, true);

        using var store = new DataStore();

        // 1) 루틴 3개짜리 상태를 저장하고 백업
        var first = new AppData { LastLogicalDate = new DateOnly(2026, 9, 7) };
        first.Routines.Add(new Routine { Title = "가" });
        first.Routines.Add(new Routine { Title = "나" });
        first.Routines.Add(new Routine { Title = "다" });
        store.RequestSave(first);
        store.Flush();

        var madePath = BackupService.Create();
        Check("백업 파일이 만들어짐", madePath is not null && System.IO.File.Exists(madePath), true);

        var listed = BackupService.List();
        Check("목록에 1개", listed.Count, 1);
        Check("루틴 개수를 읽어냄", listed[0].Routines, 3);
        Check("할 일 개수를 읽어냄", listed[0].Tasks, 0);

        // 2) 내용을 바꾼다 (루틴 1개 + 할 일 2개)
        var second = new AppData { LastLogicalDate = new DateOnly(2026, 9, 7) };
        second.Routines.Add(new Routine { Title = "라" });
        second.Tasks.Add(new TaskItem { Title = "할일1" });
        second.Tasks.Add(new TaskItem { Title = "할일2" });
        store.RequestSave(second);
        store.Flush();

        var changed = store.Load();
        Check("바뀐 내용이 저장됨", changed.Routines.Count, 1);

        // 3) 되돌린다
        Check("되돌리기 성공", BackupService.Restore(listed[0].Path), true);

        var restored = store.Load();
        Check("루틴이 3개로 돌아옴", restored.Routines.Count, 3);
        Check("할 일이 사라짐", restored.Tasks.Count, 0);
        Check("첫 루틴 이름", restored.Routines[0].Title, "가");

        // 4) 되돌리기 직전 상태도 백업돼 있어야 한다
        var after = BackupService.List();
        Check("백업이 2개로 늘어남", after.Count, 2);

        var safety = after.FirstOrDefault(b => b.Tag == "before-restore");
        Check("되돌리기 직전 백업이 있음", safety is not null, true);
        Check("그 백업은 되돌리기 전 내용", safety!.Routines, 1);
        Check("그 백업의 할 일 2개", safety.Tasks, 2);

        // 5) 그걸로 다시 되돌리면 원상복구
        Check("다시 되돌리기", BackupService.Restore(safety.Path), true);
        var again = store.Load();
        Check("바뀐 내용으로 복귀", again.Tasks.Count, 2);
    }

    private static void BucketTests()
    {
        var today = new DateOnly(2026, 9, 8);

        static TaskItem Task(DateOnly? due = null, bool done = false, DateOnly? completed = null)
            => new() { Title = "무엇", Due = due, Done = done, CompletedDate = completed };

        // 날짜를 안 정한 것은 예정이 아니다. 예정으로 보내면 기본 화면에서 영영 안 보인다.
        Check("날짜 없음 → 오늘", DayEngine.BucketOf(Task(), today), TaskBucket.Today);
        Check("오늘 마감 → 오늘", DayEngine.BucketOf(Task(today), today), TaskBucket.Today);
        Check("어제 마감 → 오늘", DayEngine.BucketOf(Task(today.AddDays(-1)), today), TaskBucket.Today);
        Check("한참 지난 마감 → 오늘", DayEngine.BucketOf(Task(today.AddDays(-30)), today), TaskBucket.Today);

        Check("내일 마감 → 예정", DayEngine.BucketOf(Task(today.AddDays(1)), today), TaskBucket.Upcoming);
        Check("다음 달 마감 → 예정", DayEngine.BucketOf(Task(today.AddDays(30)), today), TaskBucket.Upcoming);

        Check("오늘 끝냄 → 완료됨",
            DayEngine.BucketOf(Task(today, done: true, completed: today), today), TaskBucket.DoneToday);
        Check("날짜 없이 오늘 끝냄 → 완료됨",
            DayEngine.BucketOf(Task(done: true, completed: today), today), TaskBucket.DoneToday);
        Check("어제 끝냄 → 보관",
            DayEngine.BucketOf(Task(today, done: true, completed: today.AddDays(-1)), today), TaskBucket.Archived);
        Check("내일 마감이어도 끝냈으면 완료됨",
            DayEngine.BucketOf(Task(today.AddDays(1), done: true, completed: today), today), TaskBucket.DoneToday);

        // 날짜가 없으면 이월도 없다. 정한 적 없는 마감을 놓쳤다고 셀 수는 없다.
        var data = new AppData { LastLogicalDate = today.AddDays(-3) };
        data.Settings.CarryOverIncomplete = true;
        var loose = Task();
        var dated = Task(today.AddDays(-3));
        data.Tasks.Add(loose);
        data.Tasks.Add(dated);

        DayEngine.Rollover(data, today);

        Check("날짜 없는 것은 밀린 일수가 안 붙음", loose.CarryOverCount, 0);
        Check("날짜 없는 것은 날짜가 안 생김", loose.Due.HasValue, false);
        Check("날짜 없어도 오늘에 그대로 있음", DayEngine.BucketOf(loose, today), TaskBucket.Today);
        Check("날짜 있는 것은 이월됨", dated.CarryOverCount, 3);
    }

    private static void ReminderTests()
    {
        var today = new DateOnly(2026, 9, 8);          // 화요일
        const int Hour = 4;                            // 하루 시작 04시

        // ── 논리적 날짜 ↔ 시각 뒤집기
        Check("09시는 같은 날",
            DayEngine.AtLogicalTime(today, new TimeOnly(9, 0), Hour), today.ToDateTime(new TimeOnly(9, 0)));
        Check("02시는 다음 달력일",
            DayEngine.AtLogicalTime(today, new TimeOnly(2, 0), Hour),
            today.AddDays(1).ToDateTime(new TimeOnly(2, 0)));
        Check("03:59는 다음 날",
            DayEngine.AtLogicalTime(today, new TimeOnly(3, 59), Hour).Day, 9);
        Check("04:00은 같은 날",
            DayEngine.AtLogicalTime(today, new TimeOnly(4, 0), Hour).Day, 8);
        Check("하루 시작이 0시면 그냥 그 날",
            DayEngine.AtLogicalTime(today, new TimeOnly(2, 0), 0).Day, 8);

        // 왕복 불변식 — 어떤 시각이든 다시 논리적 날짜로 되돌아와야 한다
        var roundTripOk = true;
        for (var h = 0; h < 24; h++)
        {
            var moment = DayEngine.AtLogicalTime(today, new TimeOnly(h, 30), Hour);
            if (DayEngine.LogicalDate(moment, Hour) != today) roundTripOk = false;
        }
        Check("24시간 전부 왕복이 맞음", roundTripOk, true);

        // ── 언제 울리는가
        Check("정시에 1건", Pending(Data(RoutineAt(9, 0)), today, 9, 0).Count, 1);
        Check("1분 전에는 0건", Pending(Data(RoutineAt(9, 0)), today, 8, 59).Count, 0);

        var handled = Data(RoutineAt(9, 0));
        handled.Routines[0].RemindHandled = today;
        Check("이미 처리한 몫은 0건", Pending(handled, today, 9, 30).Count, 0);

        var done = Data(RoutineAt(9, 0));
        done.Routines[0].DoneToday = true;
        Check("오늘 해낸 루틴은 0건", Pending(done, today, 9, 30).Count, 0);

        var noRemind = Data(RoutineAt(9, 0));
        noRemind.Routines[0].Remind = false;
        Check("알림 끈 루틴은 0건", Pending(noRemind, today, 9, 30).Count, 0);

        var noTime = Data(RoutineAt(9, 0));
        noTime.Routines[0].Time = null;
        Check("시각 없는 루틴은 0건", Pending(noTime, today, 9, 30).Count, 0);

        var off = Data(RoutineAt(9, 0));
        off.Settings.RemindersEnabled = false;
        Check("전체 스위치를 끄면 0건", Pending(off, today, 9, 30).Count, 0);

        // 요일 루틴은 예정된 날에만
        var mwf = Data(RoutineAt(9, 0));
        mwf.Routines[0].Days = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday];
        Check("월수금 루틴은 화요일에 0건", Pending(mwf, today, 9, 30).Count, 0);
        Check("월수금 루틴은 수요일에 1건", Pending(mwf, today.AddDays(1), 9, 30).Count, 1);

        // ── 할 일
        Check("마감 시각에 1건", Pending(Data(TaskAt(today, 15, 0)), today, 15, 0).Count, 1);
        Check("다른 날 마감은 0건", Pending(Data(TaskAt(today.AddDays(1), 15, 0)), today, 15, 30).Count, 0);

        var doneTask = Data(TaskAt(today, 15, 0));
        doneTask.Tasks[0].Done = true;
        Check("끝낸 할 일은 0건", Pending(doneTask, today, 15, 30).Count, 0);

        var noDueTime = Data(TaskAt(today, 15, 0));
        noDueTime.Tasks[0].DueTime = null;
        Check("시각 없는 할 일은 0건", Pending(noDueTime, today, 15, 30).Count, 0);

        // 기본값 false 덕분에 예전 항목은 저절로 조용하다 — 마이그레이션이 필요 없는 이유
        var legacy = Data(TaskAt(today, 15, 0));
        legacy.Tasks[0].Remind = false;
        Check("업데이트 전에 만든 항목은 안 울림", Pending(legacy, today, 15, 30).Count, 0);

        // ── 유예 경계. 넘긴 것은 목록에 아예 안 나온다.
        Check("59분 지각은 알림", Pending(Data(RoutineAt(9, 0)), today, 9, 59).Count, 1);
        Check("60분 정각은 아직 알림", Pending(Data(RoutineAt(9, 0)), today, 10, 0).Count, 1);
        Check("61분 지각은 조용히 지나감", Pending(Data(RoutineAt(9, 0)), today, 10, 1).Count, 0);
        Check("지각 분수를 세어 줌", Pending(Data(RoutineAt(9, 0)), today, 9, 25)[0].LateMinutes, 25);

        var noGrace = Data(RoutineAt(9, 0));
        noGrace.Settings.MissedGraceMinutes = 0;
        Check("유예 0이면 정시에만", Pending(noGrace, today, 9, 0).Count, 1);
        Check("유예 0이면 1분만 늦어도 지나감", Pending(noGrace, today, 9, 1).Count, 0);

        // 매일 루틴이 '어제 몫'으로 매 틱 걸리던 버그를 막아 둔다
        Check("어제 몫이 덤으로 딸려오지 않음", Pending(Data(RoutineAt(9, 0)), today, 9, 0).Count, 1);

        // ── 04시 경계: 03:50 알림은 04:05 에 '어제 몫'으로 나온다
        var dawn = Data(RoutineAt(3, 50));
        var pendingDawn = Pending(dawn, today, 4, 5);
        Check("새벽 알림이 하루 넘어간 직후에도 잡힘", pendingDawn.Count, 1);
        Check("그 몫은 어제 날짜로 기록됨", pendingDawn[0].ForDate, today.AddDays(-1));

        ReminderEngine.MarkHandled(dawn, pendingDawn[0]);
        Check("처리하면 어제로 적힘", dawn.Routines[0].RemindHandled, today.AddDays(-1));
        Check("처리 뒤에는 0건", Pending(dawn, today, 4, 6).Count, 0);

        // ── 며칠 꺼 뒀다 켰을 때 밀린 알림이 쏟아지지 않는다
        Check("11시간 뒤에 켜면 아무것도 안 나옴", Pending(Data(RoutineAt(9, 0)), today, 20, 0).Count, 0);

        // ── 미루기
        var snoozed = Data(RoutineAt(9, 0));
        var at930 = At(today, 9, 30);
        Check("미루기 성공", ReminderEngine.Snooze(snoozed, snoozed.Routines[0].Id, true, at930, 10), true);
        Check("미룬 직후에는 0건", Pending(snoozed, today, 9, 35).Count, 0);
        Check("원래 시각으로 다시 나오지 않음", snoozed.Routines[0].RemindHandled, today);

        var back = Pending(snoozed, today, 9, 40);
        Check("10분 뒤에 다시 1건", back.Count, 1);
        ReminderEngine.MarkHandled(snoozed, back[0]);
        Check("나온 뒤에는 미루기가 비워짐", snoozed.Routines[0].RemindSnoozedUntil, (DateTime?)null);

        // 자는 사이 미루기가 한참 지났으면 조용히 사라진다
        var stale = Data(RoutineAt(9, 0));
        ReminderEngine.Snooze(stale, stale.Routines[0].Id, true, at930, 10);
        Check("죽은 미루기를 치움", ReminderEngine.ClearStaleSnoozes(stale, At(today, 14, 0)), 1);
        Check("치운 뒤 0건", Pending(stale, today, 14, 1).Count, 0);

        var fresh = Data(RoutineAt(9, 0));
        ReminderEngine.Snooze(fresh, fresh.Routines[0].Id, true, at930, 10);
        Check("살아 있는 미루기는 안 치움", ReminderEngine.ClearStaleSnoozes(fresh, At(today, 9, 45)), 0);

        // 어제 미뤄 두고 치우지 않은 채 하루가 넘어가도 오늘 알림은 울려야 한다
        var carried = Data(RoutineAt(9, 0));
        ReminderEngine.Snooze(carried, carried.Routines[0].Id, true, at930, 10);
        Check("지나간 미루기가 다음 날을 막지 않음",
            Pending(carried, today.AddDays(1), 9, 0).Count, 1);

        // ── 다음 알림 시각
        Check("알림이 없으면 null", ReminderEngine.NextAt(Data(), At(today, 9, 0)), (DateTime?)null);
        Check("오늘 남은 것", ReminderEngine.NextAt(Data(RoutineAt(9, 0)), At(today, 8, 0)),
            (DateTime?)At(today, 9, 0));
        Check("오늘 지났으면 내일 것", ReminderEngine.NextAt(Data(RoutineAt(9, 0)), At(today, 10, 0)),
            (DateTime?)At(today.AddDays(1), 9, 0));

        // ── 같은 틱을 두 번 돌려도 한 번만
        var twice = Data(RoutineAt(9, 0));
        var first = Pending(twice, today, 9, 0);
        foreach (var p in first) ReminderEngine.MarkHandled(twice, p);
        Check("두 번째 틱에서는 0건", Pending(twice, today, 9, 0).Count, 0);
    }

    // ── 시험용 도우미

    private static DateTime At(DateOnly date, int hour, int minute)
        => date.ToDateTime(new TimeOnly(hour, minute));

    private static IReadOnlyList<PendingReminder> Pending(AppData data, DateOnly date, int hour, int minute)
        => ReminderEngine.Pending(data, At(date, hour, minute));

    private static Routine RoutineAt(int hour, int minute) => new()
    {
        Title = "약 먹기",
        Time = new TimeOnly(hour, minute),
        Remind = true
    };

    private static TaskItem TaskAt(DateOnly due, int hour, int minute) => new()
    {
        Title = "거래처 미팅",
        Due = due,
        DueTime = new TimeOnly(hour, minute),
        Remind = true
    };

    private static AppData Data(Routine routine)
    {
        var data = Data();
        data.Routines.Add(routine);
        return data;
    }

    private static AppData Data(TaskItem task)
    {
        var data = Data();
        data.Tasks.Add(task);
        return data;
    }

    private static AppData Data() => new()
    {
        LastLogicalDate = new DateOnly(2026, 9, 8),
        Settings = { DayStartHour = 4, MissedGraceMinutes = 60, RemindersEnabled = true }
    };

    private static void DeviceStateTests()
    {
        // 이 시험은 FLOW_DATA_DIR 로 지정된 임시 폴더에서만 돈다.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 기기별 상태 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var today = new DateOnly(2026, 9, 10);

        // ── 이 PC 것들은 data.json 에 실리면 안 된다.
        //    실리면 클라우드를 타고 다른 PC로 건너가 창이 화면 밖으로 나가고 알림이 안 울린다.
        var data = new AppData { LastLogicalDate = today };
        data.Settings.WindowLeft = 1234;
        data.Settings.WindowTop = 567;
        data.Settings.WindowWidth = 480;
        data.Settings.WindowHeight = 800;
        data.Settings.WindowSizedByUser = true;
        data.Settings.CompactMode = true;
        data.Settings.RunAtStartup = true;
        data.Settings.HotKey = "Ctrl+Shift+F9";
        data.Settings.LastUpdateCheck = new DateTime(2026, 9, 9, 12, 0, 0);
        data.Settings.LastBackupDate = today;

        var routine = new Routine
        {
            Title = "약 먹기",
            Time = new TimeOnly(9, 0),
            Remind = true,
            RemindHandled = today,
            RemindSnoozedUntil = new DateTime(2026, 9, 10, 9, 10, 0)
        };
        data.Routines.Add(routine);

        var task = new TaskItem { Title = "회의", Due = today, DueTime = new TimeOnly(15, 0), Remind = true, RemindHandled = today };
        data.Tasks.Add(task);

        Check("직렬화 성공", TrySerialize(data, out var json), true);

        foreach (var field in new[] { "WindowLeft", "WindowTop", "WindowWidth", "WindowHeight",
                                      "WindowSizedByUser", "CompactMode", "RunAtStartup",
                                      "HotKey", "LastUpdateCheck", "LastBackupDate",
                                      "RemindHandled", "RemindSnoozedUntil" })
        {
            Check($"{field} 는 data.json 에 없다", json.Contains(field), false);
        }

        // 함께 다녀야 하는 것들은 그대로 실려야 한다
        foreach (var field in new[] { "DayStartHour", "RemindersEnabled", "ReminderSound",
                                      "MissedGraceMinutes", "SnoozeMinutes", "AlwaysOnTop",
                                      "IdleOpacity", "Remind", "Time" })
        {
            Check($"{field} 는 data.json 에 있다", json.Contains(field), true);
        }

        // ── 기기별 파일로 오갔다 돌아오는지
        DeviceStore.Capture(data);

        var reloaded = new AppData { LastLogicalDate = today };
        reloaded.Routines.Add(new Routine { Id = routine.Id, Title = "약 먹기" });
        reloaded.Tasks.Add(new TaskItem { Id = task.Id, Title = "회의" });
        DeviceStore.Apply(reloaded);

        Check("창 위치가 돌아옴", reloaded.Settings.WindowLeft, (double?)1234);
        Check("창 높이가 돌아옴", reloaded.Settings.WindowHeight, 800.0);
        Check("컴팩트 모드가 돌아옴", reloaded.Settings.CompactMode, true);
        Check("자동 시작이 돌아옴", reloaded.Settings.RunAtStartup, true);
        Check("단축키 조합이 돌아옴", reloaded.Settings.HotKey, "Ctrl+Shift+F9");
        Check("업데이트 확인 시각이 돌아옴", reloaded.Settings.LastUpdateCheck, data.Settings.LastUpdateCheck);
        Check("루틴 알림 표시가 돌아옴", reloaded.Routines[0].RemindHandled, today);
        Check("루틴 미루기가 돌아옴", reloaded.Routines[0].RemindSnoozedUntil, routine.RemindSnoozedUntil);
        Check("할 일 알림 표시가 돌아옴", reloaded.Tasks[0].RemindHandled, today);

        // ── 다른 PC에서 온 파일에는 이 표시가 없으므로 거기서는 새로 울려야 한다
        var fromOtherPc = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppData);
        Check("건너온 데이터에는 알림 표시가 없다", fromOtherPc!.Routines[0].RemindHandled, (DateOnly?)null);
        Check("건너온 데이터에는 미루기가 없다", fromOtherPc.Routines[0].RemindSnoozedUntil, (DateTime?)null);

        // ── 주인이 사라지면 그 알림 상태도 함께 빠진다
        var trimmed = new AppData { LastLogicalDate = today };
        trimmed.Routines.Add(new Routine { Id = routine.Id, Title = "약 먹기", RemindHandled = today });
        DeviceStore.Capture(trimmed);

        var after = DeviceStore.Load();
        Check("사라진 할 일의 상태는 빠짐", after!.Reminders.Count, 1);
        Check("남은 것은 루틴", after.Reminders[0].Id, routine.Id);

        // ── 단축키를 껐으면 껐다는 사실이 살아남아야 한다.
        //    기본값이 "Ctrl+Alt+Space" 라, 빈 값을 못 지키면 껐는데도 다음에 켤 때 되살아난다.
        var off = new AppData { LastLogicalDate = today };
        off.Settings.HotKey = "";
        DeviceStore.Capture(off);

        var offAgain = new AppData { LastLogicalDate = today };
        DeviceStore.Apply(offAgain);
        Check("꺼 둔 단축키는 되살아나지 않음", offAgain.Settings.HotKey, "");
    }

    /// <summary>
    /// 전역 단축키 조합의 읽기·쓰기. 실제 등록(RegisterHotKey)은 Windows 일이라 여기서는 못 보고,
    /// 조합을 어떻게 알아듣고 어떻게 되돌려 적는지만 본다.
    /// </summary>
    private static void HotKeyTests()
    {
        Check("기본값은 Ctrl+Alt+Space", HotKeyCombo.Default.Saved, "Ctrl+Alt+Space");
        Check("기본값 화면 표기", HotKeyCombo.Default.Text, "Ctrl + Alt + Space");
        Check("기본값 가상 키", HotKeyCombo.Default.VirtualKey, 0x20u);

        var parsed = HotKeyCombo.Parse("Ctrl+Alt+Space");
        Check("되읽기 성공", parsed is not null, true);
        Check("되읽은 조합키", parsed!.Modifiers, HotKeyCombo.ModControl | HotKeyCombo.ModAlt);
        Check("되읽은 키", parsed.VirtualKey, 0x20u);

        Check("대소문자를 가리지 않음", HotKeyCombo.Parse("ctrl+ALT+space")?.Saved, "Ctrl+Alt+Space");
        Check("빈 값은 안 씀", HotKeyCombo.Parse(""), null);
        Check("null 은 안 씀", HotKeyCombo.Parse(null), null);
        Check("알아볼 수 없으면 안 씀", HotKeyCombo.Parse("Ctrl+Alt+없는키"), null);

        // ── 창이 넘겨주는 Avalonia 키 이름을 알아듣고, 사람이 읽는 이름으로 바꿔 적는다
        Check("D1 은 숫자 1", HotKeyCombo.From(true, true, false, false, "D1", out _)?.Saved, "Ctrl+Alt+1");
        Check("Return 은 Enter", HotKeyCombo.From(true, false, false, false, "Return", out _)?.Saved, "Ctrl+Enter");
        Check("OemTilde 는 물결표", HotKeyCombo.From(false, true, false, false, "OemTilde", out _)?.Saved, "Alt+`");
        Check("Left 는 화살표", HotKeyCombo.From(true, true, false, false, "Left", out _)?.Saved, "Ctrl+Alt+←");
        Check("F5 가 그대로", HotKeyCombo.From(true, false, false, false, "F5", out _)?.VirtualKey, 0x74u);
        Check("Win 조합", HotKeyCombo.From(false, false, false, true, "K", out _)?.Text, "Win + K");

        // ── 적어 둔 글자를 다시 읽으면 같은 조합이어야 한다. 여기가 어긋나면 재시작 때 단축키가 사라진다.
        foreach (var saved in new[] { "Ctrl+Alt+Space", "Ctrl+Shift+F9", "Alt+`", "Win+K", "Ctrl+Alt+←", "Ctrl+Alt+숫자판 5" })
        {
            Check($"{saved} 왕복", HotKeyCombo.Parse(saved)?.Saved, saved);
        }

        // ── Ctrl·Alt·Win 없이 잡으면 그 글자를 어느 창에서도 못 치게 된다
        Check("조합키 없이는 안 됨", HotKeyCombo.From(false, false, false, false, "K", out _), null);
        Check("Shift 만으로도 안 됨", HotKeyCombo.From(false, false, true, false, "K", out _), null);

        HotKeyCombo.From(false, false, true, false, "K", out var reason);
        Check("까닭을 말해 준다", reason.Length > 0, true);

        // ── 조합키만 눌린 순간은 흘려보내야 한다
        Check("Ctrl 은 조합키", HotKeyCombo.IsModifierName("LeftCtrl"), true);
        Check("LWin 은 조합키", HotKeyCombo.IsModifierName("LWin"), true);
        Check("A 는 조합키가 아님", HotKeyCombo.IsModifierName("A"), false);
        Check("조합키만으로는 안 됨", HotKeyCombo.From(true, false, false, false, "LeftCtrl", out _), null);

        // ── Esc 는 잡기를 취소하는 키라 조합으로 쓸 수 없어야 한다
        Check("Esc 는 잡히지 않음", HotKeyCombo.From(true, true, false, false, "Escape", out _), null);
    }

    private static void DataLocationTests()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 저장 위치 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = System.IO.Path.Combine(DataStore.Directory, "loctest");
        if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
        System.IO.Directory.CreateDirectory(root);

        // ── 빈 폴더를 고르면 지금 것을 복사해 넣는다.
        //    원본은 남겨 둔다 — 옮기다 잘못돼도 돌아갈 자리가 있어야 한다.
        var empty = System.IO.Path.Combine(root, "empty");
        var hadSource = System.IO.File.Exists(DataStore.FilePath);

        Check("빈 폴더에는 복사해 넣는다",
            DataLocation.Prepare(empty),
            hadSource ? MoveResult.CopiedHere : MoveResult.StartedEmpty);

        if (hadSource)
        {
            Check("새 자리에 파일이 생김", System.IO.File.Exists(System.IO.Path.Combine(empty, "data.json")), true);
            Check("원본은 그대로 남는다", System.IO.File.Exists(DataStore.FilePath), true);
        }

        // ── 이미 데이터가 있는 폴더(다른 PC가 먼저 올려 둔 경우)는 건드리지 않는다.
        //    여기서 덮어쓰면 그 PC의 최신 내용이 사라진다. 이 시험이 그것을 막는다.
        var occupied = System.IO.Path.Combine(root, "occupied");
        System.IO.Directory.CreateDirectory(occupied);

        var theirs = System.IO.Path.Combine(occupied, "data.json");
        const string mark = "{\"Version\":2,\"Tasks\":[],\"Routines\":[],\"History\":[],\"__mark\":\"other-pc\"}";
        System.IO.File.WriteAllText(theirs, mark);

        Check("이미 있으면 그것을 쓴다", DataLocation.Prepare(occupied), MoveResult.AdoptedExisting);
        Check("남의 파일을 덮어쓰지 않는다", System.IO.File.ReadAllText(theirs), mark);

        System.IO.Directory.Delete(root, true);
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
            // 앱과 같은 답을 내야 확인 도구로 쓸모가 있다. 지금 시각을 똑같이 넘긴다.
            var r = QuickAddParser.Parse(input, today, routineMode, TimeOnly.FromDateTime(DateTime.Now));
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
