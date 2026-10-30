using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Models;
using Flow.Services;

namespace Flow.ViewModels;

/// <summary>
/// 메모 창. 저장 규칙은 MemoEditorState 가 맡고, 여기서는 타이머·목록·화면 전환만 다룬다.
/// 목록·검색·태그는 위젯과 같은 것(MainViewModel)을 쓴다. 훑기도 위젯 하나만 돈다.
/// </summary>
public sealed partial class MemoEditorViewModel : ObservableObject, IDisposable
{
    /// <summary>입력이 이만큼 멈추면 저장한다.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    /// <summary>저장에 실패하면 이 간격으로 다시 해 본다.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>태그 칸 아래 짧은 안내가 떠 있는 시간.</summary>
    private static readonly TimeSpan TagHintDelay = TimeSpan.FromSeconds(4);

    /// <summary>태그를 적는 동안 보여 줄 기존 태그 수.</summary>
    private const int MaxTagSuggestions = 6;

    private const string AppTitle = "Flow 메모";

    private readonly MemoEditorState _state;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _tagHintTimer;

    /// <summary>태그 칩을 마지막으로 그린 내용. 같으면 다시 만들지 않는다.</summary>
    private string? _renderedTags;

    /// <summary>코드가 EditText 를 맞추는 동안에는 그것을 '입력'으로 치지 않는다.</summary>
    private bool _syncing;

    /// <summary>읽기 화면을 마지막으로 그린 본문. 같으면 다시 그리지 않는다.</summary>
    private string? _renderedBody;

    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _hasMemo;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isDaily;
    [ObservableProperty] private bool _hasChecked;

    /// <summary>태그 줄 아래 "연결: 루틴 · …, 할 일 · …". 이 메모를 가리키는 것이 없으면 숨긴다.</summary>
    [ObservableProperty] private string _linkText = "";
    [ObservableProperty] private bool _hasLinks;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private bool _hasBanner;

    /// <summary>쓰는 중이라 이 메모에 못 들어간 휴대폰 체크를 알리는 띠. 충돌·저장 실패 띠 아래에 따로 선다.</summary>
    [ObservableProperty] private string? _phoneWait;
    [ObservableProperty] private bool _hasPhoneWait;

    /// <summary>지난번 Sync 때 쓰는 중으로 들고 있던 메모. 바뀌면 위젯에 알려 폰 화면과 기다리던 체크를 맞춘다.</summary>
    private Guid? _holding;
    [ObservableProperty] private string _windowTitle = AppTitle;

    [ObservableProperty] private string _readTitle = "";
    [ObservableProperty] private string _barTitle = "";
    [ObservableProperty] private bool _hasBarTitle;

    [ObservableProperty] private bool _showTagField;
    [ObservableProperty] private bool _canEditTags;
    [ObservableProperty] private string _tagInput = "";
    [ObservableProperty] private bool _hasTagSuggestions;
    [ObservableProperty] private string? _tagHint;
    [ObservableProperty] private bool _hasTagHint;

    [ObservableProperty] private bool _isTrashView;
    [ObservableProperty] private string _trashLabel = "휴지통";
    [ObservableProperty] private bool _trashEmpty;
    [ObservableProperty] private MemoTrashRow? _selectedTrash;

    [ObservableProperty] private bool _showReader;
    [ObservableProperty] private bool _showWriter;
    [ObservableProperty] private bool _showToolbar;
    [ObservableProperty] private bool _showRestore;
    [ObservableProperty] private bool _showEmptyPane;
    [ObservableProperty] private string _emptyPaneText = "";
    [ObservableProperty] private string _modeText = "고치기";
    [ObservableProperty] private string _modeTip = "고치기 (Ctrl+E)";
    [ObservableProperty] private string _listEmptyText = "";

    /// <summary>오른쪽 위 ? 단추로 여는 사용법 판.</summary>
    [ObservableProperty] private bool _isHelpOpen;

    [RelayCommand]
    private void ToggleHelp() => IsHelpOpen = !IsHelpOpen;

    [RelayCommand]
    private void CloseHelp() => IsHelpOpen = false;

    public MemoEditorViewModel(MainViewModel main)
    {
        Main = main;
        _state = new MemoEditorState(main.Memos);

        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Flush();
        };

        _retryTimer = new DispatcherTimer { Interval = RetryDelay };
        _retryTimer.Tick += (_, _) =>
        {
            if (!_state.IsDirty)
            {
                _retryTimer.Stop();
                return;
            }
            Flush();
        };

        _tagHintTimer = new DispatcherTimer { Interval = TagHintDelay };
        _tagHintTimer.Tick += (_, _) =>
        {
            _tagHintTimer.Stop();
            TagHint = null;
            HasTagHint = false;
        };

        Main.MemoRows.CollectionChanged += OnRowsChanged;
        Main.MemoLinksChanged += RefreshLinks;
        Main.PhoneHeldChanged += RefreshPhoneWait;
        RefreshTrash();
        Sync();
    }

    public MainViewModel Main { get; }

    /// <summary>읽기 모드 줄들. 제목 줄은 빼고 ReadTitle 로 따로 보인다.</summary>
    public ObservableCollection<MemoLineView> ReadLines { get; } = [];

    /// <summary>제목 아래 태그 칸의 칩. 머리말 태그(✕ 로 뺄 수 있음)와 본문에만 있는 #태그.</summary>
    public ObservableCollection<MemoTagChipView> TagChips { get; } = [];

    /// <summary>태그를 적는 동안 보여 주는 기존 태그들.</summary>
    public ObservableCollection<MemoTagSuggestion> TagSuggestions { get; } = [];

    /// <summary>열린 메모의 지금 본문(쓰던 글 포함). 읽기 모드 서식 단추가 쓴다.</summary>
    public string Body => _state.Body;
    public ObservableCollection<MemoTrashRow> TrashRows { get; } = [];

    /// <summary>저장하지 못한 글이 남아 있는지. 창을 닫을 때 본다.</summary>
    public bool HasUnsavedText => _state.IsDirty;

    /// <summary>
    /// 그 메모를 쓰는 중으로 들고 있는지(저장 못 한 글이 있거나 쓰기 모드). 매일 초기화가 이 메모를 건너뛴다 —
    /// 쓰기 모드에서 파일만 풀리면 다음 저장이 충돌 사본이 된다.
    /// </summary>
    public bool HoldsForWriting(Guid id) => _state.CurrentId == id && (_state.IsDirty || _state.IsEditing);

    /// <summary>열린 메모(없으면 null).</summary>
    public Guid? OpenMemoId => _state.CurrentId;

    /// <summary>저장 못 한 글이 있어 닫기를 거절했다. 띠로 알리고 다시 시도는 계속한다.</summary>
    public void RefuseClose()
    {
        // 띠가 사용법 판에 가리지 않게 판을 닫는다.
        IsHelpOpen = false;
        _state.RefuseClose();
        AfterSave(null);
    }

    /// <summary>앱이 끝나는데 저장하지 못한 글. 버리지 않고 이 PC 의 따로 둔 폴더에 남긴다(다음에 켤 때 알린다).</summary>
    public void SaveUnsavedCopy()
    {
        if (!_state.IsDirty) return;
        MemoStore.WriteRecovery(DeviceStore.UnsavedMemoFolder, _state.Body, DateTime.Now);
    }

    // ───────────────────────── 열기·저장

    public void Open(Guid id)
    {
        IsHelpOpen = false;
        if (IsTrashView) IsTrashView = false;

        if (_state.CurrentId == id && _state.Banner != MemoEditorState.DeletedBanner)
        {
            Sync();
            return;
        }

        _saveTimer.Stop();
        TagInput = "";
        // 앞 메모를 저장한 결과(충돌 사본·새 메모)가 목록에 바로 보이도록 AfterSave 로 넘긴다.
        var pre = _state.Open(id);
        if (_state.CurrentId == id || !_state.IsDirty)
        {
            // 다른 메모로 넘어가면 읽기 모드로 시작한다.
            _state.IsEditing = false;
            IsEditing = false;
        }
        AfterSave(pre);
    }

    /// <summary>지금 쓰던 글을 바로 저장한다. 메모를 바꿀 때·창을 닫을 때·되돌리기 직전에 부른다.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        AfterSave(_state.Flush());
    }

    /// <summary>되돌리기 직전. 저장하고 쓰기 모드를 빠져, 되돌린 내용을 다시 읽을 수 있게 한다.</summary>
    public void PrepareForRestore()
    {
        Flush();
        LeaveEditing();
    }

    /// <summary>위젯이 폴더를 다시 훑어 바뀐 것이 있을 때(되돌리기 포함).</summary>
    public void OnMemosChanged()
    {
        _state.OnExternalChange();
        RefreshTrash();
        Sync();
    }

    private void AfterSave(SaveResult? result)
    {
        if (result is { Outcome: SaveOutcome.Saved or SaveOutcome.Conflict or SaveOutcome.Missing })
            Main.RefreshMemoList();

        // 저장 못 한 글이 남았으면 5초마다 다시 해 본다. 메모를 바꾸려다 실패한 경우도 여기로 온다.
        if (!_state.IsDirty) _retryTimer.Stop();
        else if (_state.Banner is MemoEditorState.FailedBanner or MemoEditorState.CloseRefusedBanner
                 && !_retryTimer.IsEnabled) _retryTimer.Start();

        Sync();
    }

    partial void OnEditTextChanged(string value)
    {
        if (_syncing || !IsEditing) return;
        _state.Edit(value);
        // '모두 풀기'가 쓰던 글 기준으로 켜지고 꺼지게.
        HasChecked = _state.HasChecked;
        if (!_state.IsDirty) return;

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ───────────────────────── 명령

    /// <summary>새 메모의 본문. 쓰기 칸이 열리면 통째로 골라 두어 바로 제목을 치면 바뀐다.</summary>
    public const string NewMemoBody = "새 메모";

    /// <summary>쓰기 칸이 열릴 때 글을 통째로 고를지(+ 새 메모). 창이 한 번 쓰고 끈다.</summary>
    public bool SelectAllOnEdit { get; set; }

    /// <summary>+ 새 메모: 지금 글을 저장하고 새 메모를 만들어 쓰기 모드로 연다.</summary>
    [RelayCommand]
    private void NewMemo()
    {
        IsHelpOpen = false;
        Flush();
        if (Main.CreateMemo(NewMemoBody) is not { } id) return;

        if (IsTrashView)
        {
            IsTrashView = false;
            SelectedTrash = null;
            RefreshTrash();
        }
        Open(id);
        SelectAllOnEdit = true;
        EnterEditing();
    }

    [RelayCommand]
    public void ToggleEdit()
    {
        if (IsEditing) LeaveEditing();
        else EnterEditing();
    }

    public void EnterEditing()
    {
        if (IsTrashView || !HasMemo || IsEditing) return;

        _state.IsEditing = true;
        IsEditing = true;
        SetEditText(_state.Body);
        Sync();
    }

    /// <summary>
    /// 쓰기 칸에 지금 들어 있는 글을 받아 둔다. 한글 조합이 끝나며 들어온 마지막 글자가
    /// 바인딩보다 늦게 와도 잃지 않게, 모드를 바꾸거나 닫기 직전에 창이 부른다.
    /// </summary>
    public void TakeWriterText(string? text)
    {
        if (!IsEditing || text is null || Normalize(text) == _state.Body) return;
        _state.Edit(text);
    }

    public void LeaveEditing()
    {
        if (!IsEditing) return;

        Flush();
        _state.IsEditing = false;
        IsEditing = false;
        // 쓰는 동안 미뤄 둔 바깥 바뀜을 이제 받는다.
        _state.OnExternalChange();
        Sync();
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (!HasMemo || IsTrashView) return;
        if (_state.SetPinned(!_state.Pinned)) Main.RefreshMemoList();
        Sync();
    }

    /// <summary>
    /// ↻ 매일 초기화를 켜고 끈다. 쓰던 글을 먼저 저장하고(못 하면 띠가 남고 바꾸지 않음), 켤 때는 오늘을 reset 으로 적는다.
    /// 창이 한글 조합을 끝내고 쓰기 칸의 글을 넘긴 뒤 부른다.
    /// </summary>
    public void ToggleDaily()
    {
        if (!HasMemo || IsTrashView) return;
        _saveTimer.Stop();
        // 하루가 막 바뀌어 위젯이 아직 롤오버를 하지 않았어도 지금의 논리적 날짜를 적는다.
        var today = DayEngine.LogicalDate(DateTime.Now, Main.DayStartHour);
        var done = _state.SetDaily(!_state.IsDaily, today);
        AfterHeaderAction(done, "매일 초기화를 바꾸지 못했습니다");
    }

    /// <summary>'모두 풀기'. 쓰던 글을 먼저 저장하고 체크를 지금 모두 푼다(날짜와 상관없이).</summary>
    public void UncheckAll()
    {
        if (!HasMemo || IsTrashView || !_state.HasChecked) return;
        _saveTimer.Stop();
        var done = _state.UncheckAll();
        // 저장하고 보니 풀 체크가 없었으면(다른 곳에서 이미 풂) 실패가 아니다.
        AfterHeaderAction(done, _state.HasChecked ? "체크를 풀지 못했습니다" : null);
    }

    /// <summary>
    /// ↻·모두 풀기 뒤. 앞선 저장(충돌 사본·새 메모 포함)과 바뀐 표시가 위젯 목록에 보이게 한다.
    /// 저장하지 못해 멈춘 것이면 띠가 이미 알리고, 그 밖의 실패(폴더를 기다리는 중 등)만 짧게 알린다.
    /// </summary>
    private void AfterHeaderAction(bool done, string? failure)
    {
        Main.RefreshMemoList();
        if (!done && failure is not null && !_state.IsDirty && _state.Banner is null) ShowTagHint(failure);
        AfterSave(null);
    }

    /// <summary>휴지통으로 보내고, 목록에서 그 자리의 다음 메모를 연다. 없으면 빈 화면.</summary>
    [RelayCommand]
    private void Delete()
    {
        if (!HasMemo || IsTrashView || _state.CurrentId is not { } id) return;

        var rows = Main.MemoRows;
        var at = rows.Select(r => r.Id).ToList().IndexOf(id);

        _saveTimer.Stop();
        var deleted = _state.Delete();
        // 지우기 앞 저장이 충돌 사본이나 새 메모를 만들었을 수 있다. 스스로 쓴 파일은 훑기가 알려 주지 않으니 직접 맞춘다.
        Main.RefreshMemoList();
        if (!deleted)
        {
            AfterSave(null);
            return;
        }

        _state.IsEditing = false;
        IsEditing = false;
        RefreshTrash();

        var next = PickNext(at);
        if (next is { } nextId) _state.Open(nextId);   // 앞에서 저장했으니 여기서는 저장할 것이 없다
        Sync();
    }

    /// <summary>지운 것이 빠졌으니 같은 자리에 오는 것이 '다음'이다. 없으면 위로 찾는다.</summary>
    private Guid? PickNext(int at)
    {
        var all = Main.MemoRows;
        if (all.Count == 0) return null;
        var start = Math.Clamp(at, 0, all.Count - 1);
        for (var i = start; i < all.Count; i++)
            if (!all[i].IsUnreadable) return all[i].Id;
        for (var i = start - 1; i >= 0; i--)
            if (!all[i].IsUnreadable) return all[i].Id;
        return null;
    }

    [RelayCommand]
    private void ToggleTrash()
    {
        IsHelpOpen = false;
        if (!IsTrashView)
        {
            Flush();
            LeaveEditing();
        }

        IsTrashView = !IsTrashView;
        SelectedTrash = null;
        RefreshTrash();
        Sync();
    }

    public void SelectTrash(MemoTrashRow row)
    {
        SelectedTrash = row;
        foreach (var r in TrashRows) r.IsSelected = r == row;
        Sync();
    }

    [RelayCommand]
    private void Restore()
    {
        if (SelectedTrash is not { } row) return;
        if (!Main.Memos.RestoreFromTrash(row.Id))
        {
            Main.Announce("되살리지 못했습니다");
            return;
        }

        Main.RefreshMemoList();
        IsTrashView = false;
        SelectedTrash = null;
        RefreshTrash();
        Open(row.Id);
    }

    [RelayCommand]
    private void DismissBanner()
    {
        _state.ClearBanner();
        Sync();
    }

    // ───────────────────────── 태그 칸

    /// <summary>태그 입력칸에서 Enter.</summary>
    public void CommitTagInput() => AddTag(TagInput);

    /// <summary>
    /// 머리말 태그를 하나 더한다. 저장 안 한 본문이 있으면 먼저 저장해, 머리말 쓰기와 본문 쓰기가 엇갈리지 않게 한다.
    /// 이름이 잘못됐으면 붙이지 않고 짧게 알린다.
    /// </summary>
    public void AddTag(string raw)
    {
        if (!CanEditTags || string.IsNullOrWhiteSpace(raw)) return;

        var name = MemoFormat.NormalizeTag(raw);
        if (name is null)
        {
            ShowTagHint("태그에는 띄어쓰기·#·쉼표를 넣을 수 없습니다");
            return;
        }

        if (_state.Tags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
        {
            TagInput = "";
            return;
        }

        if (!FlushForTags()) return;
        if (_state.SetTags(_state.Tags.Append(name).ToList())) TagInput = "";
        else ShowTagHint("태그를 저장하지 못했습니다");
        AfterTagsChanged();
    }

    public void RemoveTag(string name)
    {
        if (!CanEditTags) return;
        if (!FlushForTags()) return;

        var rest = _state.Tags.Where(t => !string.Equals(t, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!_state.SetTags(rest)) ShowTagHint("태그를 저장하지 못했습니다");
        AfterTagsChanged();
    }

    /// <summary>쓰던 글을 먼저 저장한다. 못 했으면 태그는 건드리지 않는다.</summary>
    private bool FlushForTags()
    {
        if (!_state.IsDirty) return true;
        Flush();
        if (!_state.IsDirty && _state.CurrentId is not null) return true;
        ShowTagHint("글을 저장하지 못해 태그를 바꾸지 않았습니다");
        return false;
    }

    private void AfterTagsChanged()
    {
        Main.RefreshMemoList();
        Sync();
        UpdateTagSuggestions();
    }

    private void ShowTagHint(string message)
    {
        TagHint = message;
        HasTagHint = true;
        _tagHintTimer.Stop();
        _tagHintTimer.Start();
    }

    partial void OnTagInputChanged(string value) => UpdateTagSuggestions();

    /// <summary>적는 글로 시작하는 기존 태그를 먼저, 그다음 글이 들어 있는 것. 이 메모에 이미 있는 것은 뺀다.</summary>
    private void UpdateTagSuggestions()
    {
        TagSuggestions.Clear();
        var typed = (TagInput ?? "").Trim().TrimStart('#').Trim();
        if (CanEditTags && typed.Length > 0)
        {
            var have = new HashSet<string>(_state.Tags.Concat(MemoFormat.TagsOf(_state.Body)), StringComparer.OrdinalIgnoreCase);
            var all = MemoQuery.AllTags(Main.Memos.Memos).Where(t => !have.Contains(t)).ToList();
            var picks = all.Where(t => t.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase))
                .Concat(all.Where(t => !t.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase)
                                       && t.Contains(typed, StringComparison.CurrentCultureIgnoreCase)))
                .Take(MaxTagSuggestions);
            foreach (var tag in picks) TagSuggestions.Add(new MemoTagSuggestion(tag, this));
        }
        HasTagSuggestions = TagSuggestions.Count > 0;
    }

    /// <summary>제목 아래 칩을 다시 그린다. 쓰는 중에는 저장(1초)마다 본문 #태그를 따라간다.</summary>
    private void RefreshTagChips()
    {
        var chips = new List<(string Name, bool Removable, bool BodyOnly)>();
        if (IsTrashView)
        {
            if (SelectedTrash is { } trash) chips.AddRange(trash.Tags.Select(t => (t, false, false)));
        }
        else if (HasMemo)
        {
            chips.AddRange(_state.Tags.Select(t => (t, true, false)));
            var header = new HashSet<string>(_state.Tags, StringComparer.OrdinalIgnoreCase);
            chips.AddRange(MemoFormat.TagsOf(_state.Body)
                .Where(t => header.Add(t))
                .Select(t => (t, false, true)));
        }

        var key = string.Join("\n", chips.Select(c => (c.Removable ? "h:" : c.BodyOnly ? "b:" : "t:") + c.Name));
        if (key == _renderedTags) return;
        _renderedTags = key;

        TagChips.Clear();
        foreach (var (name, removable, bodyOnly) in chips) TagChips.Add(new MemoTagChipView(name, removable, bodyOnly, this));
    }

    public void ToggleCheck(int lineIndex)
    {
        if (IsTrashView || IsEditing) return;
        _state.ToggleCheck(lineIndex);
        Main.RefreshMemoList();
        AfterSave(null);
    }

    // ───────────────────────── 화면 맞추기

    /// <summary>상태를 화면 값으로 옮긴다. 상태를 바꾼 뒤엔 늘 부른다.</summary>
    private void Sync()
    {
        HasMemo = _state.CurrentId is not null;
        IsPinned = _state.Pinned;
        IsDaily = _state.IsDaily;
        HasChecked = _state.HasChecked;
        Banner = _state.Banner;
        HasBanner = !string.IsNullOrEmpty(Banner);

        // 충돌로 원본을 다시 읽었으면 쓰기 칸도 그것을 보여야 한다. 같은 글이면 건드리지 않는다(커서 보존).
        if (IsEditing && Normalize(EditText) != _state.Body) SetEditText(_state.Body);

        if (IsTrashView)
        {
            if (SelectedTrash is { } trash) Render(trash.ReadBody(), readOnly: true);
            _renderedBody = null;
        }
        else if (!IsEditing && _renderedBody != _state.Body)
        {
            Render(_state.Body, readOnly: false);
            _renderedBody = _state.Body;
        }

        WindowTitle = HasMemo ? DisplayTitle(_state.Body) + " — " + AppTitle : AppTitle;
        BarTitle = IsTrashView
            ? (SelectedTrash is { } shown ? shown.Title + " · 휴지통" : "휴지통")
            : HasMemo ? DisplayTitle(_state.Body) : "";
        HasBarTitle = BarTitle.Length > 0;
        ModeText = IsEditing ? "완료" : "고치기";
        ModeTip = IsEditing ? "읽기로 (Esc · Ctrl+E)" : "고치기 (Ctrl+E)";

        MarkCurrentRows();
        RefreshTagChips();
        RefreshLinks();
        UpdatePane();
        TrackHolding();
        RefreshPhoneWait();
    }

    /// <summary>쓰는 중으로 잡은 메모가 바뀌었으면(쓰기 시작·Esc·저장됨·다른 메모) 위젯에 알린다.</summary>
    private void TrackHolding()
    {
        Guid? holding = _state.CurrentId is { } id && HoldsForWriting(id) ? id : null;
        if (holding == _holding) return;
        _holding = holding;
        Main.MemoHoldChanged();
    }

    /// <summary>열린 메모에 기다리는 휴대폰의 일(체크·고치기·고정 등)이 있으면 한 줄로 알린다. 들어가면 위젯이 알려 와 사라진다.</summary>
    private void RefreshPhoneWait()
    {
        var count = !IsTrashView && _state.CurrentId is { } id ? Main.PhoneEditsWaiting(id) : 0;
        PhoneWait = count > 0 ? $"휴대폰에서 고친 것 {count}개가 기다립니다 · Esc 로 쓰기를 마치면 들어갑니다" : null;
        HasPhoneWait = count > 0;
    }

    /// <summary>이 메모를 가리키는 루틴·할 일을 "연결: …" 한 줄로. 위젯에서 연결을 바꾸면 MemoLinksChanged 로도 온다.</summary>
    private void RefreshLinks()
    {
        var items = !IsTrashView && _state.CurrentId is { } id ? Main.LinkedItems(id) : Array.Empty<string>();
        LinkText = items.Count > 0 ? "연결: " + string.Join(", ", items) : "";
        HasLinks = items.Count > 0;
    }

    private void UpdatePane()
    {
        ShowToolbar = !IsTrashView && HasMemo;
        ShowRestore = IsTrashView && SelectedTrash is not null;
        ShowWriter = !IsTrashView && HasMemo && IsEditing;
        ShowReader = IsTrashView ? SelectedTrash is not null : HasMemo && !IsEditing;
        ShowEmptyPane = !ShowReader && !ShowWriter;
        CanEditTags = !IsTrashView && HasMemo;
        ShowTagField = IsTrashView ? SelectedTrash is not null && TagChips.Count > 0 : HasMemo;
        // 왼쪽 목록이 이미 '비어 있습니다'를 말하면 오른쪽은 같은 말을 되풀이하지 않고 다음에 할 것을 알린다.
        var noMemos = Main.Memos.Memos.Count == 0;
        EmptyPaneText = IsTrashView
            ? (TrashRows.Count == 0 ? "지운 메모는 30일 동안 남아요" : "왼쪽에서 되살릴 메모를 고르세요")
            : noMemos ? "새 메모는 위젯 메모 탭 아래 입력칸에서 만들어요" : "왼쪽에서 메모를 고르세요";
        ListEmptyText = noMemos ? "아직 메모가 없습니다" : "찾는 메모가 없습니다";
    }

    /// <summary>제목 줄에서 줄 머리 기호(# · ## · 1. · - · [ ])를 뗀 글. 목록 줄의 제목과 같다.</summary>
    private static string DisplayTitle(string body) => MemoFormat.PlainTitle(MemoFormat.TitleOf(body));

    /// <summary>읽기 모드 화면을 본문으로 다시 만든다. 첫 비어 있지 않은 줄은 제목으로 따로 보인다.</summary>
    private void Render(string body, bool readOnly)
    {
        var lines = MemoFormat.Lines(body);
        var titleLine = lines.FirstOrDefault(l => l.Kind != MemoLineKind.Blank);

        // 제목 줄·창 제목과 같이 줄 머리 기호를 뗀다.
        ReadTitle = DisplayTitle(body);

        ReadLines.Clear();
        var started = false;
        foreach (var line in lines)
        {
            if (!started)
            {
                // 제목 앞의 빈 줄과 제목 줄은 위에서 따로 보였다.
                if (line == titleLine) started = true;
                continue;
            }
            ReadLines.Add(new MemoLineView(line, this, !readOnly));
        }

        // 제목 바로 아래 빈 줄은 칩 줄과 겹쳐 틈만 벌린다.
        while (ReadLines.Count > 0 && ReadLines[0].Kind == MemoLineKind.Blank) ReadLines.RemoveAt(0);
    }

    private void SetEditText(string text)
    {
        _syncing = true;
        try { EditText = text; }
        finally { _syncing = false; }
    }

    private void RefreshTrash()
    {
        // 목록이 바뀔 때마다 불린다. 휴지통 파일을 다 읽는 것은 휴지통을 볼 때만, 그 밖에는 단추 라벨에 쓸 파일 수만 센다.
        var keep = SelectedTrash?.Id;
        TrashRows.Clear();
        if (IsTrashView)
            foreach (var summary in Main.Memos.Trash()) TrashRows.Add(new MemoTrashRow(summary, this));

        SelectedTrash = keep is { } id ? TrashRows.FirstOrDefault(r => r.Id == id) : null;
        if (SelectedTrash is { } selected) selected.IsSelected = true;

        var count = IsTrashView ? TrashRows.Count : Main.Memos.TrashCount();
        TrashEmpty = count == 0;
        // 휴지통을 보는 동안 같은 단추는 돌아가는 길이다.
        TrashLabel = IsTrashView ? "← 메모로" : "휴지통 " + count;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 위젯이 목록을 다시 만들 때마다 들어온다. 새로 온 줄에만 '지금 연 것' 표시를 단다.
        if (e.NewItems is null) return;
        foreach (var row in e.NewItems.OfType<MemoRow>()) row.IsCurrent = row.Id == _state.CurrentId;
    }

    private void MarkCurrentRows()
    {
        var current = IsTrashView ? null : _state.CurrentId;
        foreach (var row in Main.MemoRows) row.IsCurrent = row.Id == current;
    }

    private static string Normalize(string? text) => MemoFormat.Newlines(text ?? "");

    public void Dispose()
    {
        _saveTimer.Stop();
        _retryTimer.Stop();
        _tagHintTimer.Stop();
        Main.MemoRows.CollectionChanged -= OnRowsChanged;
        Main.MemoLinksChanged -= RefreshLinks;
        Main.PhoneHeldChanged -= RefreshPhoneWait;
        foreach (var row in Main.MemoRows) row.IsCurrent = false;
        // 창이 닫히며 쓰던 메모를 놓았다. 기다리던 폰 체크를 바로 넣게 한다.
        if (_holding is not null) Main.MemoHoldChanged();
    }
}

/// <summary>읽기 모드의 한 줄. 종류에 따라 다른 모양으로 그린다(MemoLineTemplates).</summary>
public sealed partial class MemoLineView : ObservableObject
{
    private readonly MemoEditorViewModel _owner;

    public MemoLineView(MemoLine line, MemoEditorViewModel owner, bool canToggle)
    {
        _owner = owner;
        Index = line.Index;
        Kind = line.Kind;
        Text = line.Text;
        Marker = line.Marker ?? "";
        Checked = line.Checked;
        CanToggle = canToggle;
        IndentWidth = line.Indent * IndentStep;
    }

    /// <summary>들여쓰기 한 단계의 너비. 기호 칸(24)과 같아 아래 단계의 기호가 위 줄 글자 밑에 선다.</summary>
    public const double IndentStep = 24;

    public int Index { get; }
    public MemoLineKind Kind { get; }
    public string Text { get; }
    public string Marker { get; }
    public bool Checked { get; }

    /// <summary>체크·번호·점 줄을 들여쓴 만큼 기호와 글을 함께 미는 너비.</summary>
    public double IndentWidth { get; }

    /// <summary>휴지통에서 볼 때는 체크를 누를 수 없다.</summary>
    public bool CanToggle { get; }

    [RelayCommand]
    private void Toggle()
    {
        if (CanToggle) _owner.ToggleCheck(Index);
    }
}

/// <summary>
/// 태그 칸의 칩 하나. 머리말 태그는 ✕ 로 뺄 수 있다.
/// 본문에만 적힌 #태그는 본문을 고쳐야 빠지므로 ✕ 없이 흐리게 보인다.
/// </summary>
public sealed partial class MemoTagChipView(string name, bool removable, bool bodyOnly, MemoEditorViewModel owner)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Text { get; } = "#" + name;
    public bool Removable { get; } = removable;
    public bool IsBodyOnly { get; } = bodyOnly;
    public string? Tip { get; } = bodyOnly ? "본문에 적힌 태그" : null;

    [RelayCommand]
    private void Remove() => owner.RemoveTag(Name);
}

/// <summary>태그를 적는 동안 보이는 기존 태그. 누르면 이 메모에 붙는다.</summary>
public sealed partial class MemoTagSuggestion(string name, MemoEditorViewModel owner) : ObservableObject
{
    public string Text { get; } = "#" + name;

    [RelayCommand]
    private void Add() => owner.AddTag(name);
}

/// <summary>휴지통 목록의 한 줄. 누르면 오른쪽에 읽기 전용으로 보인다.</summary>
public sealed partial class MemoTrashRow : ObservableObject
{
    private readonly MemoEditorViewModel _owner;
    private readonly string _path;

    [ObservableProperty] private bool _isSelected;

    public MemoTrashRow(MemoSummary summary, MemoEditorViewModel owner)
    {
        _owner = owner;
        _path = summary.Path;
        Id = summary.Id;
        Title = MemoFormat.PlainTitle(summary.Title);
        Preview = summary.Unreadable ? "읽지 못함" : summary.Preview;
        Tags = summary.Tags;
    }

    public Guid Id { get; }
    public string Title { get; }

    /// <summary>머리말 태그와 본문 #태그를 합친 것(보기만).</summary>
    public IReadOnlyList<string> Tags { get; }
    public string Preview { get; }

    /// <summary>휴지통 파일의 본문. 읽지 못하면 빈 글.</summary>
    public string ReadBody()
    {
        try
        {
            return MemoFormat.Newlines(MemoFormat.Parse(File.ReadAllText(_path), Id).Body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    [RelayCommand]
    private void Select() => _owner.SelectTrash(this);
}
