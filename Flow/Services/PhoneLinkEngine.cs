using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 폰에서 온 일을 반영한 결과. MemoChecked 는 고쳐 쓴 메모 체크 줄 수.
/// Held 는 PC 메모 창이 쓰는 중이라 이번에 못 넣고 남긴 메모 일(체크·고치기 등) 수(메모 id → 개수). 없으면 null.
/// 메모 고치기: MemoSaved = 본문을 고침(체크 모두 풀기 포함), MemoCreated = 새 메모(그 사이 지워진 메모에 온 고치기 포함),
/// MemoConflicts = 충돌 사본, MemoHeader = 고정·매일 초기화, MemoDeleted = 휴지통으로, MemoLinked = 루틴·할 일의 연결·끊기.
/// </summary>
public readonly record struct PhoneApplyResult(int Added, int Checked, int Unchecked, int Skipped, int Edited = 0, int Deleted = 0,
    int MemoChecked = 0, IReadOnlyDictionary<Guid, int>? Held = null,
    int MemoSaved = 0, int MemoCreated = 0, int MemoConflicts = 0, int MemoHeader = 0, int MemoDeleted = 0, int MemoLinked = 0)
{
    public static readonly PhoneApplyResult None = new(0, 0, 0, 0);

    /// <summary>쓰는 중이라 남긴 그 메모의 일 수.</summary>
    public int HeldFor(Guid memoId) => Held is not null && Held.TryGetValue(memoId, out var count) ? count : 0;

    /// <summary>메모 파일을 고쳤는지(만들기·지우기 포함). 목록과 읽기 모드 메모 창이 다시 읽는다.</summary>
    public bool MemoFilesChanged => MemoChecked + MemoSaved + MemoCreated + MemoConflicts + MemoHeader + MemoDeleted > 0;

    /// <summary>화면에 보이는 것이 바뀌었는지.</summary>
    public int Changed => Added + Checked + Unchecked + Edited + Deleted + MemoChecked
                          + MemoSaved + MemoCreated + MemoConflicts + MemoHeader + MemoDeleted + MemoLinked;

    public static PhoneApplyResult operator +(PhoneApplyResult a, PhoneApplyResult b) => new(
        a.Added + b.Added, a.Checked + b.Checked, a.Unchecked + b.Unchecked,
        a.Skipped + b.Skipped, a.Edited + b.Edited, a.Deleted + b.Deleted, a.MemoChecked + b.MemoChecked,
        MergeHeld(a.Held, b.Held),
        a.MemoSaved + b.MemoSaved, a.MemoCreated + b.MemoCreated, a.MemoConflicts + b.MemoConflicts,
        a.MemoHeader + b.MemoHeader, a.MemoDeleted + b.MemoDeleted, a.MemoLinked + b.MemoLinked);

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

/// <summary>휴대폰 메모 고치기가 들어가서(Applied) 무엇이 됐는지.</summary>
public enum PhoneMemoChange { None, Checked, Saved, Created, Conflict, Header, Deleted }

/// <summary>
/// 휴대폰 메모 고치기 하나의 결과. Status 는 메모 체크와 같은 뜻(Applied · Skipped · Deferred · Held),
/// Change 는 들어갔을 때 무엇이 됐는지, NewMemo 는 새로 생긴 메모(새 메모·충돌 사본)의 id.
/// </summary>
public readonly record struct PhoneMemoResult(MemoCheckResult Status, PhoneMemoChange Change = PhoneMemoChange.None, Guid? NewMemo = null)
{
    public static readonly PhoneMemoResult Skipped = new(MemoCheckResult.Skipped);
    public static readonly PhoneMemoResult Deferred = new(MemoCheckResult.Deferred);
    public static readonly PhoneMemoResult Held = new(MemoCheckResult.Held);
}

/// <summary>
/// 폰의 메모 체크·고치기를 받아 넣을 곳. 파일을 아는 쪽(MemoStore, 쓰는 중인 메모를 아는 PhoneMemoTarget)이 맡는다.
/// day 는 폰에서 누른 논리적 날짜 — 매일 초기화 메모가 그 날 몫인지 가린다.
/// </summary>
public interface IPhoneMemoTarget
{
    MemoCheckResult SetLineChecked(Guid id, string text, int? line, bool isChecked, DateOnly? day = null);

    /// <summary>
    /// 본문 전체를 맞춘다. baseRev 가 null 이면 그 id 로 새 메모(같은 글로 이미 있으면 건너뜀, 다른 글로 있으면 충돌 사본).
    /// 판이 같으면 덮고(같은 글이면 건너뜀), 다르면 원본은 두고 충돌 사본을, 그 사이 지워졌으면 새 메모를 만든다 —
    /// 둘 다 id 는 opId(같은 일이 두 번 와도 하나). day(폰에서 저장한 논리적 날짜)가 있고 매일 초기화 메모의 그 날 몫
    /// 초기화가 아직이면 Deferred — 먼저 넣으면 곧 이어질 초기화가 휴대폰 글의 체크를 풀어 버린다.
    /// </summary>
    PhoneMemoResult Save(Guid id, string text, string? baseRev, Guid opId, DateOnly? day = null);

    PhoneMemoResult SetPinned(Guid id, bool pinned);

    /// <summary>켜면 그 날(day)을 초기화한 날로 적어 켠 날은 풀리지 않는다.</summary>
    PhoneMemoResult SetDaily(Guid id, bool daily, DateOnly day);

    PhoneMemoResult UncheckAll(Guid id);

    /// <summary>휴지통으로. 이미 없으면 건너뜀.</summary>
    PhoneMemoResult Delete(Guid id);

    /// <summary>그 메모가 지금 메모 목록에 있는지(휴지통 제외). 연결할 수 있는지 가린다.</summary>
    bool Exists(Guid id);
}

/// <summary>
/// 메모 창이 쓰는 중으로 들고 있는 메모(쓰기 모드·저장 안 된 글)는 Held 로 돌려주고, 나머지는 MemoStore 에 맡긴다.
/// 쓰는 중인 파일을 고치면 다음 저장이 충돌 사본이 되므로, 놓은 뒤에 다시 해 본다.
/// held 는 부를 때마다 묻는다 — 메모 창이 열리고 닫히며 바뀐다.
/// </summary>
public sealed class PhoneMemoTarget(MemoStore store, Func<Func<Guid, bool>?> held) : IPhoneMemoTarget
{
    /// <summary>휴대폰 글과 PC 글이 겹쳤을 때 휴대폰 글로 만든 새 메모의 제목 끝.</summary>
    public const string ConflictSuffix = " (휴대폰 충돌 사본)";

    private bool IsHeld(Guid id) => held()?.Invoke(id) == true;

    public MemoCheckResult SetLineChecked(Guid id, string text, int? line, bool isChecked, DateOnly? day = null)
        => IsHeld(id)
            ? MemoCheckResult.Held
            : store.SetLineChecked(id, text, line, isChecked, day);

    public PhoneMemoResult Save(Guid id, string text, string? baseRev, Guid opId, DateOnly? day = null)
    {
        if (IsHeld(id)) return PhoneMemoResult.Held;

        // 새로 생길 메모(충돌 사본·지워진 뒤 새 메모)의 id. 일의 번호가 메모 id 와 같으면(그렇게 보내는 폰) 새로 뽑는다 —
        // 같은 id 면 '이미 있음'으로 보여 휴대폰 글이 버려진다.
        var copyId = opId == id ? Guid.NewGuid() : opId;

        if (baseRev is null)
        {
            var made = store.CreateWithId(id, text);
            if (made != MemoCheckResult.Skipped) return made == MemoCheckResult.Applied ? new(made, PhoneMemoChange.Created, id) : new(made);

            // 그 id 의 메모가 이미 있다. 같은 글이면 같은 일이 두 번 온 것. 다른 글이면(그 사이 PC 에서 고침 ·
            // 휴대폰이 합친 저장의 앞 글이 벌써 들어감 등) 휴대폰 글을 버리지 않고 충돌 사본으로 — 사본 id 가 opId 라 두 번 와도 하나.
            // 휴지통에만 있으면 예전처럼 건너뛴다(PC 에서 지운 메모를 살리지 않는다).
            if (store.Load(id) is not { } existing || existing.Rev == MemoFormat.RevOf(text)) return PhoneMemoResult.Skipped;
            return ResultOf(store.Save(id, text, "", ConflictSuffix, copyId));
        }

        // 매일 초기화 메모의 그 날 몫 초기화가 아직이면 기다린다(메모 체크와 같다). 초기화가 먼저 되면
        // 휴대폰이 '새 하루라 풀어 보인' 글을 보고 고친 판과 맞아 깨끗이 덮인다.
        if (day is { } d && store.IsDailyDue(id, d)) return PhoneMemoResult.Deferred;

        // 지금 본문과 같은 글이면 할 것이 없다(같은 고치기가 두 번 옴·양쪽에서 같게 고침). 판이 달라도 충돌 사본을 만들지 않는다.
        if (store.Load(id) is { } current && current.Rev == MemoFormat.RevOf(text)) return PhoneMemoResult.Skipped;

        return ResultOf(store.Save(id, text, baseRev, ConflictSuffix, copyId, day));
    }

    private static PhoneMemoResult ResultOf(SaveResult saved)
        => saved.Outcome switch
        {
            SaveOutcome.Saved => new(MemoCheckResult.Applied, PhoneMemoChange.Saved),
            SaveOutcome.Conflict => new(MemoCheckResult.Applied, PhoneMemoChange.Conflict, saved.CopyId),
            SaveOutcome.Missing => new(MemoCheckResult.Applied, PhoneMemoChange.Created, saved.CopyId),
            SaveOutcome.Unchanged => PhoneMemoResult.Skipped,
            _ => PhoneMemoResult.Deferred,
        };

    public PhoneMemoResult SetPinned(Guid id, bool pinned)
        => Change(id, PhoneMemoChange.Header, doc => doc.Pinned == pinned ? null : doc with { Pinned = pinned });

    // 이미 켜져 있으면 초기화 날을 건드리지 않는다 — 오늘 몫을 풀기 전에 날짜만 오늘로 적으면 오늘은 안 풀린다.
    public PhoneMemoResult SetDaily(Guid id, bool daily, DateOnly day)
        => Change(id, PhoneMemoChange.Header,
            doc => doc.Daily == daily ? null : doc with { Daily = daily, Reset = daily ? day : doc.Reset });

    public PhoneMemoResult UncheckAll(Guid id)
        => Change(id, PhoneMemoChange.Saved, doc =>
        {
            var body = MemoFormat.UncheckAll(doc.Body);
            return body == doc.Body ? null : doc with { Body = body };
        });

    public PhoneMemoResult Delete(Guid id)
    {
        if (IsHeld(id)) return PhoneMemoResult.Held;
        var result = store.MoveToTrash(id);
        return new(result, result == MemoCheckResult.Applied ? PhoneMemoChange.Deleted : PhoneMemoChange.None);
    }

    public bool Exists(Guid id) => store.Memos.Any(m => m.Id == id) || store.Load(id) is not null;

    private PhoneMemoResult Change(Guid id, PhoneMemoChange change, Func<MemoDocument, MemoDocument?> edit)
    {
        if (IsHeld(id)) return PhoneMemoResult.Held;
        var result = store.Update(id, edit);
        return new(result, result == MemoCheckResult.Applied ? change : PhoneMemoChange.None);
    }
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

            // 본문(판)도 머리말 값도 그대로면 지난번 것을 다시 쓴다 — 매번 모든 메모를 다시 요약하지 않게.
            var memo = known.Sent is { } sent && sent.Rev == known.Rev && SameHeader(sent, summary)
                ? sent
                : PhoneLinkEngine.ToPhoneMemo(summary, known.Rev, known.Body);
            _bodies[summary.Id] = (known.Rev, known.Body, memo);
            result.Add(memo);
        }

        foreach (var gone in _bodies.Keys.Where(id => !live.Contains(id)).ToList()) _bodies.Remove(gone);
        return result;
    }

    private static bool SameHeader(PhoneMemo sent, MemoSummary summary)
        => sent.Pinned == summary.Pinned && sent.Daily == summary.Daily && sent.Reset == summary.Reset
           && sent.Updated == summary.Updated && sent.Tags.SequenceEqual(summary.Tags);
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
    /// 메모의 일(체크·고치기·고정·매일·모두 풀기·삭제·연결)은 memos 가 있을 때만 넣는다. 없으면 모르는 갈래처럼 손대지도 적지도 않는다.
    /// 지금 못 하는 메모 일(Deferred · Held)도 적지 않는다 — inbox 에 남아 다음에 다시 온다.
    /// 한 메모의 일을 하나 남겼으면 이번에 그 메모에 온 뒤의 일도 모두 남긴다 — 체크 뒤 해제, 고치기 뒤 고정이
    /// 뒤의 것만 먼저 들어갔다가 다음에 앞의 것이 들어가 거꾸로 끝나지 않게(순서는 At 순).
    /// 연결(memo-link)은 그 메모를 만드는 일(Base 없는 memo-save)이 inbox 에 남아 있는 동안 기다린다 — 순서와 상관없이.
    /// PC 가 쓰는 중이라 남긴 것(Held)은 메모마다 세어 결과의 Held 로 돌려준다.
    /// </summary>
    public static PhoneApplyResult Apply(AppData data, IEnumerable<PhoneOp>? ops, IPhoneMemoTarget? memos = null)
    {
        if (ops is null) return PhoneApplyResult.None;

        var openDay = data.LastLogicalDate;
        var applied = new HashSet<Guid>(data.PhoneOpsApplied.Select(a => a.Id));

        int added = 0, checkedCount = 0, uncheckedCount = 0, skipped = 0, edited = 0, deleted = 0, memoChecked = 0;
        int memoSaved = 0, memoCreated = 0, memoConflicts = 0, memoHeader = 0, memoDeleted = 0, memoLinked = 0;

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

        var pending = ops.Where(o => !applied.Contains(o.Id)).ToList();

        // 아직 못 넣은 '새 메모 만들기'(Base 없는 memo-save)가 만들 메모. 그 메모에 잇는 연결은 만들기가 들어갈 때까지 기다린다 —
        // '새 메모로'는 만들기와 연결이 같은 순간에 오고, 연결이 먼저 들어가면 없는 메모라 건너뛰어 영영 끊긴다.
        var creating = new HashSet<Guid>(pending
            .Where(o => o.Kind == PhoneOp.MemoSave && o.Base is null && o.Target is not null)
            .Select(o => o.Target!.Value));

        // 같은 순간이면 메모 일(만들기 등)을 연결보다 먼저.
        foreach (var op in pending.OrderBy(o => o.At).ThenBy(o => o.Kind == PhoneOp.MemoLink ? 1 : 0).ThenBy(o => o.Id))
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
                case PhoneOp.MemoSave:
                case PhoneOp.MemoPin:
                case PhoneOp.MemoDaily:
                case PhoneOp.MemoUncheckAll:
                case PhoneOp.MemoDelete:
                    if (memos is null) continue;
                    if (op.Target is { } memoId && waitingMemos.TryGetValue(memoId, out var wasHeld))
                    {
                        // 앞의 일이 남았다. 이것만 먼저 넣으면 순서가 뒤바뀐다.
                        Wait(memoId, wasHeld);
                        continue;
                    }
                    var memoResult = ApplyMemo(memos, op);
                    if (memoResult.Status is MemoCheckResult.Deferred or MemoCheckResult.Held)
                    {
                        Wait(op.Target!.Value, memoResult.Status == MemoCheckResult.Held);   // 지금은 못 함 — 적지 않는다
                        continue;
                    }
                    outcome = memoResult.Status != MemoCheckResult.Applied ? Outcome.Skipped : memoResult.Change switch
                    {
                        PhoneMemoChange.Checked => Outcome.MemoChecked,
                        PhoneMemoChange.Saved => Outcome.MemoSaved,
                        PhoneMemoChange.Created => Outcome.MemoCreated,
                        PhoneMemoChange.Conflict => Outcome.MemoConflict,
                        PhoneMemoChange.Header => Outcome.MemoHeader,
                        PhoneMemoChange.Deleted => Outcome.MemoDeleted,
                        _ => Outcome.Skipped,
                    };
                    break;

                case PhoneOp.MemoLink:
                    if (memos is null) continue;
                    // 이을 메모를 만드는 일이 아직 남았다('새 메모로'). 먼저 하면 없는 메모라 끊긴다.
                    if (LinkedMemoOf(op) is { } linked && creating.Contains(linked)) continue;
                    outcome = ApplyLink(data, op, memos);
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
                case Outcome.MemoSaved: memoSaved++; break;
                case Outcome.MemoCreated: memoCreated++; break;
                case Outcome.MemoConflict: memoConflicts++; break;
                case Outcome.MemoHeader: memoHeader++; break;
                case Outcome.MemoDeleted: memoDeleted++; break;
                case Outcome.MemoLinked: memoLinked++; break;
                default: skipped++; break;
            }

            data.PhoneOpsApplied.Add(new AppliedPhoneOp { Id = op.Id, Day = openDay });
            applied.Add(op.Id);
            if (op.Kind == PhoneOp.MemoSave && op.Base is null && op.Target is { } created) creating.Remove(created);
        }

        var cutoff = openDay.AddDays(-AppliedRetentionDays);
        data.PhoneOpsApplied.RemoveAll(a => a.Day < cutoff);

        return new PhoneApplyResult(added, checkedCount, uncheckedCount, skipped, edited, deleted, memoChecked, held,
            memoSaved, memoCreated, memoConflicts, memoHeader, memoDeleted, memoLinked);
    }

    private enum Outcome
    {
        Added, Checked, Unchecked, Edited, Deleted, MemoChecked,
        MemoSaved, MemoCreated, MemoConflict, MemoHeader, MemoDeleted, MemoLinked, Skipped
    }

    /// <summary>
    /// 메모 체크·고치기 하나. Deferred · Held 면 지금은 못 한 것(쓰는 중·폴더 기다림·파일 잠김)이라 적지 않는다.
    /// 메모 id 가 없거나 필요한 값이 없거나 틀리면 Skipped.
    /// </summary>
    private static PhoneMemoResult ApplyMemo(IPhoneMemoTarget memos, PhoneOp op)
    {
        if (op.Target is not { } target) return PhoneMemoResult.Skipped;
        switch (op.Kind)
        {
            case PhoneOp.MemoCheck:
                if (op.Checked is not { } isChecked) return PhoneMemoResult.Skipped;
                var checkResult = memos.SetLineChecked(target, op.Text ?? "", op.Line, isChecked, op.Day);
                return new(checkResult, checkResult == MemoCheckResult.Applied ? PhoneMemoChange.Checked : PhoneMemoChange.None);

            case PhoneOp.MemoSave:
                return op.Text is { } text ? memos.Save(target, text, op.Base, op.Id, op.Day) : PhoneMemoResult.Skipped;

            case PhoneOp.MemoPin:
                return BoolOf(op.Text) is { } pinned ? memos.SetPinned(target, pinned) : PhoneMemoResult.Skipped;

            case PhoneOp.MemoDaily:
                return BoolOf(op.Text) is { } daily ? memos.SetDaily(target, daily, op.Day) : PhoneMemoResult.Skipped;

            case PhoneOp.MemoUncheckAll:
                return memos.UncheckAll(target);

            case PhoneOp.MemoDelete:
                return memos.Delete(target);

            default:
                return PhoneMemoResult.Skipped;
        }
    }

    private static bool? BoolOf(string? text) => bool.TryParse(text?.Trim(), out var value) ? value : null;

    /// <summary>memo-link 가 이을 메모 id. 빈 글(끊기)·틀린 글이면 null.</summary>
    private static Guid? LinkedMemoOf(PhoneOp op) => Guid.TryParse(op.Text?.Trim(), out var id) ? id : null;

    /// <summary>
    /// 루틴·할 일의 메모 연결을 그 값으로 맞춘다. 빈 글이면 끊는다. 지금 없는 메모(휴지통·지움·틀린 id)면 건너뛴다 —
    /// 위젯의 '메모 연결'과 같이, 없는 메모에는 잇지 않는다.
    /// </summary>
    private static Outcome ApplyLink(AppData data, PhoneOp op, IPhoneMemoTarget memos)
    {
        if (op.Target is not { } target) return Outcome.Skipped;

        Guid? memoId = null;
        if (!string.IsNullOrWhiteSpace(op.Text))
        {
            if (LinkedMemoOf(op) is not { } id || !memos.Exists(id)) return Outcome.Skipped;
            memoId = id;
        }

        if (data.Routines.FirstOrDefault(r => r.Id == target) is { } routine)
        {
            if (routine.MemoId == memoId) return Outcome.Skipped;
            routine.MemoId = memoId;
            return Outcome.MemoLinked;
        }

        if (data.Tasks.FirstOrDefault(t => t.Id == target) is { } task)
        {
            if (task.MemoId == memoId) return Outcome.Skipped;
            task.MemoId = memoId;
            return Outcome.MemoLinked;
        }

        return Outcome.Skipped;
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

    /// <summary>PC 알림 한 줄: "폰에서 가져옴 · 추가 1 · 메모 고침 2 · 새 메모 1 · 충돌 사본 1 …"(0 인 것은 뺀다).</summary>
    public static string Describe(PhoneApplyResult result)
    {
        var parts = new List<string>();
        if (result.Added > 0) parts.Add($"추가 {result.Added}");
        if (result.Checked > 0) parts.Add($"완료 {result.Checked}");
        if (result.Unchecked > 0) parts.Add($"완료 취소 {result.Unchecked}");
        if (result.Edited > 0) parts.Add($"수정 {result.Edited}");
        if (result.Deleted > 0) parts.Add($"삭제 {result.Deleted}");
        if (result.MemoChecked > 0) parts.Add($"메모 체크 {result.MemoChecked}");
        if (result.MemoSaved > 0) parts.Add($"메모 고침 {result.MemoSaved}");
        if (result.MemoCreated > 0) parts.Add($"새 메모 {result.MemoCreated}");
        if (result.MemoConflicts > 0) parts.Add($"충돌 사본 {result.MemoConflicts}");
        if (result.MemoHeader > 0) parts.Add($"메모 설정 {result.MemoHeader}");
        if (result.MemoDeleted > 0) parts.Add($"메모 삭제 {result.MemoDeleted}");
        if (result.MemoLinked > 0) parts.Add($"메모 연결 {result.MemoLinked}");

        return "폰에서 가져옴 · " + string.Join(" · ", parts);
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
                .ThenBy(m => m.Title, MemoQuery.NameOrder)
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
            Text = MemoFormat.Newlines(body),
            Pinned = summary.Pinned,
            Daily = summary.Daily,
            Reset = summary.Reset,
            Tags = summary.Tags.ToList(),
            Rev = rev,
            Updated = summary.Updated
        };
    }
}
