using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 반복 규칙 파일. 데이터 폴더의 repeats\&lt;루틴 Id&gt;.json 에 규칙 하나씩.
///
/// 한 파일에 모으지 않는 까닭: data.json 과 규칙은 클라우드에서 따로 도착한다.
/// 모아 두면 그 파일이 아직 안 온 PC 가 새 반복을 만들며 다른 PC 의 반복을 통째로 덮는다.
/// 나눠 두면 한 PC 의 쓰기가 다른 반복을 건드릴 수 없다.
///
/// 이 클래스는 스스로 지우지 않는다. 루틴 없는 파일도, 못 읽은 파일도 그대로 둔다 —
/// 덜 받아진 것일 수 있고, 지우면 다른 PC 에서도 사라진다.
/// </summary>
public sealed class RepeatStore
{
    private sealed record Entry(long Length, DateTime WriteUtc, RepeatRule? Rule, string Json);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<bool> _canWrite;
    private readonly Dictionary<Guid, Entry> _entries = new();

    public RepeatStore(string dataDirectory, Func<bool> canWrite)
    {
        Folder = Path.Combine(dataDirectory, "repeats");
        _canWrite = canWrite;
    }

    public string Folder { get; }

    /// <summary>쓰려다 못 쓴 규칙이 있는지. 있으면 다음 훑기 때 다시 써 본다.</summary>
    public bool HasUnsynced => _unsynced.Count > 0;

    /// <summary>쓰려다 못 쓴 규칙(파일이 잠김 · 폴더를 기다림). 다시 쓸 때까지 여기 들고 있는다.</summary>
    private readonly Dictionary<Guid, RepeatRule> _unsynced = new();

    private string PathOf(Guid id) => Path.Combine(Folder, id.ToString("D") + ".json");

    /// <summary>
    /// 폴더를 다시 읽는다. 지난번과 달라진 것이 있으면 true.
    /// 폴더가 없으면 빈 것으로 치고, 만들지 않는다 — 옛 방식 루틴만 쓰는 사람에게는 폴더도 없어야 한다.
    /// </summary>
    public bool Scan()
    {
        var seen = new HashSet<Guid>();
        var changed = false;

        FileInfo[] files;
        try
        {
            var folder = new DirectoryInfo(Folder);
            files = folder.Exists ? folder.GetFiles("*.json") : Array.Empty<FileInfo>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 폴더를 못 읽는 동안에는 아는 것을 그대로 둔다 — 잠깐 끊겼다고 규칙을 다 떼면 안 된다
            return false;
        }

        foreach (var file in files)
        {
            // "<id>.json.tmp" 는 *.json 에 안 걸리지만, 이름이 Id 가 아닌 파일은 여기서 거른다
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "D", out var id)) continue;
            seen.Add(id);

            long length;
            DateTime written;
            try
            {
                length = file.Length;
                written = file.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // 읽는 데 성공했던 파일이 그대로면 다시 읽지 않는다.
            // 못 읽었던 파일은 겉이 같아도 다시 본다 — 잠겨 있었을 수 있다.
            if (_entries.TryGetValue(id, out var known)
                && known.Rule is not null && known.Length == length && known.WriteUtc == written)
                continue;

            var rule = Read(file.FullName);
            var entry = new Entry(length, written, rule, rule is null ? "" : Serialize(rule));

            if (known is null || known.Json != entry.Json || (known.Rule is null) != (rule is null))
                changed = true;

            _entries[id] = entry;
        }

        foreach (var id in new List<Guid>(_entries.Keys))
        {
            if (seen.Contains(id)) continue;
            _entries.Remove(id);
            changed = true;
        }

        return changed;
    }

    /// <summary>못 읽으면 null. 잠김 · 잘림 · 0바이트 · JSON 아님을 가리지 않는다 — 다음 훑기에 다시 본다.</summary>
    private static RepeatRule? Read(string path)
    {
        try
        {
            var rule = JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), AppJsonContext.Default.RepeatRule);
            if (rule is null) return null;

            // 첫 차례가 없는 파일(손으로 만들었거나 덜 적힌 것)은 셀 수 없다. 이 앱은 늘 첫 차례를 적는다.
            // 읽은 것으로 치면 '남는' 항목이 서기 1년부터 밀린 것으로 나온다.
            if (rule.Anchor == default) return null;

            // "Days": null 로 적힌 파일이 계산을 죽이지 않게 한다
            rule.Days ??= new List<int>();
            rule.MonthDays ??= new List<int>();
            return rule;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Serialize(RepeatRule rule)
        => JsonSerializer.Serialize(rule, AppJsonContext.Default.RepeatRule);

    /// <summary>
    /// 읽어 둔 규칙의 사본. 파일이 없거나 못 읽었으면 null.
    /// 모르는 Unit 은 그대로 돌려준다 — "반복 정보 없음"으로 보일 뿐 버리지 않는다.
    /// </summary>
    public RepeatRule? Get(Guid id)
        => _entries.TryGetValue(id, out var entry) ? entry.Rule?.Clone() : null;

    /// <summary>
    /// 읽어 둔 규칙을 루틴에 단다. data.json 이 기준이다:
    /// Archived 인 루틴에만 달고, 그 밖의 루틴은 파일이 있어도 옛 방식(매일 · 요일)으로 둔다.
    /// </summary>
    public void Attach(AppData data)
    {
        var alive = new HashSet<Guid>();

        foreach (var routine in data.Routines)
        {
            if (!routine.Archived)
            {
                routine.Repeat = null;
                continue;
            }

            alive.Add(routine.Id);

            // 쓰려다 못 쓴 규칙이 있으면 파일의 옛 값이 아니라 그것을 단다. 다음 Sync 가 다시 쓴다.
            routine.Repeat = _unsynced.TryGetValue(routine.Id, out var waiting) ? waiting.Clone() : Get(routine.Id);
        }

        // 그 루틴이 없어졌거나 옛 방식으로 돌아갔으면 못 쓴 규칙도 쓸 데가 없다
        foreach (var id in new List<Guid>(_unsynced.Keys))
            if (!alive.Contains(id)) _unsynced.Remove(id);
    }

    /// <summary>
    /// 루틴에 달린 규칙 가운데 파일과 다른 것만 쓴다. 지우지 않는다. 못 쓴 것이 있으면 false.
    ///
    /// 못 읽은 파일 · 모르는 Unit 의 파일은 덮지 않는다: 그런 루틴은 Repeat 가 없거나 모르는 값이라
    /// 여기서 걸러진다. 덜 받아진 파일을 이쪽 값으로 덮으면 다른 PC 의 반복이 바뀐다.
    /// </summary>
    public bool Sync(AppData data)
    {
        var ok = true;

        foreach (var routine in data.Routines)
        {
            if (!routine.Archived || routine.Repeat is not { } rule || !RepeatEngine.IsKnown(rule)) continue;

            // 지우기로 해 둔 사이 그 루틴이 다시 새 반복이 됐다. 이제 쓰는 파일이다.
            _deleting.Remove(routine.Id);

            var json = Serialize(rule);
            if (_entries.TryGetValue(routine.Id, out var known) && known.Rule is not null && known.Json == json)
            {
                _unsynced.Remove(routine.Id);
                continue;
            }

            if (_canWrite() && Write(routine.Id, rule, json))
            {
                _unsynced.Remove(routine.Id);
                continue;
            }

            // 못 썼다(드라이브가 파일을 잡고 있음 · 폴더를 기다림). 잃지 않게 들고 있다가 다음에 다시 쓴다.
            _unsynced[routine.Id] = rule.Clone();
            ok = false;
        }

        return ok;
    }

    private bool Write(Guid id, RepeatRule rule, string json)
    {
        var path = PathOf(id);
        var temp = path + ".tmp";

        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(temp, json, Utf8NoBom);
            File.Move(temp, path, overwrite: true);

            // 스스로 쓴 것을 다음 훑기가 '바뀜'으로 읽지 않게 겉모습을 기억해 둔다
            var info = new FileInfo(path);
            _entries[id] = new Entry(info.Length, info.LastWriteTimeUtc, rule.Clone(), json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>지우기로 해 두고 아직 못 지운 규칙 파일이 있는지.</summary>
    public bool HasPendingDeletes => _deleting.Count > 0;

    /// <summary>data.json 이 저장된 뒤에 지울 규칙 파일.</summary>
    private readonly HashSet<Guid> _deleting = new();

    /// <summary>
    /// 그 루틴의 규칙 파일을 지우기로 해 둔다. 실제로 지우는 것은 <see cref="CompleteDeletes"/> —
    /// 그 루틴이 빠진 data.json 이 디스크에 남은 뒤다.
    ///
    /// 바로 지우면 순서가 뒤집힌다: data.json 은 1초 뒤에 저장되므로, 그 사이 꺼지거나 다른 PC 가 먼저 읽으면
    /// data.json 에는 루틴이 그대로인데 규칙만 없어 "반복 정보 없음"으로 보인다.
    /// 끝내 못 지운 파일은 루틴 없는 파일로 남을 뿐이라 해가 없다.
    ///
    /// 부르는 때는 셋뿐이다: 루틴을 지울 때, 할 일로 옮길 때, 옛 방식으로 반복을 바꿀 때.
    /// </summary>
    public void DeleteLater(Guid id)
    {
        // 지우는 것은 그 반복을 버리겠다는 뜻이다. 못 쓴 채 들고 있던 것은 지금 버린다 — 다시 쓰면 안 된다.
        _unsynced.Remove(id);
        _deleting.Add(id);
    }

    /// <summary>
    /// 지우기로 해 둔 규칙 파일을 지운다. <paramref name="saved"/> 는 방금 디스크에 남은 데이터다.
    /// 그 사이 그 루틴이 다시 새 반복이 됐으면(매일로 바꿨다가 도로 매월) 지우지 않는다.
    /// 못 지운 것(폴더를 기다림 · 파일이 잠김)은 남겨 두었다가 다음에 다시 한다.
    /// </summary>
    public void CompleteDeletes(AppData saved)
    {
        if (_deleting.Count == 0) return;

        var inUse = new HashSet<Guid>();
        foreach (var routine in saved.Routines)
            if (routine.Archived) inUse.Add(routine.Id);

        foreach (var id in new List<Guid>(_deleting))
            if (inUse.Contains(id) || Delete(id)) _deleting.Remove(id);
    }

    /// <summary>
    /// 그 루틴의 규칙 파일을 지금 지운다. 없으면 true.
    /// 루틴을 지우거나 옮길 때는 이것이 아니라 <see cref="DeleteLater"/> 를 쓴다.
    /// </summary>
    public bool Delete(Guid id)
    {
        var path = PathOf(id);

        try
        {
            // 지우는 것은 그 반복을 버리겠다는 뜻이다. 못 쓴 채 들고 있던 것도 버린다.
            _unsynced.Remove(id);

            if (!File.Exists(path))
            {
                _entries.Remove(id);
                return true;
            }

            if (!_canWrite()) return false;

            File.Delete(path);
            _entries.Remove(id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
