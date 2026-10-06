using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Flow.Models;
using Flow.Services;

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
            var future = Op("rename", Mon, 9, text: "새 이름");
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

        view.Tasks.Add(new PhoneViewItem { Id = Guid.NewGuid(), Title = "새 일" });
        Check("내용이 바뀌면 씀", store.WriteView(view), true);

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

    /// <summary>파일이 디스크에서 같은 파일인지 가르는 번호(볼륨 + 파일 인덱스).</summary>
    private static string FileIdentity(string path)
    {
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
