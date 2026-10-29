using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Models;
using Flow.Services;

namespace Flow.ViewModels;

/// <summary>
/// 메모 창에 열린 메모 하나의 상태. 저장·충돌·다시 읽기 규칙만 맡고 UI 를 모른다(시험 대상).
///
/// 본문은 늘 줄바꿈을 \n 으로 맞춰 든다. 파일이 \r\n 이어도 체크 줄 번호와 비교가 어긋나지 않는다.
/// 쓰는 중이거나 저장 안 한 글이 있으면 바깥 바뀜으로 본문을 갈아끼우지 않는다 —
/// 한글 조합 중에 글이 바뀌면 커서가 튀고 글자가 사라진다. 그런 바뀜은 저장 때 판 검사가 잡는다.
/// </summary>
public sealed class MemoEditorState
{
    public const string ConflictBanner = "다른 곳에서 먼저 고쳐서, 내 글은 충돌 사본으로 따로 남겼습니다";
    public const string MissingBanner = "이 메모는 다른 곳에서 지워졌습니다. 쓰던 글은 새 메모로 남겼습니다";
    public const string DeletedBanner = "이 메모는 다른 곳에서 지워졌습니다";
    public const string FailedBanner = "저장하지 못했습니다 · 다시 시도 중";
    public const string UnreadableBanner = "이 메모를 지금 읽지 못했습니다 · 잠시 뒤 다시 열어 보세요";
    public const string CloseRefusedBanner = "저장하지 못한 글이 있어 닫지 않았습니다 · 저장되면 닫을 수 있습니다";

    private readonly MemoStore _store;

    /// <summary>열었거나 마지막으로 저장한 판. 저장 때 파일의 지금 판과 견준다.</summary>
    private string? _baseRev;

    public MemoEditorState(MemoStore store) => _store = store;

    public Guid? CurrentId { get; private set; }
    public string Body { get; private set; } = "";
    public bool IsDirty { get; private set; }
    public bool IsEditing { get; set; }
    public bool Pinned { get; private set; }

    /// <summary>머리말 daily. 켜 두면 하루가 바뀔 때 체크가 풀린다.</summary>
    public bool IsDaily { get; private set; }

    /// <summary>본문에 체크된 칸이 하나라도 있는지('모두 풀기'를 켤지). 쓰던 글 기준.</summary>
    public bool HasChecked => CurrentId is not null && MemoFormat.UncheckAll(Body) != Body;

    /// <summary>머리말 tags(# 없이). 본문 #태그는 들어 있지 않다.</summary>
    public IReadOnlyList<string> Tags { get; private set; } = Array.Empty<string>();

    /// <summary>충돌·지워짐·저장 실패 안내. null 이면 띠를 숨긴다.</summary>
    public string? Banner { get; private set; }

    /// <summary>
    /// 먼저 지금 메모를 저장하고 그 결과를 돌려준다. 저장하지 못해 글이 남아 있으면 넘어가지 않는다 —
    /// 넘어가면 그 글을 잃는다. 띠가 실패를 알리고, 다시 시도가 붙으면 그때 넘어갈 수 있다.
    /// 그 저장이 충돌·지워짐이었으면 그 띠를 새로 연 메모 위에 남긴다(사본이 생겼다는 것을 놓치지 않게).
    /// 열린 메모가 없을 때(지운 직후) 남아 있던 충돌·지워짐 띠도 그대로 둔다.
    /// </summary>
    public SaveResult? Open(Guid id)
    {
        var carried = CurrentId is null && Banner is (ConflictBanner or MissingBanner) ? Banner : null;
        var pre = Flush();
        if (IsDirty) return pre;

        Banner = pre?.Outcome switch
        {
            SaveOutcome.Conflict => ConflictBanner,
            SaveOutcome.Missing => MissingBanner,
            _ => carried,
        };

        var file = _store.Load(id);
        if (file is null)
        {
            CurrentId = null;
            Body = "";
            Pinned = false;
            IsDaily = false;
            Tags = Array.Empty<string>();
            _baseRev = null;
            // 잠겼거나 덜 받아진 파일은 목록에 남아 있다. 지워졌다고 하면 안 된다.
            Banner = _store.Memos.Any(m => m.Id == id) ? UnreadableBanner : DeletedBanner;
            return pre;
        }

        CurrentId = id;
        Body = Normalize(file.Doc.Body);
        Pinned = file.Doc.Pinned;
        IsDaily = file.Doc.Daily;
        Tags = file.Doc.Tags ?? Array.Empty<string>();
        _baseRev = file.Rev;
        return pre;
    }

    /// <summary>열린 메모를 내려놓는다(삭제 뒤 고를 것이 없을 때). 먼저 저장하고, 못 하면 그대로 둔다.</summary>
    public void Close()
    {
        Flush();
        if (IsDirty) return;

        CurrentId = null;
        Body = "";
        Pinned = false;
        IsDaily = false;
        Tags = Array.Empty<string>();
        _baseRev = null;
        Banner = null;
    }

    public void Edit(string body)
    {
        if (CurrentId is null) return;
        var normalized = Normalize(body);
        if (normalized == Body) return;
        Body = normalized;
        IsDirty = true;
    }

    /// <summary>
    /// 저장 안 한 글이 있을 때만 저장한다. 깨끗한데 저장하면, 다른 PC 가 일부러 지운 메모를
    /// Missing 으로 되살려 버린다. 깨끗하면 null.
    /// </summary>
    public SaveResult? Flush()
    {
        if (!IsDirty || CurrentId is not { } id || _baseRev is null) return null;

        var result = _store.Save(id, Body, _baseRev);
        switch (result.Outcome)
        {
            case SaveOutcome.Saved:
            case SaveOutcome.Unchanged:
                _baseRev = result.Rev;
                IsDirty = false;
                Banner = null;
                break;

            case SaveOutcome.Conflict:
                // 내 글은 사본으로 남았다. 창은 원래 메모의 지금 내용을 보여 준다.
                IsDirty = false;
                Banner = ConflictBanner;
                Reload();
                break;

            case SaveOutcome.Missing:
                // 원래 메모는 사라졌고 내 글이 새 메모가 됐다. 이제 그것을 연 것으로 친다.
                CurrentId = result.CopyId;
                _baseRev = MemoFormat.RevOf(Body);
                Pinned = false;
                IsDaily = false;
                Tags = Array.Empty<string>();
                IsDirty = false;
                Banner = MissingBanner;
                break;

            case SaveOutcome.Failed:
                // 닫기를 막았다는 안내는 저장될 때까지 남긴다(다시 시도가 덮지 않게).
                if (Banner != CloseRefusedBanner) Banner = FailedBanner;
                break;
        }
        return result;
    }

    /// <summary>그 줄의 체크를 뒤집고 바로 저장한다. 체크 줄이 아니면 아무것도 하지 않는다.</summary>
    public void ToggleCheck(int lineIndex)
    {
        if (CurrentId is null) return;
        var line = MemoFormat.Lines(Body).FirstOrDefault(l => l.Index == lineIndex);
        if (line is null || line.Kind != MemoLineKind.Check) return;

        Edit(MemoFormat.SetChecked(Body, lineIndex, !line.Checked));
        Flush();
    }

    /// <summary>
    /// 폴더 훑기(또는 되돌리기) 뒤에 불린다. 쓰는 중이거나 저장 안 한 글이 있으면 아무것도 하지 않는다.
    /// 아니면 새 내용으로 바꾸고, 파일이 사라졌으면 띠로 알린다(글은 그대로 보인다).
    /// </summary>
    public void OnExternalChange()
    {
        if (CurrentId is null || IsEditing || IsDirty) return;
        Reload();
    }

    /// <summary>고정을 바꾼다. 본문 판은 그대로라 저장 안 한 글과 다투지 않는다.</summary>
    public bool SetPinned(bool pinned)
    {
        if (CurrentId is not { } id) return false;
        if (!_store.SetPinned(id, pinned)) return false;
        Pinned = pinned;
        return true;
    }

    /// <summary>머리말 tags 를 바꾼다. 본문 판은 그대로라 저장 안 한 글과 다투지 않는다(저장은 머리말을 지킨다).</summary>
    public bool SetTags(IReadOnlyList<string> tags)
    {
        if (CurrentId is not { } id) return false;
        if (!_store.SetTags(id, tags)) return false;
        Tags = tags.Select(MemoFormat.NormalizeTag).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    /// <summary>
    /// '매일 초기화'를 켜고 끈다. 켤 때 reset 을 today 로 적어 켠 날 바로 풀리지 않는다(MemoStore.SetDaily).
    /// 쓰던 글을 먼저 저장하고, 못 했으면(띠가 남음) 아무것도 하지 않는다. 바뀐 파일을 다시 읽어 판을 맞춘다.
    /// </summary>
    public bool SetDaily(bool daily, DateOnly today)
    {
        if (!FlushFirst(out var id)) return false;
        if (!_store.SetDaily(id, daily, today)) return false;
        Reload();
        return true;
    }

    /// <summary>
    /// 체크를 지금 모두 푼다(날짜와 상관없이, reset 은 그대로). 쓰던 글을 먼저 저장하고,
    /// 못 했으면(띠가 남음) 풀지 않는다. 푼 파일을 다시 읽어 본문·판을 맞춘다 — 그래서 다음 저장이 충돌이 아니다.
    /// </summary>
    public bool UncheckAll()
    {
        if (!FlushFirst(out var id)) return false;
        if (!_store.UncheckAll(id)) return false;
        Reload();
        return true;
    }

    /// <summary>
    /// 머리말·본문을 고치기 전에 쓰던 글을 저장한다. 저장하지 못했거나, 그 저장으로 다른 메모(지워져 새로 남긴 것)를
    /// 열게 됐으면 거짓 — 사용자가 띠를 보고 다시 누르게 한다.
    /// </summary>
    private bool FlushFirst(out Guid id)
    {
        id = default;
        if (CurrentId is not { } current) return false;
        Flush();
        if (IsDirty || CurrentId != current) return false;
        id = current;
        return true;
    }

    /// <summary>
    /// 휴지통으로 보낸다. 쓰던 글을 먼저 저장해 휴지통에 마지막 글이 남게 한다.
    /// 그 저장이 충돌이었으면 원본을 지우되 충돌 띠는 남긴다(내 글은 사본에 있다).
    /// 지워짐(Missing)이었으면 지울 원본이 없으니 지우지 않고 새 메모를 연 채로 둔다.
    /// </summary>
    public bool Delete()
    {
        if (CurrentId is not { } id) return false;
        var pre = Flush();
        if (IsDirty || CurrentId != id) return false;
        if (!_store.Delete(id)) return false;

        CurrentId = null;
        Body = "";
        Pinned = false;
        IsDaily = false;
        Tags = Array.Empty<string>();
        _baseRev = null;
        Banner = pre?.Outcome == SaveOutcome.Conflict ? ConflictBanner : null;
        return true;
    }

    public void ClearBanner() => Banner = null;

    /// <summary>저장 못 한 글이 있어 창 닫기를 거절했다. 저장될 때까지 그 안내를 보인다.</summary>
    public void RefuseClose()
    {
        if (IsDirty) Banner = CloseRefusedBanner;
    }

    private void Reload()
    {
        if (CurrentId is not { } id) return;

        var file = _store.Load(id);
        if (file is null)
        {
            // 잠겼거나 덜 받아진 것은 목록에 남아 있다. 그때는 다음 훑기를 기다린다.
            if (_store.Memos.Any(m => m.Id == id)) return;
            Banner = DeletedBanner;
            return;
        }

        Body = Normalize(file.Doc.Body);
        Pinned = file.Doc.Pinned;
        IsDaily = file.Doc.Daily;
        Tags = file.Doc.Tags ?? Array.Empty<string>();
        _baseRev = file.Rev;
        if (Banner == DeletedBanner) Banner = null;
    }

    private static string Normalize(string body) => body.Replace("\r\n", "\n").Replace('\r', '\n');
}
