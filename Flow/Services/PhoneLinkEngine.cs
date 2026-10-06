using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Models;

namespace Flow.Services;

/// <summary>폰에서 온 일을 반영한 결과.</summary>
public readonly record struct PhoneApplyResult(int Added, int Checked, int Unchecked, int Skipped)
{
    public static readonly PhoneApplyResult None = new(0, 0, 0, 0);

    /// <summary>화면에 보이는 것이 바뀌었는지.</summary>
    public int Changed => Added + Checked + Unchecked;

    /// <summary>
    /// 저장할 것이 생겼는지. 건너뛴 것도 '처리했음'을 적어 둬야 하므로 저장 대상이다 —
    /// 안 적으면 폰이 그 일을 inbox 에서 영영 못 지운다.
    /// </summary>
    public bool NeedsSave => Changed > 0 || Skipped > 0;
}

/// <summary>
/// 폰과 주고받는 규칙. 파일을 모르는 순수 함수라 시간과 데이터를 넣어 시험할 수 있다.
///
/// 핵심은 '두 번 해도 한 번 한 것과 같게'다. 클라우드는 같은 파일을 늦게, 여러 번,
/// 순서를 바꿔 가며 내려 줄 수 있고, PC도 두 대일 수 있다. 그래서
/// - 반영한 일의 번호를 data.json 에 남겨 두 번 하지 않고,
/// - 그 기록이 사라져도 결과가 같도록 일 자체를 '뒤집기'가 아니라 '이렇게 맞추기'로 만든다.
///   (체크 = 끝난 상태로 맞추기, 추가 = 그 번호의 항목이 없을 때만 만들기)
/// </summary>
public static class PhoneLinkEngine
{
    /// <summary>
    /// 반영 기록을 이만큼 들고 있는다. 폰은 이 기록을 보고 inbox 를 비우므로
    /// 폰을 이보다 오래 안 열면 그 사이 일이 한 번 더 반영될 수 있다 — 그래도 결과는 같다.
    /// </summary>
    public const int AppliedRetentionDays = 45;

    /// <summary>
    /// inbox 의 일들을 <see cref="AppData.LastLogicalDate"/>(아직 정산하지 않은 날) 기준으로 반영한다.
    ///
    /// PC를 켤 때는 롤오버 <b>전에</b> 한 번, <b>뒤에</b> 한 번 부른다.
    /// 월요일 밤 폰에서 한 체크를 화요일 아침에 받으면, 롤오버 전이라야 월요일 몫으로 들어가
    /// 월요일 결산과 연속 기록에 잡힌다. 화요일에 한 일은 롤오버 뒤에 들어간다.
    /// </summary>
    public static PhoneApplyResult Apply(AppData data, IEnumerable<PhoneOp>? ops)
    {
        if (ops is null) return PhoneApplyResult.None;

        var openDay = data.LastLogicalDate;
        var applied = new HashSet<Guid>(data.PhoneOpsApplied.Select(a => a.Id));

        int added = 0, checkedCount = 0, uncheckedCount = 0, skipped = 0;

        foreach (var op in ops.Where(o => !applied.Contains(o.Id)).OrderBy(o => o.At).ThenBy(o => o.Id))
        {
            // PC가 아직 그 날에 오지 않았다. 롤오버 뒤에 다시 본다.
            if (op.Day > openDay) continue;

            Outcome outcome;
            switch (op.Kind)
            {
                case PhoneOp.Add: outcome = ApplyAdd(data, op); break;
                case PhoneOp.Done: outcome = ApplyCheck(data, op, done: true); break;
                case PhoneOp.Undone: outcome = ApplyCheck(data, op, done: false); break;

                // 새 버전 폰이 보낸, 이 PC가 모르는 일. 손대지도 적지도 않는다 —
                // 적어 버리면 폰이 지워서 새 버전 PC조차 영영 못 받는다.
                default: continue;
            }

            switch (outcome)
            {
                case Outcome.Added: added++; break;
                case Outcome.Checked: checkedCount++; break;
                case Outcome.Unchecked: uncheckedCount++; break;
                default: skipped++; break;
            }

            data.PhoneOpsApplied.Add(new AppliedPhoneOp { Id = op.Id, Day = openDay });
            applied.Add(op.Id);
        }

        var cutoff = openDay.AddDays(-AppliedRetentionDays);
        data.PhoneOpsApplied.RemoveAll(a => a.Day < cutoff);

        return new PhoneApplyResult(added, checkedCount, uncheckedCount, skipped);
    }

    private enum Outcome { Added, Checked, Unchecked, Skipped }

    private static Outcome ApplyAdd(AppData data, PhoneOp op)
    {
        // 같은 추가가 두 번 왔다(반영 기록이 사라진 경우 등). 이미 있으면 하나로 족하다.
        if (data.Routines.Any(r => r.Id == op.Id) || data.Tasks.Any(t => t.Id == op.Id)) return Outcome.Skipped;

        var input = op.Text?.Trim();
        if (string.IsNullOrEmpty(input)) return Outcome.Skipped;

        // 적은 그 순간 기준으로 읽는다. 월요일 밤에 적은 "내일 회의"는 화요일이다 —
        // PC가 수요일에 받았다고 목요일이 되면 안 된다.
        var parsed = QuickAddParser.Parse(input, op.Day, assumeRoutine: false, TimeOnly.FromDateTime(op.At));
        var title = ItemFactory.TitleOf(parsed, input);

        if (parsed.IsRoutine)
        {
            data.Routines.Add(ItemFactory.CreateRoutine(parsed, title, data.Routines.Count, op.Id));
        }
        else
        {
            // 폰에는 탭이 하나라 '예정 탭이면 내일로' 같은 규칙이 없다. 날짜를 안 적었으면 비워 둔다.
            data.Tasks.Add(ItemFactory.CreateTask(parsed, title, parsed.Due, op.Day, data.Tasks.Count, op.Id));
        }

        return Outcome.Added;
    }

    private static Outcome ApplyCheck(AppData data, PhoneOp op, bool done)
    {
        if (op.Target is not { } target) return Outcome.Skipped;

        if (data.Routines.FirstOrDefault(r => r.Id == target) is { } routine)
        {
            // 루틴은 그 날 몫만 체크할 수 있다. 이미 정산이 끝난 날을 고치면
            // 히스토리와 연속 기록을 다시 계산해야 하는데, 그건 PC에서도 안 되는 일이다.
            if (op.Day != data.LastLogicalDate) return Outcome.Skipped;
            if (!DayEngine.IsScheduled(routine, op.Day)) return Outcome.Skipped;
            if (routine.DoneToday == done) return Outcome.Skipped;

            if (done) DayEngine.CompleteRoutine(routine, op.Day);
            else DayEngine.UncompleteRoutine(routine);

            return done ? Outcome.Checked : Outcome.Unchecked;
        }

        if (data.Tasks.FirstOrDefault(t => t.Id == target) is { } task)
        {
            if (task.Done == done) return Outcome.Skipped;

            task.Done = done;
            task.CompletedDate = done ? op.Day : null;

            return done ? Outcome.Checked : Outcome.Unchecked;
        }

        // PC에서 지운 항목이다. 할 일이 없다.
        return Outcome.Skipped;
    }

    /// <summary>폰이 보여줄 오늘 화면. PC의 '오늘' 탭과 같은 항목, 같은 순서.</summary>
    public static PhoneView BuildView(AppData data, DateOnly today, DateTime now)
    {
        var view = new PhoneView
        {
            GeneratedAt = now,
            Today = today,
            DayStartHour = data.Settings.DayStartHour,
            AppliedOps = data.PhoneOpsApplied.Select(a => a.Id).ToList()
        };

        foreach (var routine in data.Routines.OrderBy(r => r.Order))
        {
            if (!DayEngine.IsScheduled(routine, today)) continue;

            view.Routines.Add(new PhoneViewItem
            {
                Id = routine.Id,
                Title = routine.Title,
                Done = routine.DoneToday,
                Time = routine.Time,
                Streak = routine.Streak
            });
        }

        var ordered = data.Tasks
            .OrderByDescending(t => (int)t.Priority)
            .ThenBy(t => t.Due ?? DateOnly.MaxValue)
            .ThenBy(t => t.Order);

        foreach (var task in ordered)
        {
            var bucket = DayEngine.BucketOf(task, today);
            if (bucket is TaskBucket.Today or TaskBucket.DoneToday) view.Tasks.Add(ToViewItem(task));
        }

        // 예정은 PC의 '예정' 탭처럼 가까운 날짜부터 늘어놓는다.
        foreach (var task in data.Tasks
                     .Where(t => DayEngine.BucketOf(t, today) == TaskBucket.Upcoming)
                     .OrderBy(t => t.Due)
                     .ThenBy(t => t.DueTime ?? TimeOnly.MaxValue)
                     .ThenByDescending(t => (int)t.Priority)
                     .ThenBy(t => t.Order))
        {
            view.Upcoming.Add(ToViewItem(task));
        }

        return view;
    }

    private static PhoneViewItem ToViewItem(TaskItem task) => new()
    {
        Id = task.Id,
        Title = task.Title,
        Done = task.Done,
        Time = task.DueTime,
        Priority = task.Priority,
        Due = task.Due,
        CarryOverCount = task.CarryOverCount
    };
}
