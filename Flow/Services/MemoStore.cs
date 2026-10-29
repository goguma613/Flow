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

/// <summary>목록에 보이는 메모 하나. Unreadable 이면 잠겼거나 덜 받아진 파일이라 이전 값(없으면 파일 이름)을 보인다.</summary>
public sealed record MemoSummary(Guid Id, string Path, string Title, string Preview,
    IReadOnlyList<string> Tags, bool Pinned, bool Daily, DateTime Updated, string Rev, bool Unreadable);

public sealed record MemoFile(MemoDocument Doc, string Path, string Rev);

public enum SaveOutcome { Saved, Unchanged, Conflict, Missing, Failed }

public sealed record SaveResult(SaveOutcome Outcome, string? Rev, Guid? CopyId);

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

    /// <summary>안쪽 훑기(찾다가 다시 훑기)가 찾은 바뀜. 다음 Scan() 이 대신 알린다.</summary>
    private bool _changedSinceReport;

    public MemoStore(string dataDirectory, Func<bool> canWrite, Func<DateTime> now)
    {
        Folder = System.IO.Path.Combine(dataDirectory, "memos");
        TrashFolder = System.IO.Path.Combine(Folder, "휴지통");
        _canWrite = canWrite;
        _now = now;
    }

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
                foreach (var p in Directory.EnumerateFiles(Folder))
                    if (IsMemoFile(p)) files[p] = new FileInfo(p);
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

    public MemoFile? Create(string body)
    {
        if (!_canWrite()) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            var doc = new MemoDocument(Guid.NewGuid(), true, false, false, null, null,
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

    public SaveResult Save(Guid id, string body, string baseRev)
    {
        if (!_canWrite()) return new SaveResult(SaveOutcome.Failed, null, null);
        try
        {
            // 그 자리에 다른 메모가 와 있으면 덮지 않는다. 끝내 못 찾으면 지워진 것으로 본다.
            if (Locate(id) is not (var e, var current))
            {
                var made = Create(body);
                return made is null
                    ? new SaveResult(SaveOutcome.Failed, null, null)
                    : new SaveResult(SaveOutcome.Missing, null, made.Doc.Id);
            }

            var currentRev = MemoFormat.RevOf(current.Body);
            if (currentRev != baseRev)
            {
                var copyBody = MemoFormat.AppendToTitle(body,
                    " (충돌 사본 " + _now().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
                var copy = Create(copyBody);
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

    // ───────────────────────── 휴지통

    public bool Delete(Guid id)
    {
        if (!_canWrite()) return false;
        try
        {
            if (Locate(id) is not (var e, var found)) return false;
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
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

    public bool RestoreFromTrash(Guid id)
    {
        if (!_canWrite()) return false;
        try
        {
            foreach (var path in TrashFiles())
            {
                var doc = ReadDoc(path);
                if (doc.Id != id) continue;
                doc = doc with { HasHeader = true, Deleted = null };
                Directory.CreateDirectory(Folder);
                var target = UniquePath(Folder, StemOf(doc.Body), null);
                WriteDoc(target, doc);
                Remember(target, doc);
                File.Delete(path);
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
            MemoFormat.TagsOfDoc(doc), doc.Pinned, doc.Daily, updated, MemoFormat.RevOf(doc.Body), false);

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
