using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Services;

namespace Flow.ViewModels;

/// <summary>위젯 메모 탭의 한 줄. 요약만 들고 있고 본문은 들지 않는다.</summary>
public sealed partial class MemoRow : ObservableObject
{
    private readonly MainViewModel _owner;

    /// <summary>컴팩트 모드에서는 줄 높이를 줄인다. 다른 탭의 줄과 같은 규칙.</summary>
    [ObservableProperty] private bool _isCompact;

    public MemoRow(MemoSummary summary, MainViewModel owner)
    {
        _owner = owner;
        IsCompact = owner.IsCompact;
        Id = summary.Id;
        Title = summary.Title;
        // 읽지 못한 파일의 요약은 이전 값일 수 있어 그대로 믿지 않고 상태만 알린다.
        Preview = summary.Unreadable ? "읽지 못함" : summary.Preview;
        IsPinned = summary.Pinned;
        IsDaily = summary.Daily;
        IsUnreadable = summary.Unreadable;
    }

    public Guid Id { get; }
    public string Title { get; }
    public string Preview { get; }
    public bool IsPinned { get; }
    public bool IsDaily { get; }

    /// <summary>잠겼거나 덜 받아진 파일. 흐리게 보이고 열리지 않는다.</summary>
    public bool IsUnreadable { get; }

    [RelayCommand]
    private void Open() => _owner.OpenMemo(Id);
}

/// <summary>태그 칩 하나. 눌린 태그는 다시 누르면 풀린다.</summary>
public sealed partial class MemoTagChip : ObservableObject
{
    private readonly MainViewModel _owner;

    public MemoTagChip(string tag, bool active, MainViewModel owner)
    {
        Tag = tag;
        IsActive = active;
        _owner = owner;
    }

    public string Tag { get; }
    public string Text => "#" + Tag;
    public bool IsActive { get; }

    [RelayCommand]
    private void Toggle() => _owner.ToggleMemoTag(Tag);
}
