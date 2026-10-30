using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Flow.Models;

namespace Flow.Services;

// 메모 폴더를 다루는 저장소. UI 패키지를 참조하지 않는다.

/// <summary>
/// 목록에 보이는 메모 하나. Unreadable 이면 잠겼거나 덜 받아진 파일이라 이전 값(없으면 파일 이름)을 보인다.
/// Reset 은 머리말의 마지막 매일 초기화 날(폰 화면이 쓴다).
/// </summary>
public sealed record MemoSummary(Guid Id, string Path, string Title, string Preview,
    IReadOnlyList<string> Tags, bool Pinned, bool Daily, DateTime Updated, string Rev, bool Unreadable,
    DateOnly? Reset = null);

public sealed record MemoFile(MemoDocument Doc, string Path, string Rev);

public enum SaveOutcome { Saved, Unchanged, Conflict, Missing, Failed }

/// <summary>
/// 휴대폰에서 온 메모 체크 하나의 결과. Applied = 고쳐 씀, Skipped = 할 것이 없음(맞는 줄 없음·이미 그 상태·메모 없음),
/// Deferred = 지금은 못 함(쓸 수 없음·읽지 못함) — 처리했다고 적지 않아 다음에 다시 해 본다.
/// Held = PC 메모 창이 그 메모를 쓰는 중으로 들고 있어 못 함. Deferred 와 같이 남기되, 쓰기를 마치면 들어간다고 알릴 수 있게 따로 둔다.
/// </summary>
public enum MemoCheckResult { Applied, Skipped, Deferred, Held }

public sealed record SaveResult(SaveOutcome Outcome, string? Rev, Guid? CopyId);

/// <summary>
/// 매일 초기화 한 번의 결과. Written 은 쓴 파일 수(풀 체크가 없던 것도 날짜를 적으므로 센다).
/// Pending 은 이번에 건너뛰었거나(열린 메모·읽지 못함·쓰기 실패) 아직 모르는 메모 — 같은 날 다시 해 본다.
/// Blocked 는 폴더를 기다리느라 아무것도 보지 못한 것.
/// </summary>
public sealed record DailyResetResult(int Written, IReadOnlyList<Guid> Pending, bool IsBlocked)
{
    public static readonly DailyResetResult Blocked = new(0, Array.Empty<Guid>(), true);

    /// <summary>오늘 몫을 다 끝냈는지. 거짓이면 같은 날 다시 해 본다.</summary>
    public bool Complete => !IsBlocked && Pending.Count == 0;
}

/// <summary>
/// 데이터 폴더의 memos 폴더. 훑기·저장(충돌 사본)·이름 바꾸기·휴지통을 맡는다.
/// 쓰기는 늘 이름.tmp 에 쓴 뒤 바꿔치기한다. canWrite 가 거짓이면 아무것도 쓰지 않는다.
/// </summary>
public sealed class MemoStore
{
    private const int TrashDays = 30;
    private static readonly Regex NumberSuffix = new(@" \(\d+\)$", RegexOptions.Compiled);
    private static readonly Regex TagInTitle = new(@"(?<=^|\s)#[^\s#]+", RegexOptions.Compiled);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private sealed class Entry
    {
        public string Path = "";
        public long Size;
        public long Ticks;
        public MemoSummary Summary = null!;
        public bool Unreadable;
        public bool Hidden;
    }

    private readonly Func<bool> _canWrite;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _scanned;

    /// <summary>매일 초기화를 끝낸(풀었거나 풀 것이 없던) 메모와 그 날. 같은 날 다시 해 볼 때 남은 것만 보려고.</summary>
    private DateOnly? _resetDay;
    private readonly HashSet<Guid> _resetDone = new();

    /// <summary>안쪽 훑기(찾다가 다시 훑기)가 찾은 바뀜. 다음 Scan() 이 대신 알린다.</summary>
    private bool _changedSinceReport;

    public MemoStore(string dataDirectory, Func<bool> canWrite, Func<DateTime> now)
    {
        DataFolder = dataDirectory;
        Folder = System.IO.Path.Combine(dataDirectory, "memos");
        TrashFolder = System.IO.Path.Combine(Folder, "휴지통");
        _canWrite = canWrite;
        _now = now;
    }

    /// <summary>memos 폴더를 담은 데이터 폴더. 이것째 안 보이면 클라우드 드라이브가 잠깐 빠진 것이지 메모를 지운 것이 아니다.</summary>
    public string DataFolder { get; }
    public string Folder { get; }
    public string TrashFolder { get; }

    public IReadOnlyList<MemoSummary> Memos
        => _entries.Values.Where(e => !e.Hidden).Select(e => e.Summary)
            .OrderByDescending(s => s.Updated).ToList();

    // ───────────────────────── 훑기

    /// <summary>폴더를 훑는다. 지난번 Scan() 뒤로 바뀐 것이 있으면(안쪽 훑기가 찾은 것 포함) true.</summary>
    public bool Scan()
    {
        var found = ScanFolder();
        var report = found || _changedSinceReport;
        _changedSinceReport = false;
        return report;
    }

    private bool ScanFolder()
    {
        _scanned = true;
        var changed = false;

        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(Folder))
            {
                foreach (var p in Directory.EnumerateFiles(Folder))
                    if (IsMemoFile(p)) files[p] = new FileInfo(p);
            }
            else if (!Directory.Exists(DataFolder))
            {
                return false;   // 드라이브가 잠깐 빠졌다 — 목록을 비우면 모든 메모가 '다른 곳에서 지워진' 것이 된다
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var gone in _entries.Keys.Where(k => !files.ContainsKey(k)).ToList())
        {
            _entries.Remove(gone);
            changed = true;
        }

        foreach (var (path, fi) in files)
        {
            long size, ticks;
            try { fi.Refresh(); size = fi.Length; ticks = fi.LastWriteTimeUtc.Ticks; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            _entries.TryGetValue(path, out var known);
            if (known is { Unreadable: false } && known.Size == size && known.Ticks == ticks) continue;

            try
            {
                var doc = MemoFormat.Parse(File.ReadAllText(path), MemoFormat.IdFromPath(Relative(path)));
                _entries[path] = new Entry
                {
                    Path = path, Size = size, Ticks = ticks,
                    Summary = Summarize(doc, path, fi.LastWriteTime),
                };
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 표시를 기억하지 않아 다음 훑기에 다시 읽는다. 이전 값이 있으면 그대로 보인다.
                if (known is { Unreadable: true }) continue;
                var summary = known is not null
                    ? known.Summary with { Unreadable = true }
                    : new MemoSummary(MemoFormat.IdFromPath(Relative(path)), path,
                        System.IO.Path.GetFileNameWithoutExtension(path), "", Array.Empty<string>(),
                        false, false, fi.LastWriteTime, "", true);
                _entries[path] = new Entry { Path = path, Unreadable = true, Summary = summary };
                changed = true;
            }
        }

        if (ResolveDuplicateIds()) changed = true;
        return changed;
    }

    /// <summary>같은 id 가 둘이면 고친 시각이 늦은 쪽이 원본. 다른 쪽은 새 id 의 충돌 사본으로 그 자리에서 다시 쓴다.</summary>
    private bool ResolveDuplicateIds()
    {
        foreach (var e in _entries.Values) e.Hidden = false;
        var changed = false;

        var groups = _entries.Values.Where(e => !e.Unreadable)
            .GroupBy(e => e.Summary.Id).Where(g => g.Count() > 1).ToList();
        foreach (var group in groups)
        {
            var ordered = group.OrderByDescending(e => e.Ticks)
                .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var copy in ordered.Skip(1))
            {
                if (!_canWrite()) { copy.Hidden = true; continue; }
                try
                {
                    var doc = ReadDoc(copy.Path);
                    var renewed = doc with
                    {
                        Id = Guid.NewGuid(), HasHeader = true,
                        Body = MemoFormat.AppendToTitle(doc.Body, " (충돌 사본)"),
                    };
                    WriteDoc(copy.Path, renewed);
                    Remember(copy.Path, renewed);
                    changed = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    copy.Hidden = true;
                }
            }
        }
        return changed;
    }

    // ───────────────────────── 읽기·만들기·저장

    public MemoFile? Load(Guid id)
    {
        try
        {
            if (Locate(id) is not (var e, var doc)) return null;
            return new MemoFile(doc, e.Path, MemoFormat.RevOf(doc.Body));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public MemoFile? Create(string body) => CreateAs(Guid.NewGuid(), body);

    /// <summary>
    /// 휴대폰에서 만든 새 메모를 그 id 로 만든다 — 같은 일이 두 번 와도 하나만 생기게.
    /// 그 id 의 메모가 이미 있으면(휴지통 포함) Skipped, 쓸 수 없거나 쓰지 못하면 Deferred, 만들면 Applied.
    /// </summary>
    public MemoCheckResult CreateWithId(Guid id, string body)
    {
        if (!_canWrite()) return MemoCheckResult.Deferred;
        try
        {
            if (Has(id)) return MemoCheckResult.Skipped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MemoCheckResult.Deferred;
        }
        return CreateAs(id, body) is null ? MemoCheckResult.Deferred : MemoCheckResult.Applied;
    }

    /// <summary>
    /// 그 id 의 메모가 메모 폴더나 휴지통에 있는지. 메모 폴더를 읽지 못하면 IOException 그대로.
    /// 휴지통의 읽지 못하는 파일은 훑기처럼 건너뛴다 — 그 하나 때문에 휴대폰 저장이 영영 미뤄지지 않게.
    /// </summary>
    private bool Has(Guid id)
    {
        if (Locate(id) is not null) return true;
        foreach (var path in TrashFiles())
        {
            try
            {
                if (ReadDoc(path).Id == id) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 다음 파일
            }
        }
        return false;
    }

    private MemoFile? CreateAs(Guid id, string body)
    {
        if (!_canWrite()) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            var doc = new MemoDocument(id, true, false, false, null, null,
                Array.Empty<KeyValuePair<string, string>>(), body);
            var path = UniquePath(Folder, StemOf(body), null);
            WriteDoc(path, doc);
            Remember(path, doc);
            return new MemoFile(doc, path, MemoFormat.RevOf(body));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 판이 baseRev 그대로면 덮어쓴다. 그 사이 바뀌었으면 원본은 두고 글을 충돌 사본(새 메모)으로, 지워졌으면 새 메모로 남긴다.
    /// conflictSuffix 는 충돌 사본 제목 끝에 붙일 말(없으면 " (충돌 사본 MM-dd HH:mm)"),
    /// newId 는 충돌 사본·새 메모의 id(없으면 새로 뽑는다). 휴대폰은 그 일의 번호를 준다 — 그 id 의 메모가 이미 있으면(휴지통 포함)
    /// 같은 일이 두 번 온 것이라 아무것도 만들지 않고 Unchanged.
    /// day(휴대폰이 저장한 논리적 날짜)가 있고 그 메모가 매일 초기화인데 그 날 몫 초기화가 아직이면 Failed(나중에 다시) —
    /// 먼저 덮으면 곧 이어질 초기화가 방금 넣은 글의 체크를 풀어 버린다(SetLineChecked 와 같다). PC 메모 창은 day 를 주지 않는다.
    /// </summary>
    public SaveResult Save(Guid id, string body, string baseRev, string? conflictSuffix = null, Guid? newId = null, DateOnly? day = null)
    {
        if (!_canWrite()) return new SaveResult(SaveOutcome.Failed, null, null);
        try
        {
            // 그 자리에 다른 메모가 와 있으면 덮지 않는다. 끝내 못 찾으면 지워진 것으로 본다.
            var located = Locate(id);
            if (located is (_, var due) && due.Daily && day is { } d && (due.Reset is not { } reset || reset < d))
                return new SaveResult(SaveOutcome.Failed, null, null);

            // 덮지 못하고 새 메모로 남길 차례인데 newId 의 메모가 벌써 있다 — 같은 일이 두 번 왔다.
            var overwrites = located is { } l && MemoFormat.RevOf(l.Doc.Body) == baseRev;
            if (!overwrites && newId is { } fresh && Has(fresh)) return new SaveResult(SaveOutcome.Unchanged, null, null);

            if (located is not (var e, var current))
            {
                var made = CreateAs(newId ?? Guid.NewGuid(), body);
                return made is null
                    ? new SaveResult(SaveOutcome.Failed, null, null)
                    : new SaveResult(SaveOutcome.Missing, null, made.Doc.Id);
            }

            var currentRev = MemoFormat.RevOf(current.Body);
            if (currentRev != baseRev)
            {
                var copyBody = MemoFormat.AppendToTitle(body, conflictSuffix
                    ?? " (충돌 사본 " + _now().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
                var copy = CreateAs(newId ?? Guid.NewGuid(), copyBody);
                return copy is null
                    ? new SaveResult(SaveOutcome.Failed, null, null)
                    : new SaveResult(SaveOutcome.Conflict, null, copy.Doc.Id);
            }

            var newRev = MemoFormat.RevOf(body);
            if (newRev == currentRev) return new SaveResult(SaveOutcome.Unchanged, newRev, null);

            var doc = current with { Id = id, HasHeader = true, Body = body };
            var path = e.Path;

            // 제목이 바뀌어 이름이 달라지면 먼저 이름을 바꾼다(실패해도 같은 id 가 둘 생기지 않게).
            var stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var want = StemOf(body);
            if (!SameName(stem, want) && !SameName(NumberSuffix.Replace(stem, ""), want))
            {
                var target = UniquePath(Folder, want, path);
                if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(path, target);
                    _entries.Remove(path);
                    path = target;
                }
            }

            WriteDoc(path, doc);
            Remember(path, doc);
            return new SaveResult(SaveOutcome.Saved, newRev, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveResult(SaveOutcome.Failed, null, null);
        }
    }

    public bool SetPinned(Guid id, bool pinned)
    {
        if (!_canWrite()) return false;
        try
        {
            if (Locate(id) is not (var e, var doc)) return false;
            if (doc.HasHeader && doc.Pinned == pinned) return true;
            doc = doc with { Id = id, HasHeader = true, Pinned = pinned };
            WriteDoc(e.Path, doc);
            Remember(e.Path, doc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 휴대폰에서 온 고치기 하나(고정·매일 초기화·체크 모두 풀기). change 가 고친 문서를 돌려주면 쓰고 Applied,
    /// null 이면(이미 그 상태) Skipped. 메모가 없으면(휴지통 포함) Skipped, 쓸 수 없거나 읽지·쓰지 못하면 Deferred.
    /// </summary>
    public MemoCheckResult Update(Guid id, Func<MemoDocument, MemoDocument?> change)
    {
        if (!_canWrite()) return MemoCheckResult.Deferred;
        try
        {
            if (Locate(id) is not (var e, var doc)) return MemoCheckResult.Skipped;
            if (change(doc) is not { } changed) return MemoCheckResult.Skipped;
            changed = changed with { Id = id, HasHeader = true };
            WriteDoc(e.Path, changed);
            Remember(e.Path, changed);
            return MemoCheckResult.Applied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MemoCheckResult.Deferred;
        }
    }

    /// <summary>머리말 tags 만 고친다. 본문·판은 그대로. 잘못된 이름은 버리고 중복은 하나로 한다.</summary>
    public bool SetTags(Guid id, IReadOnlyList<string> tags)
    {
        if (!_canWrite()) return false;
        try
        {
            if (Locate(id) is not (var e, var doc)) return false;
            var clean = tags.Select(MemoFormat.NormalizeTag).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (doc.HasHeader && (doc.Tags ?? Array.Empty<string>()).SequenceEqual(clean)) return true;
            doc = doc with { Id = id, HasHeader = true, Tags = clean };
            WriteDoc(e.Path, doc);
            Remember(e.Path, doc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>머리말 daily 만 고친다(판 그대로). 켤 때 reset 을 today 로 적어 켠 날 바로 풀리지 않게 한다. 끌 때 reset 은 둔다.</summary>
    public bool SetDaily(Guid id, bool daily, DateOnly today)
    {
        if (!_canWrite()) return false;
        try
        {
            if (Locate(id) is not (var e, var doc)) return false;
            if (doc.HasHeader && doc.Daily == daily && (!daily || doc.Reset == today)) return true;
            doc = doc with { Id = id, HasHeader = true, Daily = daily, Reset = daily ? today : doc.Reset };
            WriteDoc(e.Path, doc);
            Remember(e.Path, doc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// daily 이고 reset 이 없거나 today 보다 이전인 메모의 체크를 풀고 reset 을 today 로 적는다(풀 것이 없어도 날짜는 적는다).
    /// skip(id) 가 참이거나 읽지·쓰지 못한 메모는 이번에 건너뛰고 Pending 으로 알린다. 휴지통은 보지 않는다.
    /// 같은 날 다시 부르면 이미 끝낸 메모는 읽지도 않고 남은 것만 해 본다.
    /// 체크를 풀 것이 없어 머리말 reset 만 바뀐 파일은 고친 시각을 옛 시각 + 2초로 되돌린다 — 매일 아침 '최근'의 맨 위로 뛰지 않게.
    /// </summary>
    public DailyResetResult ResetDaily(DateOnly today, Func<Guid, bool>? skip = null)
    {
        if (!_canWrite()) return DailyResetResult.Blocked;
        // 데이터 폴더째 안 보이면 아무것도 볼 수 없다. 풀 것이 없다고 끝내면 그날 다시 안 푼다.
        if (!Directory.Exists(DataFolder)) return DailyResetResult.Blocked;
        try
        {
            if (!_scanned && ScanFolder()) _changedSinceReport = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DailyResetResult.Blocked;
        }

        if (_resetDay != today)
        {
            _resetDay = today;
            _resetDone.Clear();
        }

        var count = 0;
        var pending = new List<Guid>();
        var tried = new HashSet<Guid>();
        // 찾다가 다시 훑으면 새로 보이는 메모가 생길 수 있어, 새 것이 없을 때까지 돈다.
        for (;;)
        {
            var batch = _entries.Values
                .Where(en => !en.Hidden && (en.Summary.Daily || en.Unreadable))
                .Select(en => en.Summary.Id)
                .Where(id => !_resetDone.Contains(id) && !tried.Contains(id))
                .Distinct().ToList();
            if (batch.Count == 0) break;

            foreach (var id in batch)
            {
                tried.Add(id);
                try
                {
                    if (Locate(id) is not (var e, var doc)) continue;   // 사라졌으면 할 것이 없다
                    if (!doc.Daily || (doc.Reset is { } last && last >= today))
                    {
                        _resetDone.Add(id);
                        continue;
                    }
                    if (skip?.Invoke(id) == true)
                    {
                        pending.Add(id);
                        continue;
                    }

                    var body = MemoFormat.UncheckAll(doc.Body);
                    DateTime? keepTime = body == doc.Body ? File.GetLastWriteTimeUtc(e.Path) : null;
                    doc = doc with { Id = id, HasHeader = true, Reset = today, Body = body };
                    WriteDoc(e.Path, doc);
                    if (keepTime is { } time)
                    {
                        // 딱 그 시각이 아니라 2초 뒤로: 같은 길이로 reset 만 바뀐 파일을 다른 PC 의 훑기가
                        // 크기 · 시각으로 알아채야 한다(2초는 시각을 2초 단위로 적는 저장소도 구별하는 폭).
                        try { File.SetLastWriteTimeUtc(e.Path, time.AddSeconds(2)); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 시각만 못 되돌림 */ }
                    }
                    Remember(e.Path, doc);
                    _resetDone.Add(id);
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    pending.Add(id);   // 다음 기회에
                }
            }
        }
        return new DailyResetResult(count, pending, false);
    }

    /// <summary>
    /// 오늘 끝낸 매일 초기화를 잊는다. 메모 파일을 통째로 되돌린 뒤(백업 되살리기) 부른다 —
    /// 되돌린 파일에는 어제 체크가 들었을 수 있는데, 끝낸 것으로 기억하면 그날 다시 풀지 않는다.
    /// </summary>
    public void ForgetDailyProgress()
    {
        _resetDay = null;
        _resetDone.Clear();
    }

    /// <summary>그 메모가 daily 이고 오늘 아직 풀지 않았는지(롤오버에서 열린 메모를 먼저 저장할지). 읽지 못하면 false.</summary>
    public bool IsDailyDue(Guid id, DateOnly today)
    {
        try
        {
            return Locate(id) is (_, var doc) && doc.Daily && (doc.Reset is not { } last || last < today);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>본문 체크를 지금 모두 풀고 저장한다(판이 바뀐다). 풀 것이 없거나 메모가 없거나 쓸 수 없으면 false.</summary>
    public bool UncheckAll(Guid id)
    {
        if (!_canWrite()) return false;
        try
        {
            if (Locate(id) is not (var e, var doc)) return false;
            var body = MemoFormat.UncheckAll(doc.Body);
            if (body == doc.Body) return false;
            doc = doc with { Id = id, HasHeader = true, Body = body };
            WriteDoc(e.Path, doc);
            Remember(e.Path, doc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 휴대폰의 메모 체크 하나를 맞춘다. line 줄이 체크 줄이고 글이 text 와 같으면 그 줄을, 아니면(줄이 밀렸다)
    /// 같은 글의 체크 줄 중 첫째를 isChecked 로 맞춘다. 글은 앞뒤 공백을 떼고 견준다.
    /// 맞는 줄이 없거나 이미 그 상태거나 메모가 없으면 Skipped, 쓸 수 없거나 읽지·쓰지 못하면 Deferred.
    /// day(폰에서 누른 논리적 날짜)가 있고 매일 초기화 메모라면: 그 날보다 뒤에 이미 풀었으면 지난 날 체크라 Skipped,
    /// 그 날 몫 초기화가 아직이면 Deferred — 먼저 넣으면 곧 이어질 초기화가 방금 넣은 체크를 풀어 버린다.
    /// </summary>
    public MemoCheckResult SetLineChecked(Guid id, string? text, int? line, bool isChecked, DateOnly? day = null)
    {
        if (!_canWrite()) return MemoCheckResult.Deferred;
        try
        {
            if (Locate(id) is not (var e, var doc)) return MemoCheckResult.Skipped;

            if (doc.Daily && day is { } d)
            {
                if (doc.Reset is { } reset && d < reset) return MemoCheckResult.Skipped;
                if (doc.Reset is not { } done || done < d) return MemoCheckResult.Deferred;
            }

            var want = (text ?? "").Trim();
            var lines = MemoFormat.Lines(doc.Body);
            bool Matches(MemoLine l) => l.Kind == MemoLineKind.Check && l.Text.Trim() == want;

            var target = line is { } n && n >= 0 && n < lines.Count && Matches(lines[n]) ? lines[n] : null;
            target ??= lines.FirstOrDefault(Matches);
            if (target is null || target.Checked == isChecked) return MemoCheckResult.Skipped;

            var body = MemoFormat.SetChecked(doc.Body, target.Index, isChecked);
            if (body == doc.Body) return MemoCheckResult.Skipped;

            doc = doc with { Id = id, HasHeader = true, Body = body };
            WriteDoc(e.Path, doc);
            Remember(e.Path, doc);
            return MemoCheckResult.Applied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MemoCheckResult.Deferred;
        }
    }

    // ───────────────────────── 휴지통

    public bool Delete(Guid id) => MoveToTrash(id) == MemoCheckResult.Applied;

    /// <summary>휴지통으로 옮긴다. 없으면(이미 휴지통 포함) Skipped, 쓸 수 없거나 옮기지 못하면 Deferred.</summary>
    public MemoCheckResult MoveToTrash(Guid id)
    {
        if (!_canWrite()) return MemoCheckResult.Deferred;
        try
        {
            if (Locate(id) is not (var e, var found)) return MemoCheckResult.Skipped;
            var doc = found with
            {
                Id = id, HasHeader = true, Deleted = DateOnly.FromDateTime(_now()),
            };
            Directory.CreateDirectory(TrashFolder);
            var target = UniquePath(TrashFolder, System.IO.Path.GetFileNameWithoutExtension(e.Path), null);
            WriteDoc(target, doc);
            try { File.Delete(e.Path); }
            catch
            {
                File.Delete(target);   // 둘이 남지 않게 되돌린다
                throw;
            }
            _entries.Remove(e.Path);
            return MemoCheckResult.Applied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MemoCheckResult.Deferred;
        }
    }

    public IReadOnlyList<MemoSummary> Trash()
    {
        var result = new List<MemoSummary>();
        foreach (var path in TrashFiles())
        {
            try
            {
                var doc = ReadDoc(path);
                result.Add(Summarize(doc, path, File.GetLastWriteTime(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new MemoSummary(MemoFormat.IdFromPath(Relative(path)), path,
                    System.IO.Path.GetFileNameWithoutExtension(path), "", Array.Empty<string>(),
                    false, false, DateTime.MinValue, "", true));
            }
        }
        return result.OrderByDescending(s => s.Updated).ToList();
    }

    /// <summary>
    /// 휴지통에서 메모 폴더로 되살린다. 다른 휴지통 파일이 안 읽혀도 나머지는 본다.
    /// 되살린 뒤 휴지통 파일을 못 지워도 성공이다 — 실패로 알리면 다시 눌러 같은 메모가 둘이 된다.
    /// 그렇게 남은 휴지통 파일은 다시 되살릴 때 메모가 이미 있으니 지우기만 한다.
    /// </summary>
    public bool RestoreFromTrash(Guid id)
    {
        if (!_canWrite()) return false;
        foreach (var path in TrashFiles())
        {
            MemoDocument doc;
            try { doc = ReadDoc(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (doc.Id != id) continue;

            try
            {
                if (Locate(id) is null)
                {
                    doc = doc with { HasHeader = true, Deleted = null };
                    Directory.CreateDirectory(Folder);
                    var target = UniquePath(Folder, StemOf(doc.Body), null);
                    WriteDoc(target, doc);
                    Remember(target, doc);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 이미 되살아났다 */ }
            return true;
        }
        return false;
    }

    /// <summary>deleted 가 today-30일 보다 이전인 것만 지운다. 머리말에 deleted 가 없으면 파일 고친 날을 쓴다.</summary>
    public int PurgeTrash(DateOnly today)
    {
        if (!_canWrite()) return 0;
        var limit = today.AddDays(-TrashDays);
        var count = 0;
        foreach (var path in TrashFiles())
        {
            try
            {
                var doc = ReadDoc(path);
                var deleted = doc.Deleted ?? DateOnly.FromDateTime(File.GetLastWriteTime(path));
                if (deleted >= limit) continue;
                File.Delete(path);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 다음 기회에
            }
        }
        return count;
    }

    // ───────────────────────── 저장하지 못한 글

    /// <summary>
    /// 앱이 끝나는데 저장하지 못한 글을 folder 에 '제목 yyyyMMdd-HHmmss.md' 로 남긴다.
    /// 머리말+본문 그대로라 memos 폴더로 옮기면 다시 보인다. 새 id 라 원래 메모와 겹치지 않는다.
    /// </summary>
    public static string? WriteRecovery(string folder, string body, DateTime now)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var doc = new MemoDocument(Guid.NewGuid(), true, false, false, null, null,
                Array.Empty<KeyValuePair<string, string>>(), body);
            var stem = StemOf(body) + " " + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = UniquePath(folder, stem, null);
            WriteDoc(path, doc);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ───────────────────────── 안쪽 도구

    private IEnumerable<string> TrashFiles()
    {
        try
        {
            if (!Directory.Exists(TrashFolder)) return Array.Empty<string>();
            return Directory.EnumerateFiles(TrashFolder).Where(IsMemoFile).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>한 번도 훑지 않았거나 모르는 id 면 먼저 훑는다.</summary>
    private Entry? Find(Guid id)
    {
        if (_scanned && TryFind(id, out var known) && File.Exists(known!.Path)) return known;
        if (ScanFolder()) _changedSinceReport = true;
        return TryFind(id, out var found) ? found : null;
    }

    /// <summary>
    /// id 의 파일과 그 내용. 기억한 자리를 그대로 믿지 않고 읽은 id 를 견준다 —
    /// 다른 PC 가 그 메모를 옮기고 같은 이름으로 다른 메모를 두었을 수 있다.
    /// 어긋나면 그 자리를 잊고 다시 훑어 한 번 더 찾는다. 그래도 어긋나면 없는 것으로 친다.
    /// </summary>
    private (Entry Entry, MemoDocument Doc)? Locate(Guid id)
    {
        var e = Find(id);
        for (var attempt = 0; e is not null; attempt++)
        {
            if (!File.Exists(e.Path))
            {
                // 데이터 폴더째 안 보이면 지운 것이 아니라 지금 못 읽는 것이다 — 부른 쪽은 다음에 다시 한다.
                if (!Directory.Exists(DataFolder)) throw new DirectoryNotFoundException(DataFolder);
                _entries.Remove(e.Path);
                return null;
            }

            var doc = ReadDoc(e.Path);
            if (doc.Id == id) return (e, doc);
            if (attempt > 0) return null;

            _entries.Remove(e.Path);
            if (ScanFolder()) _changedSinceReport = true;
            e = TryFind(id, out var again) ? again : null;
        }
        return null;
    }

    private bool TryFind(Guid id, out Entry? entry)
    {
        entry = _entries.Values.FirstOrDefault(e => !e.Hidden && e.Summary.Id == id);
        return entry is not null;
    }

    private MemoDocument ReadDoc(string path)
        => MemoFormat.Parse(File.ReadAllText(path), MemoFormat.IdFromPath(Relative(path)));

    private string Relative(string path) => System.IO.Path.GetRelativePath(Folder, path);

    private static bool IsMemoFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>파일 이름감: 제목에서 #태그를 빼고 FileStem 으로 다듬는다.</summary>
    private static string StemOf(string body)
        => MemoFormat.FileStem(Regex.Replace(TagInTitle.Replace(MemoFormat.TitleOf(body), ""), @"\s+", " "));

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static MemoSummary Summarize(MemoDocument doc, string path, DateTime updated)
        => new(doc.Id, path, MemoFormat.TitleOf(doc.Body), MemoFormat.PreviewOf(doc.Body),
            MemoFormat.TagsOfDoc(doc), doc.Pinned, doc.Daily, updated, MemoFormat.RevOf(doc.Body), false, doc.Reset);

    /// <summary>스스로 쓴 파일의 (크기, 고친 시각)을 기억해 바로 다음 훑기가 제 쓰기를 바뀜으로 읽지 않게 한다.</summary>
    private void Remember(string path, MemoDocument doc)
    {
        var fi = new FileInfo(path);
        _entries[path] = new Entry
        {
            Path = path, Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks,
            Summary = Summarize(doc, path, fi.LastWriteTime),
        };
    }

    /// <summary>폴더 안 파일 이름과 겹치지 않는 이름.md. 대소문자는 무시하고, self 는 겹침에서 뺀다.</summary>
    private static string UniquePath(string folder, string stem, string? self)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folder))
            foreach (var p in Directory.EnumerateFiles(folder))
                if (self is null || !SameName(p, self)) taken.Add(System.IO.Path.GetFileName(p));

        var name = stem + ".md";
        for (var n = 2; taken.Contains(name); n++)
            name = stem + " (" + n + ").md";
        return System.IO.Path.Combine(folder, name);
    }

    /// <summary>본문 줄바꿈을 \r\n 으로 맞춰 BOM 없는 UTF-8 로, 이름.tmp 에 쓴 뒤 바꿔친다.</summary>
    private static void WriteDoc(string path, MemoDocument doc)
    {
        var body = doc.Body.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        var text = MemoFormat.Serialize(doc with { Body = body });
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        try
        {
            File.WriteAllText(tmp, text, Utf8NoBom);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* 남아도 훑기가 무시한다 */ }
            throw;
        }

        try
        {
            if (File.Exists(path))
            {
                // 백업 이름을 줘서 원본이 이름 모를 곳에 남지 않게 한다.
                File.Replace(tmp, path, bak);
                try { File.Delete(bak); } catch { /* 남아도 훑기가 무시한다 */ }
            }
            else File.Move(tmp, path);
        }
        catch
        {
            if (File.Exists(path))
            {
                try { File.Delete(tmp); } catch { /* 남아도 훑기가 무시한다 */ }
                throw;
            }
            // 원본이 사라졌으면 새 글이 담긴 tmp 를 지우지 않고 제자리로 옮긴다. 실패하면 tmp 를 남긴 채 알린다.
            File.Move(tmp, path);
        }
    }
}
