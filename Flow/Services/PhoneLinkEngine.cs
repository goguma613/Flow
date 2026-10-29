using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 폰에서 온 일을 반영한 결과. MemoChecked 는 고쳐 쓴 메모 체크 줄 수.
/// Held 는 PC 메모 창이 쓰는 중이라 이번에 못 넣고 남긴 메모 체크 수(메모 id → 개수). 없으면 null.
/// </summary>
public readonly record struct PhoneApplyResult(int Added, int Checked, int Unchecked, int Skipped, int Edited = 0, int Deleted = 0,
    int MemoChecked = 0, IReadOnlyDictionary<Guid, int>? Held = null)
{
    public static readonly PhoneApplyResult None = new(0, 0, 0, 0);

    /// <summary>쓰는 중이라 남긴 그 메모의 체크 수.</summary>
    public int HeldFor(Guid memoId) => Held is not null && Held.TryGetValue(memoId, out var count) ? count : 0;

    /// <summary>화면에 보이는 것이 바뀌었는지.</summary>
    public int Changed => Added + Checked + Unchecked + Edited + Deleted + MemoChecked;

    public static PhoneApplyResult operator +(PhoneApplyResult a, PhoneApplyResult b) => new(
        a.Added + b.Added, a.Checked + b.Checked, a.Unchecked + b.Unchecked,
        a.Skipped + b.Skipped, a.Edited + b.Edited, a.Deleted + b.Deleted, a.MemoChecked + b.MemoChecked,
        MergeHeld(a.Held, b.Held));

    private static IReadOnlyDictionary<Guid, int>? MergeHeld(IReadOnlyDictionary<Guid, int>? a, IReadOnlyDictionary<Guid, int>? b)
    {
        if (a is null || a.Count == 0) return b;
        if (b is null || b.Count == 0) return a;
        var merged = new Dictionary<Guid, int>(a);
        foreach (var (id, count) in b) merged[id] = merged.GetValueOrDefault(id) + count;
        return merged;
    }

    /// <summary>
    /// 저장할 것이 생겼는지. 건너뛴 것도 '처리했음'을 적어 둬야 하므로 저장 대상이다 —
    /// 안 적으면 폰이 그 일을 inbox 에서 영영 못 지운다.
    /// </summary>
    public bool NeedsSave => Changed > 0 || Skipped > 0;
}

/// <summary>
/// 폰의 메모 체크를 받아 넣을 곳. 파일을 아는 쪽(MemoStore, 쓰는 중인 메모를 아는 PhoneMemoTarget)이 맡는다.
/// day 는 폰에서 누른 논리적 날짜 — 매일 초기화 메모가 그 날 몫인지 가린다.
/// </summary>
public interface IPhoneMemoTarget
{
    MemoCheckResult SetLineChecked(Guid id, string text, int? line, bool isChecked, DateOnly? day = null);
}

/// <summary>
/// 메모 창이 쓰는 중으로 들고 있는 메모(쓰기 모드·저장 안 된 글)는 Held 로 돌려주고, 나머지는 MemoStore 에 맡긴다.
/// 쓰는 중인 파일을 고치면 다음 저장이 충돌 사본이 되므로, 놓은 뒤에 다시 해 본다.
/// held 는 부를 때마다 묻는다 — 메모 창이 열리고 닫히며 바뀐다.
/// </summary>
public sealed class PhoneMemoTarget(MemoStore store, Func<Func<Guid, bool>?> held) : IPhoneMemoTarget
{
    public MemoCheckResult SetLineChecked(Guid id, string text, int? line, bool isChecked, DateOnly? day = null)
        => held()?.Invoke(id) == true
            ? MemoCheckResult.Held
            : store.SetLineChecked(id, text, line, isChecked, day);
}

/// <summary>
/// 폰 화면에 넣을 메모 본문을 기억해 둔다(id → 판·본문·마지막으로 보낸 것). 판이 바뀐 메모만 다시 읽고, 사라진 메모는 잊는다.
/// 머리말 값(고정·매일·초기화 날·태그·고친 시각)은 요약에서 그대로 가져온다 — 판은 본문만 보므로.
/// </summary>
public sealed class PhoneMemoCache
{
    private readonly Dictionary<Guid, (string Rev, string Body, PhoneMemo Sent)> _bodies = new();

    /// <summary>
    /// 판이 같으면 기억한 본문을 쓴다. 잠깐 읽지 못하는 메모(동기화 중·잠김)는 기억한 것이 있으면 그대로 보낸다 —
    /// 빼 버리면 폰에서 메모가 사라졌다 돌아오고, 열어 보던 화면이 "이제 PC에 없습니다"가 된다.
    /// 한 번도 읽지 못한 메모만 뺀다.
    /// </summary>
    public IReadOnlyList<PhoneMemo> Build(IEnumerable<MemoSummary> memos, Func<Guid, MemoFile?> load)
    {
        var result = new List<PhoneMemo>();
        var live = new HashSet<Guid>();
        foreach (var summary in memos)
        {
            if (!live.Add(summary.Id)) continue;

            var cached = _bodies.TryGetValue(summary.Id, out var known);
            if (summary.Unreadable)
            {
                // 요약의 머리말 값도 믿을 수 없다. 마지막으로 보낸 것을 그대로.
                if (cached) result.Add(known.Sent);
                continue;
            }

            if (!cached || known.Rev != summary.Rev)
            {
                if (load(summary.Id) is not { } file)
                {
                    // 기억한 본문으로 보내고, 판은 옛것으로 두어 다음에 다시 읽게 한다.
                    if (!cached) continue;
                }
                else
                {
                    known = (file.Rev, file.Doc.Body, known.Sent);
                }
            }

            var memo = PhoneLinkEngine.ToPhoneMemo(summary, known.Rev, known.Body);
            _bodies[summary.Id] = (known.Rev, known.Body, memo);
            result.Add(memo);
        }

        foreach (var gone in _bodies.Keys.Where(id => !live.Contains(id)).ToList()) _bodies.Remove(gone);
        return result;
    }
}

/// <summary>
/// 폰과 주고받는 규칙. 파일을 모르는 순수 함수라 시간과 데이터를 넣어 시험할 수 있다.
///
/// 핵심은 '두 번 해도 한 번 한 것과 같게'다. 클라우드는 같은 파일을 늦게, 여러 번,
/// 순서를 바꿔 가며 내려 줄 수 있고, PC도 두 대일 수 있다. 그래서
/// - 반영한 일의 번호를 data.json 에 남겨 두 번 하지 않고,
/// - 그 기록이 사라져도 결과가 같도록 일 자체를 '뒤집기'가 아니라 '이렇게 맞추기'로 만든다.
///   (체크 = 끝난 상태로 맞추기, 추가 = 그 번호의 항목이 없을 때만 만들기,
///    이름·중요도 = 그 값으로 맞추기, 지우기 = 있으면 지우기, 메모 체크 = 그 줄을 그 상태로 맞추기)
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
    ///
    /// 메모 체크는 memos 가 있을 때만 넣는다. 없으면 모르는 갈래처럼 손대지도 적지도 않는다.
    /// 지금 못 하는 메모 체크(Deferred · Held)도 적지 않는다 — inbox 에 남아 다음에 다시 온다.
    /// 한 메모의 체크를 하나 남겼으면 이번에 그 메모에 온 뒤의 체크도 모두 남긴다 — 체크 뒤 해제가
    /// 해제만 먼저 들어갔다가 다음에 체크가 들어가 거꾸로 끝나지 않게(순서는 At 순).
    /// PC 가 쓰는 중이라 남긴 것(Held)은 메모마다 세어 결과의 Held 로 돌려준다.
    /// </summary>
    public static PhoneApplyResult Apply(AppData data, IEnumerable<PhoneOp>? ops, IPhoneMemoTarget? memos = null)
    {
        if (ops is null) return PhoneApplyResult.None;

        var openDay = data.LastLogicalDate;
        var applied = new HashSet<Guid>(data.PhoneOpsApplied.Select(a => a.Id));

        int added = 0, checkedCount = 0, uncheckedCount = 0, skipped = 0, edited = 0, deleted = 0, memoChecked = 0;

        // 이번에 체크를 남긴 메모 → 쓰는 중이라 남겼는지(Held). 그 메모의 뒤 체크도 같은 까닭으로 남긴다.
        var waitingMemos = new Dictionary<Guid, bool>();
        Dictionary<Guid, int>? held = null;
        void Wait(Guid memo, bool isHeld)
        {
            waitingMemos[memo] = isHeld;
            if (!isHeld) return;
            held ??= new Dictionary<Guid, int>();
            held[memo] = held.GetValueOrDefault(memo) + 1;
        }

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
                case PhoneOp.Rename: outcome = ApplyRename(data, op); break;
                case PhoneOp.SetPriority: outcome = ApplyPriority(data, op); break;
                case PhoneOp.Delete: outcome = ApplyDelete(data, op); break;

                case PhoneOp.MemoCheck:
                    if (memos is null) continue;
                    if (op.Target is { } memoId && waitingMemos.TryGetValue(memoId, out var wasHeld))
                    {
                        // 앞의 체크가 남았다. 이것만 먼저 넣으면 순서가 뒤바뀐다.
                        Wait(memoId, wasHeld);
                        continue;
                    }
                    var memoResult = ApplyMemoCheck(memos, op);
                    if (memoResult is MemoCheckResult.Deferred or MemoCheckResult.Held)
                    {
                        Wait(op.Target!.Value, memoResult == MemoCheckResult.Held);   // 지금은 못 함 — 적지 않는다
                        continue;
                    }
                    outcome = memoResult == MemoCheckResult.Applied ? Outcome.MemoChecked : Outcome.Skipped;
                    break;

                // 새 버전 폰이 보낸, 이 PC가 모르는 일. 손대지도 적지도 않는다 —
                // 적어 버리면 폰이 지워서 새 버전 PC조차 영영 못 받는다.
                default: continue;
            }

            switch (outcome)
            {
                case Outcome.Added: added++; break;
                case Outcome.Checked: checkedCount++; break;
                case Outcome.Unchecked: uncheckedCount++; break;
                case Outcome.Edited: edited++; break;
                case Outcome.Deleted: deleted++; break;
                case Outcome.MemoChecked: memoChecked++; break;
                default: skipped++; break;
            }

            data.PhoneOpsApplied.Add(new AppliedPhoneOp { Id = op.Id, Day = openDay });
            applied.Add(op.Id);
        }

        var cutoff = openDay.AddDays(-AppliedRetentionDays);
        data.PhoneOpsApplied.RemoveAll(a => a.Day < cutoff);

        return new PhoneApplyResult(added, checkedCount, uncheckedCount, skipped, edited, deleted, memoChecked, held);
    }

    private enum Outcome { Added, Checked, Unchecked, Edited, Deleted, MemoChecked, Skipped }

    /// <summary>메모 체크. Deferred · Held 면 지금은 못 한 것(쓰는 중·폴더 기다림·파일 잠김)이라 적지 않는다. 메모 id 가 없으면 Skipped.</summary>
    private static MemoCheckResult ApplyMemoCheck(IPhoneMemoTarget memos, PhoneOp op)
    {
        if (op.Target is not { } target || op.Checked is not { } isChecked) return MemoCheckResult.Skipped;
        return memos.SetLineChecked(target, op.Text ?? "", op.Line, isChecked, op.Day);
    }

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

    /// <summary>이름 바꾸기. PC의 우클릭 '이름 바꾸기'와 같이 적은 그대로 쓴다 — 날짜 같은 말을 다시 읽지 않는다.</summary>
    private static Outcome ApplyRename(AppData data, PhoneOp op)
    {
        var title = op.Text?.Trim();
        if (op.Target is not { } target || string.IsNullOrEmpty(title)) return Outcome.Skipped;

        if (data.Routines.FirstOrDefault(r => r.Id == target) is { } routine)
        {
            if (routine.Title == title) return Outcome.Skipped;
            routine.Title = title;
            return Outcome.Edited;
        }

        if (data.Tasks.FirstOrDefault(t => t.Id == target) is { } task)
        {
            if (task.Title == title) return Outcome.Skipped;
            task.Title = title;
            return Outcome.Edited;
        }

        return Outcome.Skipped;
    }

    /// <summary>중요도 바꾸기. 할 일에만 있다.</summary>
    private static Outcome ApplyPriority(AppData data, PhoneOp op)
    {
        if (op.Target is not { } target) return Outcome.Skipped;
        if (!Enum.TryParse<Priority>(op.Text, ignoreCase: true, out var priority) || !Enum.IsDefined(priority))
            return Outcome.Skipped;
        if (data.Tasks.FirstOrDefault(t => t.Id == target) is not { } task) return Outcome.Skipped;
        if (task.Priority == priority) return Outcome.Skipped;

        task.Priority = priority;
        return Outcome.Edited;
    }

    /// <summary>지우기. 이미 없으면 할 일이 없다 — 두 번 와도 같다.</summary>
    private static Outcome ApplyDelete(AppData data, PhoneOp op)
    {
        if (op.Target is not { } target) return Outcome.Skipped;

        if (data.Routines.RemoveAll(r => r.Id == target) > 0) return Outcome.Deleted;
        if (data.Tasks.RemoveAll(t => t.Id == target) > 0) return Outcome.Deleted;

        return Outcome.Skipped;
    }

    /// <summary>
    /// 폰이 보여줄 오늘 화면. PC의 '오늘' 탭과 같은 항목, 같은 순서.
    /// memos 는 폰에 보일 메모(PhoneMemoCache 가 만든다). 고정 먼저, 최근에 고친 순으로 늘어놓고,
    /// 루틴·할 일의 MemoId 는 그 메모가 여기 있을 때만 채운다.
    /// heldMemos 는 PC 메모 창이 쓰는 중으로 들고 있는 메모 — 여기 있는 메모만 HeldMemos 로 알린다.
    /// </summary>
    public static PhoneView BuildView(AppData data, DateOnly today, DateTime now, IReadOnlyList<PhoneMemo>? memos = null,
        IEnumerable<Guid>? heldMemos = null)
    {
        var view = new PhoneView
        {
            GeneratedAt = now,
            Today = today,
            DayStartHour = data.Settings.DayStartHour,
            AppliedOps = data.PhoneOpsApplied.Select(a => a.Id).ToList()
        };

        if (memos is not null)
            view.Memos = memos
                .OrderByDescending(m => m.Pinned)
                .ThenByDescending(m => m.Updated)
                .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        var memoIds = new HashSet<Guid>(view.Memos.Select(m => m.Id));
        Guid? LinkOf(Guid? memoId) => memoId is { } id && memoIds.Contains(id) ? id : null;
        if (heldMemos is not null) view.HeldMemos = heldMemos.Where(memoIds.Contains).Distinct().ToList();

        foreach (var routine in data.Routines.OrderBy(r => r.Order))
        {
            if (!DayEngine.IsScheduled(routine, today)) continue;

            view.Routines.Add(new PhoneViewItem
            {
                Id = routine.Id,
                Title = routine.Title,
                Done = routine.DoneToday,
                Time = routine.Time,
                Streak = routine.Streak,
                MemoId = LinkOf(routine.MemoId)
            });
        }

        var ordered = data.Tasks
            .OrderByDescending(t => (int)t.Priority)
            .ThenBy(t => t.Due ?? DateOnly.MaxValue)
            .ThenBy(t => t.Order);

        foreach (var task in ordered)
        {
            var bucket = DayEngine.BucketOf(task, today);
            if (bucket is TaskBucket.Today or TaskBucket.DoneToday) view.Tasks.Add(ToViewItem(task, LinkOf(task.MemoId)));
        }

        // 예정은 PC의 '예정' 탭처럼 가까운 날짜부터 늘어놓는다.
        foreach (var task in data.Tasks
                     .Where(t => DayEngine.BucketOf(t, today) == TaskBucket.Upcoming)
                     .OrderBy(t => t.Due)
                     .ThenBy(t => t.DueTime ?? TimeOnly.MaxValue)
                     .ThenByDescending(t => (int)t.Priority)
                     .ThenBy(t => t.Order))
        {
            view.Upcoming.Add(ToViewItem(task, LinkOf(task.MemoId)));
        }

        return view;
    }

    private static PhoneViewItem ToViewItem(TaskItem task, Guid? memoId) => new()
    {
        Id = task.Id,
        Title = task.Title,
        Done = task.Done,
        Time = task.DueTime,
        Priority = task.Priority,
        Due = task.Due,
        CarryOverCount = task.CarryOverCount,
        MemoId = memoId
    };

    /// <summary>
    /// 폰에 보낼 메모 하나. 제목은 메모 탭처럼 줄 머리 기호를 뗀 것(비면 "제목 없음"),
    /// 본문은 머리말을 뺀 그대로에 줄바꿈만 \n 으로 맞춘 것 — 체크의 줄 번호가 PC 와 같게.
    /// </summary>
    public static PhoneMemo ToPhoneMemo(MemoSummary summary, string rev, string body)
    {
        var title = MemoFormat.PlainTitle(MemoFormat.TitleOf(body));
        return new PhoneMemo
        {
            Id = summary.Id,
            Title = string.IsNullOrWhiteSpace(title) ? MemoFormat.Untitled : title,
            Text = body.Replace("\r\n", "\n").Replace('\r', '\n'),
            Pinned = summary.Pinned,
            Daily = summary.Daily,
            Reset = summary.Reset,
            Tags = summary.Tags.ToList(),
            Rev = rev,
            Updated = summary.Updated
        };
    }
}
