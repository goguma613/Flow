using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Flow.Models;
using Flow.Services;
using Flow.ViewModels;

namespace Flow.Tests;

internal static partial class Program
{
    private static readonly DateOnly Mon = new(2026, 10, 5);
    private static readonly DateOnly Tue = Mon.AddDays(1);

    private static PhoneOp Op(string kind, DateOnly day, int hour, int minute = 0, Guid? target = null, string? text = null)
        => new()
        {
            Kind = kind,
            Day = day,
            At = day.ToDateTime(new TimeOnly(hour, minute)),
            Target = target,
            Text = text
        };

    /// <summary>폰에서 온 일을 PC가 어떻게 받아 넣는지. 파일을 모르는 순수 규칙.</summary>
    private static void PhoneEngineTests()
    {
        // ── 추가: PC 입력칸과 똑같이 읽는다
        {
            var data = new AppData { LastLogicalDate = Mon };
            var add = Op(PhoneOp.Add, Mon, 21, text: "내일 오후 3시 회의 !1");

            var result = PhoneLinkEngine.Apply(data, [add]);
            var task = data.Tasks.SingleOrDefault();

            Check("추가 1건", result.Added, 1);
            Check("새 할 일의 번호는 그 일의 번호", task?.Id, (Guid?)add.Id);
            Check("'내일'은 적은 날 기준", task?.Due, (DateOnly?)Tue);
            Check("시각", task?.DueTime, (TimeOnly?)new TimeOnly(15, 0));
            Check("중요도", task?.Priority, (Priority?)Priority.High);
            Check("시각을 적었으면 알림이 붙는다", task?.Remind, (bool?)true);
            Check("만든 날은 적은 날", task?.CreatedDate, (DateOnly?)Mon);

            // PC 입력칸에 같은 시각에 같은 줄을 적은 것과 똑같아야 한다
            var onPc = QuickAddParser.Parse("내일 오후 3시 회의 !1", Mon, false, new TimeOnly(21, 0));
            Check("PC에서 적은 것과 같은 제목", task?.Title, onPc.Title);
            Check("PC에서 적은 것과 같은 날짜", task?.Due, onPc.Due);

            var again = PhoneLinkEngine.Apply(data, [add]);
            Check("같은 일이 또 와도 다시 하지 않는다", again.NeedsSave, false);
            Check("할 일은 여전히 하나", data.Tasks.Count, 1);

            // 반영 기록이 사라져도(옛 버전 PC가 data.json 을 다시 썼다) 하나로 족하다
            data.PhoneOpsApplied.Clear();
            PhoneLinkEngine.Apply(data, [add]);
            Check("기록이 지워져도 둘이 되지 않는다", data.Tasks.Count, 1);
        }

        {
            var data = new AppData { LastLogicalDate = Mon };
            PhoneLinkEngine.Apply(data, [Op(PhoneOp.Add, Mon, 8, text: "매일 물 2L 마시기")]);
            Check("'매일'은 루틴이 된다", data.Routines.Count, 1);
            Check("루틴 제목", data.Routines.FirstOrDefault()?.Title, "물 2L 마시기");
        }

        {
            // 밤 9시에 "9시 22분"이라고 적으면 오후다. 폰이 누른 그 시각을 기준으로 읽어야 한다.
            var data = new AppData { LastLogicalDate = Mon };
            PhoneLinkEngine.Apply(data, [Op(PhoneOp.Add, Mon, 21, text: "9시 22분 약 먹기")]);
            Check("오전·오후 없는 시각은 누른 시각 기준", data.Tasks.FirstOrDefault()?.DueTime,
                (TimeOnly?)new TimeOnly(21, 22));
        }

        {
            var data = new AppData { LastLogicalDate = Mon };
            var blank = PhoneLinkEngine.Apply(data, [Op(PhoneOp.Add, Mon, 9, text: "   ")]);
            Check("빈 줄은 아무것도 안 만든다", data.Tasks.Count + data.Routines.Count, 0);
            Check("빈 줄도 처리한 것으로 적는다", blank.Skipped, 1);
        }

        // ── 할 일 체크
        {
            var data = new AppData { LastLogicalDate = Mon };
            var task = new TaskItem { Title = "보고서", CreatedDate = Mon };
            data.Tasks.Add(task);

            var done = PhoneLinkEngine.Apply(data, [Op(PhoneOp.Done, Mon, 10, target: task.Id)]);
            Check("완료 1건", done.Checked, 1);
            Check("끝남", task.Done, true);
            Check("끝낸 날은 폰에서 누른 날", task.CompletedDate, (DateOnly?)Mon);

            var undo = PhoneLinkEngine.Apply(data, [Op(PhoneOp.Undone, Mon, 11, target: task.Id)]);
            Check("완료 취소 1건", undo.Unchecked, 1);
            Check("다시 안 끝남", task.Done, false);
            Check("끝낸 날도 지워짐", task.CompletedDate, (DateOnly?)null);
        }

        {
            // 체크했다가 바로 해제. 클라우드가 순서를 바꿔 내려줘도 누른 순서대로 맞춘다.
            var data = new AppData { LastLogicalDate = Mon };
            var task = new TaskItem { Title = "장보기", CreatedDate = Mon };
            data.Tasks.Add(task);

            var first = Op(PhoneOp.Done, Mon, 10, 0, task.Id);
            var second = Op(PhoneOp.Undone, Mon, 10, 1, task.Id);
            PhoneLinkEngine.Apply(data, [second, first]);
            Check("누른 순서대로 — 마지막은 해제", task.Done, false);
        }

        // ── 루틴 체크
        {
            var data = new AppData { LastLogicalDate = Mon };
            var routine = new Routine { Title = "스트레칭", Streak = 3, LastDoneDate = Mon.AddDays(-1) };
            data.Routines.Add(routine);

            PhoneLinkEngine.Apply(data, [Op(PhoneOp.Done, Mon, 22, target: routine.Id)]);
            Check("루틴 체크됨", routine.DoneToday, true);
            Check("연속 기록이 이어짐", routine.Streak, 4);

            PhoneLinkEngine.Apply(data, [Op(PhoneOp.Undone, Mon, 22, 5, routine.Id)]);
            Check("해제하면 연속 기록도 돌아감", routine.Streak, 3);
        }

        {
            // 월요일 밤에 폰으로 체크 → PC는 화요일 아침에야 켜짐.
            // 롤오버 전에 넣어야 월요일 몫이 되어 결산과 연속 기록에 잡힌다.
            var data = new AppData { LastLogicalDate = Mon };
            var routine = new Routine { Title = "일기", Streak = 5, LastDoneDate = Mon.AddDays(-1) };
            data.Routines.Add(routine);
            var ops = new[] { Op(PhoneOp.Done, Mon, 23, target: routine.Id) };

            PhoneLinkEngine.Apply(data, ops);
            DayEngine.Rollover(data, Tue);
            PhoneLinkEngine.Apply(data, ops);

            Check("월요일 결산에 들어감", data.History.FirstOrDefault(h => h.Date == Mon)?.RoutinesDone, (int?)1);
            Check("연속 기록이 끊기지 않음", routine.Streak, 6);
            Check("화요일 아침에는 다시 빈칸", routine.DoneToday, false);
        }

        {
            // 반대로, 이미 정산이 끝난 날의 루틴 체크는 받지 않는다(그 날을 다시 계산할 수는 없다).
            var data = new AppData { LastLogicalDate = Tue };
            var routine = new Routine { Title = "일기" };
            data.Routines.Add(routine);

            var stale = PhoneLinkEngine.Apply(data, [Op(PhoneOp.Done, Mon, 23, target: routine.Id)]);
            Check("지난 날 루틴 체크는 건너뜀", routine.DoneToday, false);
            Check("건너뛴 것도 처리한 것으로 적음", stale.Skipped, 1);
            Check("그래서 폰이 지울 수 있다", data.PhoneOpsApplied.Count, 1);
        }

        {
            // 폰이 PC보다 앞선 날(PC가 아직 4시를 안 넘김). 지금은 손대지 않고, 롤오버 뒤에 넣는다.
            var data = new AppData { LastLogicalDate = Mon };
            var routine = new Routine { Title = "운동" };
            data.Routines.Add(routine);
            var ahead = new[] { Op(PhoneOp.Done, Tue, 7, target: routine.Id) };

            var now = PhoneLinkEngine.Apply(data, ahead);
            Check("앞선 날의 일은 아직 안 넣음", routine.DoneToday, false);
            Check("기록도 안 함 — 나중에 넣어야 하니까", now.NeedsSave, false);

            DayEngine.Rollover(data, Tue);
            PhoneLinkEngine.Apply(data, ahead);
            Check("롤오버 뒤에 들어감", routine.DoneToday, true);
        }

        {
            var data = new AppData { LastLogicalDate = Mon };
            var weekday = new Routine { Title = "수영", Days = [DayOfWeek.Wednesday] };
            data.Routines.Add(weekday);
            PhoneLinkEngine.Apply(data, [Op(PhoneOp.Done, Mon, 9, target: weekday.Id)]);
            Check("그 날 예정이 아닌 루틴은 체크 안 됨", weekday.DoneToday, false);
        }

        // ── 모르는 것 · 없는 것
        {
            var data = new AppData { LastLogicalDate = Mon };
            var future = Op("move", Mon, 9, text: "다른 날로");
            var result = PhoneLinkEngine.Apply(data, [future]);
            Check("모르는 갈래는 건드리지 않음", result.NeedsSave, false);
            Check("모르는 갈래는 적지도 않음 — 새 버전 PC가 받아야 하니까", data.PhoneOpsApplied.Count, 0);

            var gone = PhoneLinkEngine.Apply(data, [Op(PhoneOp.Done, Mon, 9, target: Guid.NewGuid())]);
            Check("PC에서 지운 항목은 건너뜀", gone.Skipped, 1);
        }

        {
            var data = new AppData { LastLogicalDate = Mon };
            data.PhoneOpsApplied.Add(new AppliedPhoneOp { Id = Guid.NewGuid(), Day = Mon.AddDays(-60) });
            data.PhoneOpsApplied.Add(new AppliedPhoneOp { Id = Guid.NewGuid(), Day = Mon.AddDays(-3) });
            PhoneLinkEngine.Apply(data, []);
            Check("오래된 반영 기록은 지움", data.PhoneOpsApplied.Count, 1);
        }

        // ── 고치기: 이름 · 중요도 · 지우기
        {
            var data = new AppData { LastLogicalDate = Mon };
            var task = new TaskItem { Title = "장부 정리" };
            var routine = new Routine { Title = "물 마시기" };
            data.Tasks.Add(task);
            data.Routines.Add(routine);

            var rename = Op(PhoneOp.Rename, Mon, 9, target: task.Id, text: "  내일 장부 정리  ");
            var renameRoutine = Op(PhoneOp.Rename, Mon, 9, 1, target: routine.Id, text: "물 2L 마시기");
            var priority = Op(PhoneOp.SetPriority, Mon, 9, 2, target: task.Id, text: "High");
            var result = PhoneLinkEngine.Apply(data, [rename, renameRoutine, priority]);

            Check("수정 3건", result.Edited, 3);
            Check("새 이름은 적은 그대로 — 날짜 말을 다시 읽지 않음", task.Title, "내일 장부 정리");
            Check("마감은 그대로", task.Due, (DateOnly?)null);
            Check("루틴 이름도 바뀜", routine.Title, "물 2L 마시기");
            Check("중요도", task.Priority, Priority.High);
            Check("고친 것도 화면이 바뀐 것", result.Changed, 3);

            var bad = PhoneLinkEngine.Apply(data, [
                Op(PhoneOp.SetPriority, Mon, 10, target: task.Id, text: "7"),
                Op(PhoneOp.SetPriority, Mon, 10, 1, target: routine.Id, text: "Low"),
                Op(PhoneOp.Rename, Mon, 10, 2, target: task.Id, text: "   ")]);
            Check("엉뚱한 값은 건너뜀", bad.Skipped, 3);
            Check("중요도는 그대로", task.Priority, Priority.High);
            Check("빈 이름으로 바뀌지 않음", task.Title, "내일 장부 정리");

            var delete = Op(PhoneOp.Delete, Mon, 11, target: task.Id);
            var deleteRoutine = Op(PhoneOp.Delete, Mon, 11, 1, target: routine.Id);
            var gone = PhoneLinkEngine.Apply(data, [delete, deleteRoutine]);
            Check("삭제 2건", gone.Deleted, 2);
            Check("할 일 없음", data.Tasks.Count, 0);
            Check("루틴 없음", data.Routines.Count, 0);

            data.PhoneOpsApplied.Clear();
            var again = PhoneLinkEngine.Apply(data, [delete]);
            Check("이미 지운 것은 건너뜀", again.Skipped, 1);
        }

        // ── 폰에서 적고 PC가 받기 전에 고친 것: 추가 → 이름 바꾸기 순서대로
        {
            var data = new AppData { LastLogicalDate = Mon };
            var add = Op(PhoneOp.Add, Mon, 9, text: "우유 사기");
            var rename = Op(PhoneOp.Rename, Mon, 9, 5, target: add.Id, text: "우유랑 빵 사기");
            PhoneLinkEngine.Apply(data, [rename, add]);
            Check("나중에 고친 이름으로", data.Tasks.SingleOrDefault()?.Title, "우유랑 빵 사기");
        }

        // ── 폰이 볼 화면
        {
            var data = new AppData { LastLogicalDate = Mon };
            data.Routines.Add(new Routine { Title = "매일", Order = 1 });
            data.Routines.Add(new Routine { Title = "수요일만", Days = [DayOfWeek.Wednesday] });
            data.Tasks.Add(new TaskItem { Title = "보통", Order = 0 });
            data.Tasks.Add(new TaskItem { Title = "급함", Priority = Priority.High, Order = 1 });
            data.Tasks.Add(new TaskItem { Title = "다음 주", Due = Mon.AddDays(7) });
            data.Tasks.Add(new TaskItem { Title = "모레", Due = Mon.AddDays(2) });
            data.Tasks.Add(new TaskItem { Title = "오늘 끝냄", Done = true, CompletedDate = Mon });
            data.Tasks.Add(new TaskItem { Title = "지난주 끝냄", Done = true, CompletedDate = Mon.AddDays(-5) });
            var appliedId = Guid.NewGuid();
            data.PhoneOpsApplied.Add(new AppliedPhoneOp { Id = appliedId, Day = Mon });

            var view = PhoneLinkEngine.BuildView(data, Mon, Mon.ToDateTime(new TimeOnly(9, 0)));

            Check("오늘 예정 루틴만", string.Join(",", view.Routines.Select(r => r.Title)), "매일");
            Check("오늘 할 일과 오늘 끝낸 것, 중요한 것 먼저",
                string.Join(",", view.Tasks.Select(t => t.Title)), "급함,보통,오늘 끝냄");
            Check("예정은 따로", string.Join(",", view.Upcoming.Select(t => t.Title)), "모레,다음 주");
            Check("반영한 일 번호를 알려 줌", view.AppliedOps.Contains(appliedId), true);
            Check("기준 시각을 알려 줌", view.DayStartHour, 4);

            var json = JsonSerializer.Serialize(view, AppJsonContext.Default.PhoneView);
            Check("중요도는 글자로", json.Contains("\"High\""), true);
        }

        // ── 새 필드가 data.json 을 오가는지
        {
            var data = new AppData { LastLogicalDate = Mon };
            var id = Guid.NewGuid();
            data.PhoneOpsApplied.Add(new AppliedPhoneOp { Id = id, Day = Mon });
            var back = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(data, AppJsonContext.Default.AppData), AppJsonContext.Default.AppData);
            Check("반영 기록이 data.json 에 남음", back?.PhoneOpsApplied.FirstOrDefault()?.Id, (Guid?)id);

            // 이 필드가 없는 옛 파일도 그대로 읽힌다
            var old = JsonSerializer.Deserialize("{\"LastLogicalDate\":\"2026-10-05\"}", AppJsonContext.Default.AppData);
            Check("옛 파일은 빈 목록으로 읽힘", old?.PhoneOpsApplied.Count, (int?)0);
        }
    }

    /// <summary>폰과 주고받는 파일. 실제 디스크에서 시험하므로 FLOW_DATA_DIR 아래에서만 돈다.</summary>
    private static void PhoneStoreTests()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 폰 파일 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = Path.Combine(DataStore.Directory, "phonetest");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var dataDir = Path.Combine(root, "drive", "Flow");
        Directory.CreateDirectory(dataDir);

        var store = new PhoneLinkStore(dataDir);
        var view = new PhoneView { Today = Mon, GeneratedAt = Mon.ToDateTime(new TimeOnly(9, 0)) };

        // ── 폰이 연결하기 전에는 아무 일도 없다
        Check("연결 폴더가 없으면 못 찾음", store.FindFolder(), (string?)null);
        Check("inbox 도 없음", store.ReadInboxIfChanged(), null);
        Check("화면도 안 씀", store.WriteView(view), false);
        Check("PC가 폴더를 만들지 않는다", Directory.Exists(Path.Combine(root, "drive", PhoneLinkStore.FolderName)), false);

        // ── 폰이 폴더만 만들었다(아직 파일 없음)
        var link = Path.Combine(root, "drive", PhoneLinkStore.FolderName);
        Directory.CreateDirectory(link);
        Check("한 단계 위의 연결 폴더를 찾음", store.FindFolder(), (string?)link);
        Check("화면 파일이 없으면 쓰지 않음", store.WriteView(view), false);
        Check("PC가 파일을 만들지 않는다 — 폰 앱이 손을 못 대게 됨",
            File.Exists(Path.Combine(link, PhoneLinkStore.ViewFileName)), false);

        // ── 폰이 빈 파일 두 개를 만들었다
        var viewPath = Path.Combine(link, PhoneLinkStore.ViewFileName);
        var inboxPath = Path.Combine(link, PhoneLinkStore.InboxFileName);
        File.WriteAllText(viewPath, "");
        File.WriteAllText(inboxPath, "");

        var idBefore = FileIdentity(viewPath);
        Check("화면을 씀", store.WriteView(view), true);
        Check("제자리에 덮어씀 — 드라이브에서 같은 파일로 남는다", FileIdentity(viewPath), idBefore);

        var written = JsonSerializer.Deserialize(File.ReadAllText(viewPath), AppJsonContext.Default.PhoneView);
        Check("쓴 내용을 다시 읽을 수 있음", written?.Today, (DateOnly?)Mon);

        view.GeneratedAt = view.GeneratedAt.AddMinutes(5);
        Check("시각만 다르면 다시 쓰지 않음 — 업로드를 아낀다", store.WriteView(view), false);

        view.Tasks.Add(new PhoneViewItem { Id = Guid.NewGuid(), Title = "새 일", Priority = Priority.High });
        Check("내용이 바뀌면 씀", store.WriteView(view), true);
        var compact = File.ReadAllText(viewPath);
        Check("파일은 한글을 그대로 (\\u 로 바꾸지 않음)", (compact.Contains("\"Title\":\"새 일\""), compact.Contains("\\u")), (true, false));
        Check("파일은 들여쓰기 없이 한 줄", compact.Contains('\n'), false);
        Check("파일의 중요도는 이름 그대로", compact.Contains("\"Priority\":\"High\""), true);
        view.GeneratedAt = view.GeneratedAt.AddMinutes(5);
        Check("한 줄로 써도 시각만 다르면 다시 쓰지 않음", store.WriteView(view), false);

        // 내용이 짧아졌을 때 뒤에 옛 글자가 남으면 JSON 이 깨진다
        view.Tasks.Clear();
        store.WriteView(view);
        var shorter = JsonSerializer.Deserialize(File.ReadAllText(viewPath), AppJsonContext.Default.PhoneView);
        Check("짧아져도 깨지지 않음", shorter?.Tasks.Count, (int?)0);

        // ── inbox 읽기
        Check("빈 inbox 는 없는 것으로", store.ReadInbox(), null);

        var op = Op(PhoneOp.Add, Mon, 9, text: "우유 사기");
        File.WriteAllText(inboxPath,
            JsonSerializer.Serialize(new PhoneInbox { Ops = [op] }, AppJsonContext.Default.PhoneInbox));
        Check("inbox 를 읽음", store.ReadInboxIfChanged()?.FirstOrDefault()?.Id, (Guid?)op.Id);
        Check("안 바뀌었으면 다시 안 읽음", store.ReadInboxIfChanged(), null);

        // 드라이브가 아직 덜 받아 깨진 상태 → 이번엔 넘기고 다음에 다시 본다
        File.WriteAllText(inboxPath, "{\"Ops\":[{\"Id\":");
        Check("깨진 inbox 는 넘김", store.ReadInboxIfChanged(), null);
        File.WriteAllText(inboxPath,
            JsonSerializer.Serialize(new PhoneInbox { Ops = [op, Op(PhoneOp.Add, Mon, 10, text: "빵")] },
                AppJsonContext.Default.PhoneInbox));
        Check("다 받아지면 읽음", store.ReadInboxIfChanged()?.Count, (int?)2);

        // ── 더 위에 있는 폴더도, 너무 먼 폴더는 아니게
        var deepData = Path.Combine(root, "drive", "a", "b", "c");
        Directory.CreateDirectory(deepData);
        Check("세 단계 넘게 위에 있으면 찾지 않음", new PhoneLinkStore(deepData).FindFolder(), (string?)null);
        Check("바로 위 단계에서는 찾음",
            new PhoneLinkStore(Path.Combine(root, "drive", "x")).FindFolder(), (string?)link);

        Directory.Delete(root, true);
    }

    /// <summary>시험용 메모 체크 받는 곳. 정해 둔 결과를 돌려주고 받은 값을 적어 둔다.</summary>
    private sealed class FakeMemoTarget(MemoCheckResult answer) : IPhoneMemoTarget
    {
        public MemoCheckResult Answer { get; set; } = answer;
        public List<(Guid Id, string Text, int? Line, bool Checked, DateOnly? Day)> Calls { get; } = new();

        public MemoCheckResult SetLineChecked(Guid id, string text, int? line, bool isChecked, DateOnly? day = null)
        {
            Calls.Add((id, text, line, isChecked, day));
            return Answer;
        }
    }

    /// <summary>
    /// 시험용: 체크 줄 하나를 가진 메모(Memo). 처음 LockedCalls 번은 잠겨 미루고, 그다음부터 그 줄을 맞춘다.
    /// History 는 실제로 바꾼 순서(+ 체크, - 해제). 다른 메모의 체크는 그냥 들어간 것으로 한다.
    /// </summary>
    private sealed class LineTarget : IPhoneMemoTarget
    {
        public Guid Memo { get; init; }
        public int LockedCalls { get; set; }
        public int Calls { get; private set; }
        public bool Checked { get; private set; }
        public string History { get; private set; } = "";

        public MemoCheckResult SetLineChecked(Guid id, string text, int? line, bool isChecked, DateOnly? day = null)
        {
            Calls++;
            if (id != Memo) return MemoCheckResult.Applied;
            if (LockedCalls > 0)
            {
                LockedCalls--;
                return MemoCheckResult.Deferred;
            }
            if (Checked == isChecked) return MemoCheckResult.Skipped;
            Checked = isChecked;
            History += isChecked ? "+" : "-";
            return MemoCheckResult.Applied;
        }
    }

    private static PhoneOp MemoCheckOp(Guid memo, string text, int? line, bool isChecked, DateOnly day, int hour, int minute = 0)
    {
        var op = Op(PhoneOp.MemoCheck, day, hour, minute, target: memo, text: text);
        op.Line = line;
        op.Checked = isChecked;
        return op;
    }

    /// <summary>폰 화면의 메모와 폰에서 온 메모 체크.</summary>
    private static void PhoneMemoTests()
    {
        var memoA = Guid.Parse("3f2a9c1e-0000-4000-8000-0000000000a1");
        var memoB = Guid.Parse("3f2a9c1e-0000-4000-8000-0000000000b2");
        var memoGone = Guid.Parse("3f2a9c1e-0000-4000-8000-0000000000c3");

        // ── 받는 곳이 없으면(옛 길) 메모 체크는 모르는 갈래처럼 남긴다
        {
            var data = new AppData { LastLogicalDate = Mon };
            var op = MemoCheckOp(memoA, "단가표 확인", 2, true, Mon, 9);
            var result = PhoneLinkEngine.Apply(data, [op]);
            Check("메모 받는 곳 없이: 저장할 것 없음", result.NeedsSave, false);
            Check("메모 받는 곳 없이: 처리했다고 적지 않음", data.PhoneOpsApplied.Count, 0);
        }

        // ── 받는 곳의 결과대로 적는다
        {
            var data = new AppData { LastLogicalDate = Mon };
            var task = new TaskItem { Title = "장부 정리" };
            data.Tasks.Add(task);

            var target = new FakeMemoTarget(MemoCheckResult.Deferred);
            var before = Op(PhoneOp.Done, Mon, 8, target: task.Id);
            var check = MemoCheckOp(memoA, "단가표 확인", 2, true, Mon, 9);
            var rename = Op(PhoneOp.Rename, Mon, 10, target: task.Id, text: "장부 정리 끝");
            var deferred = PhoneLinkEngine.Apply(data, [rename, check, before], target);
            Check("미룬 메모 체크는 처리했다고 적지 않음 (inbox 에 남음)",
                data.PhoneOpsApplied.Any(a => a.Id == check.Id), false);
            Check("미뤄도 다른 일은 들어감", (task.Done, task.Title), (true, "장부 정리 끝"));
            Check("미룬 것은 건너뜀으로도 안 셈", (deferred.Skipped, deferred.MemoChecked), (0, 0));
            Check("받는 곳이 받은 값", target.Calls.SingleOrDefault(), (memoA, "단가표 확인", (int?)2, true, (DateOnly?)Mon));

            target.Answer = MemoCheckResult.Applied;
            var applied = PhoneLinkEngine.Apply(data, [rename, check, before], target);
            Check("다음에 다시 해 봄 → 메모 체크 1", applied.MemoChecked, 1);
            Check("메모 체크도 화면이 바뀐 것", applied.Changed, 1);
            Check("이번에는 처리했다고 적음", data.PhoneOpsApplied.Any(a => a.Id == check.Id), true);
            PhoneLinkEngine.Apply(data, [check], target);
            Check("적은 것은 다시 하지 않음", target.Calls.Count, 2);

            target.Answer = MemoCheckResult.Skipped;
            var none = MemoCheckOp(memoGone, "없는 줄", 0, true, Mon, 11);
            var skipped = PhoneLinkEngine.Apply(data, [none], target);
            Check("맞는 줄이 없으면 건너뛰고 처리함으로 적음", (skipped.Skipped, data.PhoneOpsApplied.Any(a => a.Id == none.Id)), (1, true));

            var broken = Op(PhoneOp.MemoCheck, Mon, 12, target: memoA, text: "단가표 확인");
            var brokenResult = PhoneLinkEngine.Apply(data, [broken], target);
            Check("Checked 가 없으면 건너뜀", (brokenResult.Skipped, target.Calls.Count), (1, 3));

            var tomorrow = MemoCheckOp(memoA, "단가표 확인", 2, true, Tue, 9);
            PhoneLinkEngine.Apply(data, [tomorrow], target);
            Check("PC 가 아직 그 날에 안 왔으면 손대지 않음", (target.Calls.Count, data.PhoneOpsApplied.Any(a => a.Id == tomorrow.Id)), (3, false));
        }

        // ── PC 가 쓰는 중이라 남긴 체크는 메모마다 세어 알린다(잠김 같은 다른 까닭은 세지 않음)
        {
            var data = new AppData { LastLogicalDate = Mon };
            var target = new FakeMemoTarget(MemoCheckResult.Held);
            var c1 = MemoCheckOp(memoA, "엑셀 받기", 1, true, Mon, 9);
            var c2 = MemoCheckOp(memoA, "단가표 확인", 2, true, Mon, 9, 1);
            var heldResult = PhoneLinkEngine.Apply(data, [c1, c2], target);
            Check("쓰는 중이라 남긴 체크 수 (메모마다)", (heldResult.HeldFor(memoA), heldResult.HeldFor(memoB)), (2, 0));
            Check("쓰는 중이면 처리했다고 적지 않음", (data.PhoneOpsApplied.Count, heldResult.NeedsSave), (0, false));

            target.Answer = MemoCheckResult.Deferred;
            var locked = PhoneLinkEngine.Apply(data, [c1], target);
            Check("잠김으로 미룬 것은 쓰는 중으로 세지 않음", (locked.Held, data.PhoneOpsApplied.Count), ((IReadOnlyDictionary<Guid, int>?)null, 0));

            target.Answer = MemoCheckResult.Applied;
            var released = PhoneLinkEngine.Apply(data, [c1, c2], target);
            Check("놓으면 들어가고 남은 것 없음", (released.MemoChecked, released.Held), (2, (IReadOnlyDictionary<Guid, int>?)null));

            var sum = new PhoneApplyResult(0, 0, 0, 0, Held: new Dictionary<Guid, int> { [memoA] = 1 })
                      + new PhoneApplyResult(0, 0, 0, 0, Held: new Dictionary<Guid, int> { [memoA] = 2, [memoB] = 1 });
            Check("결과를 더하면 남긴 수도 더함", (sum.HeldFor(memoA), sum.HeldFor(memoB)), (3, 1));
        }

        // ── 한 메모의 체크를 하나 남기면 그 메모의 뒤 체크도 남긴다 — 체크 → 해제가 거꾸로 끝나지 않게
        {
            var data = new AppData { LastLogicalDate = Mon };
            var line = new LineTarget { Memo = memoA, LockedCalls = 1 };   // 첫 시도만 잠김(잠깐 잡혔다 풀림)
            var check = MemoCheckOp(memoA, "단가표 확인", 2, true, Mon, 9);
            var uncheck = MemoCheckOp(memoA, "단가표 확인", 2, false, Mon, 9, 1);
            var other = MemoCheckOp(memoB, "아침 점검", 0, true, Mon, 9, 2);

            var first = PhoneLinkEngine.Apply(data, [uncheck, check, other], line);
            Check("첫 체크가 잠겨 미뤄지면 같은 메모의 해제도 남김", (line.Calls, line.Checked, first.MemoChecked), (2, false, 1));
            Check("다른 메모의 체크는 그대로 들어감", data.PhoneOpsApplied.Select(a => a.Id).SingleOrDefault(), other.Id);

            PhoneLinkEngine.Apply(data, [uncheck, check, other], line);
            Check("풀린 뒤: 체크 → 해제 순으로 들어가 마지막은 해제", (line.History, line.Checked), ("+-", false));
            Check("둘 다 처리했다고 적음", data.PhoneOpsApplied.Count, 3);
        }

        // ── 폰 화면의 메모: 순서, 기호 뗀 제목, \n 본문, 살아 있는 메모만 연결
        {
            MemoSummary Summary(Guid id, bool pinned, int day, string rev, bool unreadable = false)
                => new(id, "", "?", "", new[] { "정산", "AK넷" }, pinned, id == memoB, new DateTime(2026, 10, day, 9, 0, 0), rev,
                    unreadable, id == memoB ? Mon : null);

            var bodies = new Dictionary<Guid, string>
            {
                [memoA] = "# 배포 절차 #AK넷\r\n[ ] 엑셀 받기\r\n[x] 단가표 확인",
                [memoB] = "\n\n[ ] 아침 점검\n- 메모",
                [memoGone] = "읽지 못함",
            };
            var loads = 0;
            MemoFile? Load(Guid id)
            {
                loads++;
                if (!bodies.TryGetValue(id, out var body)) return null;
                return new MemoFile(new MemoDocument(id, true, false, false, null, null,
                    Array.Empty<KeyValuePair<string, string>>(), body), "", MemoFormat.RevOf(body));
            }

            var cache = new PhoneMemoCache();
            var summaries = new List<MemoSummary>
            {
                Summary(memoA, false, 9, MemoFormat.RevOf(bodies[memoA])),
                Summary(memoB, true, 1, MemoFormat.RevOf(bodies[memoB])),
                Summary(memoGone, false, 10, "", unreadable: true),
            };
            var memos = cache.Build(summaries, Load);
            Check("읽지 못한 메모는 빼고, 처음엔 다 읽음", (memos.Count, loads), (2, 2));
            var a = memos.Single(m => m.Id == memoA);
            Check("제목은 기호를 뗌", a.Title, "배포 절차 #AK넷");
            Check("본문은 머리말 뺀 그대로, 줄바꿈만 \\n", a.Text, "# 배포 절차 #AK넷\n[ ] 엑셀 받기\n[x] 단가표 확인");
            Check("판", a.Rev, MemoFormat.RevOf(bodies[memoA]));
            Check("태그는 요약 그대로 (머리말+본문)", string.Join(",", a.Tags), "정산,AK넷");
            var b = memos.Single(m => m.Id == memoB);
            Check("체크 줄 제목도 기호를 뗌", b.Title, "아침 점검");
            Check("매일·초기화 날", (b.Daily, b.Reset), (true, (DateOnly?)Mon));

            cache.Build(summaries, Load);
            Check("판이 같으면 다시 읽지 않음", loads, 2);

            bodies[memoA] = "# 배포 절차 #AK넷\r\n[x] 엑셀 받기\r\n[x] 단가표 확인";
            summaries[0] = Summary(memoA, false, 11, MemoFormat.RevOf(bodies[memoA]));
            var changed = cache.Build(summaries, Load);
            Check("판이 바뀐 메모만 읽음", loads, 3);
            Check("새 본문", changed.Single(m => m.Id == memoA).Text.Contains("[x] 엑셀 받기"), true);

            summaries.RemoveAt(1);
            cache.Build(summaries, Load);
            summaries.Add(Summary(memoB, true, 1, MemoFormat.RevOf(bodies[memoB])));
            cache.Build(summaries, Load);
            Check("사라진 메모는 잊음 (돌아오면 다시 읽음)", loads, 4);

            var lastText = changed.Single(m => m.Id == memoA).Text;
            bodies.Remove(memoA);
            summaries[0] = Summary(memoA, false, 12, "다른 판");
            var failed = cache.Build(summaries, Load).SingleOrDefault(m => m.Id == memoA);
            Check("잠깐 읽다 실패해도 기억한 본문으로 그대로 보냄", failed?.Text, lastText);
            Check("…머리말 값은 새 요약에서", failed?.Updated, (DateTime?)new DateTime(2026, 10, 12, 9, 0, 0));
            bodies[memoA] = "# 배포 절차\n[ ] 다시 읽음";
            cache.Build(summaries, Load);
            Check("다음에는 다시 읽어 봄", loads, 6);

            summaries[0] = Summary(memoA, false, 13, "", unreadable: true);
            var unreadable = cache.Build(summaries, Load).SingleOrDefault(m => m.Id == memoA);
            Check("잠깐 읽지 못하는 메모도 기억한 것을 그대로 보냄", (unreadable?.Text, unreadable?.Title), ((string?)"# 배포 절차\n[ ] 다시 읽음", (string?)"배포 절차"));
            Check("…읽지 못한 요약은 다시 읽지 않음", loads, 6);

            // 폰 화면에 얹기
            var list = new List<PhoneMemo>
            {
                new() { Id = memoA, Title = "오래된", Updated = new DateTime(2026, 10, 1) },
                new() { Id = memoGone, Title = "최근", Updated = new DateTime(2026, 10, 9) },
                new() { Id = memoB, Title = "고정", Pinned = true, Updated = new DateTime(2026, 9, 1) },
            };
            var data = new AppData { LastLogicalDate = Mon };
            data.Routines.Add(new Routine { Title = "아침 점검", MemoId = memoB });
            data.Tasks.Add(new TaskItem { Title = "배포", MemoId = memoA, Order = 0 });
            data.Tasks.Add(new TaskItem { Title = "지운 메모", MemoId = Guid.NewGuid(), Order = 1 });
            data.Tasks.Add(new TaskItem { Title = "다음 주 배포", MemoId = memoA, Due = Mon.AddDays(7) });

            var view = PhoneLinkEngine.BuildView(data, Mon, Mon.ToDateTime(new TimeOnly(9, 0)), list);
            Check("메모 순서: 고정 먼저, 그다음 최근", string.Join(",", view.Memos.Select(m => m.Title)), "고정,최근,오래된");
            Check("루틴의 메모 연결", view.Routines.Single().MemoId, (Guid?)memoB);
            Check("할 일의 메모 연결", view.Tasks.Single(t => t.Title == "배포").MemoId, (Guid?)memoA);
            Check("목록에 없는 메모(휴지통·지움)는 연결하지 않음", view.Tasks.Single(t => t.Title == "지운 메모").MemoId, (Guid?)null);
            Check("예정의 메모 연결", view.Upcoming.Single().MemoId, (Guid?)memoA);

            Check("쓰는 중인 메모가 없으면 HeldMemos 비어 있음", view.HeldMemos.Count, 0);
            var heldView = PhoneLinkEngine.BuildView(data, Mon, Mon.ToDateTime(new TimeOnly(9, 0)), list, [memoB, Guid.NewGuid()]);
            Check("HeldMemos: 폰에 보이는 메모 중 PC 가 쓰는 중인 것만", string.Join(",", heldView.HeldMemos), memoB.ToString());

            var old = PhoneLinkEngine.BuildView(data, Mon, Mon.ToDateTime(new TimeOnly(9, 0)));
            Check("메모 없이 만든 화면: 메모 없음, 연결 없음",
                (old.Memos.Count, old.Routines.Single().MemoId), (0, (Guid?)null));

            // JSON 에 새 필드
            view.Memos[0].Reset = Mon;
            view.Memos[0].Tags = ["정산"];
            view.Memos[0].Text = "고정\n[ ] 하나";
            var json = JsonSerializer.Serialize(view, AppJsonContext.Default.PhoneView);
            foreach (var field in new[] { "Memos", "MemoId", "Id", "Title", "Text", "Pinned", "Daily", "Reset", "Tags", "Rev", "Updated" })
                Check($"phone-view.json 에 {field}", json.Contains($"\"{field}\":"), true);
            Check("Reset 은 yyyy-MM-dd", json.Contains("\"Reset\": \"2026-10-05\""), true);
            Check("초기화 날이 없으면 null", json.Contains("\"Reset\": null"), true);
            view.HeldMemos = [memoB];
            var compactJson = PhoneLinkStore.SerializeView(view);
            Check("폰 화면 파일: 한글 그대로", (compactJson.Contains("\"Text\":\"고정\\n[ ] 하나\""), compactJson.Contains("\\u")), (true, false));
            Check("폰 화면 파일: 한 줄", compactJson.Contains('\n'), false);
            Check("폰 화면 파일: HeldMemos", compactJson.Contains($"\"HeldMemos\":[\"{memoB}\"]"), true);
            Check("폰 화면 파일: Reset 은 yyyy-MM-dd", compactJson.Contains("\"Reset\":\"2026-10-05\""), true);
            Check("폰 화면 파일이 더 작음", Encoding.UTF8.GetByteCount(compactJson) < Encoding.UTF8.GetByteCount(json), true);
            var compactBack = JsonSerializer.Deserialize(compactJson, AppJsonContext.Default.PhoneView);
            Check("한 줄 파일도 다시 읽힘", (compactBack?.Memos[0].Text, compactBack?.HeldMemos.SingleOrDefault()), ((string?)"고정\n[ ] 하나", (Guid?)memoB));
            Check("data.json 꼴은 그대로(들여쓰기)", JsonSerializer.Serialize(new AppData(), AppJsonContext.Default.AppData).Contains("\n"), true);
            view.HeldMemos = [];

            var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.PhoneView);
            Check("메모가 오감", (back?.Memos.Count, back?.Memos[0].Text, back?.Memos[0].Reset), ((int?)3, "고정\n[ ] 하나", (DateOnly?)Mon));
            Check("연결이 오감", back?.Routines[0].MemoId, (Guid?)memoB);

            var inbox = PhoneLinkStore.ParseInbox(
                "{\"Version\":1,\"Ops\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"Kind\":\"memo-check\",\"Target\":\"" + memoA +
                "\",\"Text\":\"단가표 확인\",\"Line\":2,\"Checked\":true,\"At\":\"2026-10-05T09:00:00\",\"Day\":\"2026-10-05\"}]}");
            var inOp = inbox?.Ops.SingleOrDefault();
            Check("inbox 의 메모 체크를 읽음", (inOp?.Kind, inOp?.Line, inOp?.Checked), ((string?)"memo-check", (int?)2, (bool?)true));
            var oldInbox = PhoneLinkStore.ParseInbox(
                "{\"Ops\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"Kind\":\"done\",\"At\":\"2026-10-05T09:00:00\",\"Day\":\"2026-10-05\"}]}");
            Check("옛 inbox 는 Line·Checked 없음", (oldInbox?.Ops[0].Line, oldInbox?.Ops[0].Checked), ((int?)null, (bool?)null));
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 메모 체크 파일 시험 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = Path.Combine(DataStore.Directory, "phonememotest");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var store = new MemoStore(root, () => true, () => new DateTime(2026, 10, 5, 9, 0, 0));
        string Body(Guid id) => store.Load(id)!.Doc.Body.Replace("\r\n", "\n");

        var memo = store.Create("# 배포 절차 #AK넷\n[ ] 엑셀 받기\n[ ] 단가표 확인\n  - [ ] 배포\n[ ] 단가표 확인")!;
        var id = memo.Doc.Id;
        store.SetTags(id, ["정산"]);
        var trashed = store.Create("지울 메모\n[ ] 하나")!;
        store.Delete(trashed.Doc.Id);

        // ── 폰 화면: 실제 파일에서
        {
            var cache = new PhoneMemoCache();
            var memos = cache.Build(store.Memos, store.Load);
            Check("휴지통 메모는 폰 화면에 없음", memos.Select(m => m.Id).SequenceEqual([id]), true);
            Check("머리말 태그 + 본문 태그", string.Join(",", memos[0].Tags), "정산,AK넷");
            Check("본문 그대로(\\n)", memos[0].Text, "# 배포 절차 #AK넷\n[ ] 엑셀 받기\n[ ] 단가표 확인\n  - [ ] 배포\n[ ] 단가표 확인");
        }

        // ── 줄 번호가 맞음
        Check("줄 번호가 맞으면 그 줄을 체크", store.SetLineChecked(id, "단가표 확인", 2, true), MemoCheckResult.Applied);
        Check("그 줄만 바뀜", Body(id), "# 배포 절차 #AK넷\n[ ] 엑셀 받기\n[x] 단가표 확인\n  - [ ] 배포\n[ ] 단가표 확인");
        Check("머리말은 그대로", store.Load(id)!.Doc.Tags?.SingleOrDefault(), "정산");
        Check("이미 그 상태면 건너뜀", store.SetLineChecked(id, "단가표 확인", 2, true), MemoCheckResult.Skipped);
        Check("같은 글이 둘이어도 줄 번호가 맞으면 그 줄", store.SetLineChecked(id, "단가표 확인", 4, true), MemoCheckResult.Applied);
        Check("둘째 줄이 바뀜", Body(id).EndsWith("\n[x] 단가표 확인"), true);
        Check("들여쓴 체크도 글로 찾음", store.SetLineChecked(id, "배포", 3, true), MemoCheckResult.Applied);
        Check("들여쓰기는 그대로", Body(id).Contains("\n  - [x] 배포\n"), true);

        // ── 폰이 본 뒤 PC 에서 위에 줄을 더함 → 같은 글을 찾는다
        {
            var file = store.Load(id)!;
            store.Save(id, "# 배포 절차 #AK넷\n오늘 메모 한 줄\n[ ] 엑셀 받기\n[x] 단가표 확인\n  - [x] 배포\n[x] 단가표 확인", file.Rev);
        }
        Check("줄이 밀려도 같은 글을 찾음", store.SetLineChecked(id, "엑셀 받기", 1, true), MemoCheckResult.Applied);
        Check("밀린 줄이 체크됨", Body(id), "# 배포 절차 #AK넷\n오늘 메모 한 줄\n[x] 엑셀 받기\n[x] 단가표 확인\n  - [x] 배포\n[x] 단가표 확인");
        Check("같은 글이 둘이고 줄이 틀리면 첫째", store.SetLineChecked(id, "단가표 확인", 2, false), MemoCheckResult.Applied);
        Check("첫째만 풀림", Body(id), "# 배포 절차 #AK넷\n오늘 메모 한 줄\n[x] 엑셀 받기\n[ ] 단가표 확인\n  - [x] 배포\n[x] 단가표 확인");

        // ── 맞는 줄이 없음 · 메모가 없음 · 휴지통
        var rev = store.Load(id)!.Rev;
        Check("맞는 줄이 없으면 건너뜀", store.SetLineChecked(id, "없는 줄", 2, true), MemoCheckResult.Skipped);
        Check("체크 줄이 아닌 같은 글은 건드리지 않음", store.SetLineChecked(id, "오늘 메모 한 줄", 1, true), MemoCheckResult.Skipped);
        Check("건너뛰면 파일 그대로", store.Load(id)!.Rev, rev);
        Check("없는 메모는 건너뜀", store.SetLineChecked(Guid.NewGuid(), "단가표 확인", 2, true), MemoCheckResult.Skipped);
        Check("휴지통 메모는 건너뜀", store.SetLineChecked(trashed.Doc.Id, "하나", 1, true), MemoCheckResult.Skipped);
        Check("폴더를 기다리는 중이면 미룸",
            new MemoStore(root, () => false, () => DateTime.Now).SetLineChecked(id, "단가표 확인", 3, true), MemoCheckResult.Deferred);

        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(store.Load(id)!.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check("파일이 잠겼으면 미룸", store.SetLineChecked(id, "단가표 확인", 3, true), MemoCheckResult.Deferred);
            Check("잠겼던 동안 바뀌지 않음", store.Load(id)!.Rev, rev);
        }

        // ── 매일 초기화 메모: 지난 날 체크는 건너뛰고, 그 날 몫 초기화 전이면 미룬다
        {
            var daily = store.Create("아침 점검\n[ ] 창문\n[ ] 화분")!.Doc.Id;
            store.SetDaily(daily, true, Mon);
            Check("초기화한 날의 체크는 넣음", store.SetLineChecked(daily, "창문", 1, true, Mon), MemoCheckResult.Applied);
            Check("아직 그 날 몫을 안 풀었으면 미룸", store.SetLineChecked(daily, "화분", 2, true, Tue), MemoCheckResult.Deferred);
            store.ResetDaily(Tue);
            Check("풀고 나면 넣음", store.SetLineChecked(daily, "화분", 2, true, Tue), MemoCheckResult.Applied);
            Check("어제 체크가 늦게 오면 건너뜀", store.SetLineChecked(daily, "창문", 1, true, Mon), MemoCheckResult.Skipped);
            Check("매일 메모 본문", Body(daily), "아침 점검\n[ ] 창문\n[x] 화분");
        }

        // ── PC 메모 창이 쓰는 중이면 남겨 두었다가 다음에 — 덮어쓰지도, 충돌 사본을 만들지도 않는다
        {
            var data = new AppData { LastLogicalDate = Mon };
            var editor = new MemoEditorState(store);
            editor.Open(id);
            Func<Guid, bool>? held = m => editor.CurrentId == m && (editor.IsDirty || editor.IsEditing);
            var target = new PhoneMemoTarget(store, () => held);
            var count = store.Memos.Count;

            editor.IsEditing = true;
            var op = MemoCheckOp(id, "단가표 확인", 3, true, Mon, 9);
            var first = PhoneLinkEngine.Apply(data, [op], target);
            Check("쓰기 모드면 미룸 (처리했다고 적지 않음)", (first.MemoChecked, data.PhoneOpsApplied.Count), (0, 0));
            Check("쓰기 모드면 파일 그대로", store.Load(id)!.Rev, rev);
            Check("쓰기 모드라 남긴 체크로 셈 (메모 창 띠)", first.HeldFor(id), 1);

            editor.Edit(editor.Body + "\n새로 쓴 줄");
            Check("저장 안 된 글이 있어도 미룸", PhoneLinkEngine.Apply(data, [op], target).MemoChecked, 0);
            Check("내 글은 그대로 저장됨", editor.Flush()!.Outcome, SaveOutcome.Saved);
            editor.IsEditing = false;

            var second = PhoneLinkEngine.Apply(data, [op], target);
            Check("놓은 뒤 다시 해 보면 들어감", (second.MemoChecked, data.PhoneOpsApplied.Count), (1, 1));
            Check("쓴 글과 폰 체크가 함께 남음", Body(id).EndsWith("[x] 단가표 확인\n  - [x] 배포\n[x] 단가표 확인\n새로 쓴 줄"), true);

            editor.OnExternalChange();
            Check("읽기 모드 메모 창은 새 글을 다시 읽음", editor.Body.Replace("\r\n", "\n").Contains("\n[x] 단가표 확인\n  -"), true);
            editor.ToggleCheck(2);
            Check("그 뒤 PC 에서 체크해도 충돌 없음", (Body(id).Contains("\n[ ] 엑셀 받기\n"), store.Memos.Count), (true, count));

            held = null;   // 메모 창이 닫힘
            var closed = MemoCheckOp(id, "엑셀 받기", 2, true, Mon, 10);
            Check("메모 창이 없으면 바로 들어감", PhoneLinkEngine.Apply(data, [closed], target).MemoChecked, 1);
        }

        Directory.Delete(root, true);
    }

    /// <summary>파일이 디스크에서 같은 파일인지 가르는 번호(볼륨 + 파일 인덱스).</summary>
    private static string FileIdentity(string path)
    {
        // 윈도 밖(로컬에서 돌려 볼 때)에는 이 번호를 얻을 길이 없다. CI(윈도)에서 제대로 본다.
        if (!OperatingSystem.IsWindows()) return "?";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out var info)) return "?";
        return $"{info.VolumeSerialNumber:X}-{info.FileIndexHigh:X}-{info.FileIndexLow:X}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(IntPtr handle, out ByHandleFileInformation info);
}
