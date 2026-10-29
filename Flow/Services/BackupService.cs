using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Flow.Services;

/// <summary>백업 파일 하나에 대한 정보.</summary>
public sealed record BackupEntry(string Path, DateTime CreatedAt, int Routines, int Tasks, string Tag, int Memos);

/// <summary>
/// data.json 과 메모 폴더를 zip 하나로 묶어 백업 폴더에 둔다(엔트리 data.json, memos/파일 이름 — 휴지통 제외).
/// 예전 .json 백업도 목록에 보이고 되돌릴 수 있다.
/// </summary>
public static class BackupService
{
    /// <summary>보관할 개수. 넘으면 오래된 것부터 지운다.</summary>
    private const int KeepCount = 20;

    private const string Prefix = "data-";
    private const string DataEntry = "data.json";
    private const string MemoPrefix = "memos/";
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string Directory => Path.Combine(DataStore.Directory, "backup");

    /// <summary>
    /// 지금 상태를 백업한다. 만든 파일 경로를 돌려주고, 실패하면 null.
    /// </summary>
    /// <param name="tag">파일 이름 뒤에 붙일 짧은 표시(update, manual 등). 없으면 시각만.</param>
    public static string? Create(string? tag = null)
    {
        try
        {
            if (!File.Exists(DataStore.FilePath)) return null;

            System.IO.Directory.CreateDirectory(Directory);

            var suffix = string.IsNullOrWhiteSpace(tag) ? "" : "-" + tag;
            if (NextFreePath(suffix, ".zip") is not { } target) return null;

            // 임시 파일에 다 쓴 뒤 옮긴다. 덜 쓴 zip 이 백업 이름으로 남으면 안 된다.
            // 옮길 때도 덮어쓰지 않는다. 되돌리기 직전에 만드는 안전망이 사라지면 안 된다.
            var tmp = target + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, false, Utf8))
                {
                    AddFile(archive, DataEntry, DataStore.FilePath);

                    var memoFolder = Path.Combine(DataStore.Directory, "memos");
                    if (System.IO.Directory.Exists(memoFolder))
                        foreach (var file in System.IO.Directory.EnumerateFiles(memoFolder).Where(IsMemoFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                            AddFile(archive, MemoPrefix + Path.GetFileName(file), file);
                }

                File.Move(tmp, target, overwrite: false);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception) { /* 남아도 목록에 안 보인다 */ }
                throw;
            }

            Prune();

            return target;
        }
        catch (Exception)
        {
            // 백업 실패가 앱을 방해해선 안 된다.
            return null;
        }
    }

    /// <summary>
    /// 지금 메모리에 있는 내용을 백업으로 남긴다.
    /// 다른 PC의 내용으로 갈아끼우기 직전처럼, 파일에는 없고 메모리에만 있는 것을 지킬 때 쓴다.
    /// </summary>
    public static string? CreateFromJson(string json, string tag)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            if (NextFreePath("-" + tag, ".json") is not { } target) return null;

            File.WriteAllText(target, json);
            Prune();

            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>최근 것부터 정렬한 백업 목록. 각 파일을 열어 무엇이 들어 있는지도 읽는다.</summary>
    public static IReadOnlyList<BackupEntry> List()
    {
        var entries = new List<BackupEntry>();

        foreach (var file in Files())
        {
            var routines = 0;
            var tasks = 0;
            var memos = 0;

            try
            {
                string json;
                if (IsZip(file.Name))
                {
                    using var archive = ZipFile.Open(file.FullName, ZipArchiveMode.Read, Utf8);
                    memos = archive.Entries.Count(e => MemoNameOf(e) is not null);
                    var data = archive.GetEntry(DataEntry);
                    if (data is null) throw new InvalidDataException();
                    using var reader = new StreamReader(data.Open(), Utf8);
                    json = reader.ReadToEnd();
                }
                else json = File.ReadAllText(file.FullName);

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty("Routines", out var r) && r.ValueKind == JsonValueKind.Array)
                    routines = r.GetArrayLength();
                if (root.TryGetProperty("Tasks", out var t) && t.ValueKind == JsonValueKind.Array)
                    tasks = t.GetArrayLength();
            }
            catch (Exception)
            {
                // 못 읽는 파일이어도 목록에는 남겨 둔다. 사용자가 판단하면 된다.
            }

            entries.Add(new BackupEntry(file.FullName, file.LastWriteTime, routines, tasks, TagOf(file.Name), memos));
        }

        return entries;
    }

    /// <summary>
    /// 백업 파일을 현재 데이터 자리에 되돌린다.
    /// 되돌리기 자체도 되돌릴 수 있도록, 지금 상태를 먼저 백업해 둔다.
    /// .json 은 data.json 만. .zip 은 메모도 되돌린다(백업에 없는 지금 메모는 휴지통으로).
    /// </summary>
    public static bool Restore(string backupPath)
    {
        try
        {
            if (!File.Exists(backupPath)) return false;

            if (!IsZip(backupPath))
            {
                // 되돌린 게 잘못이었을 때를 대비한 안전망.
                Create("before-restore");

                File.Copy(backupPath, DataStore.FilePath, overwrite: true);
                return true;
            }

            using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Read, Utf8);
            var data = archive.GetEntry(DataEntry);
            if (data is null) return false;

            Create("before-restore");

            var tmp = DataStore.FilePath + ".restore.tmp";
            try
            {
                using (var input = data.Open())
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                File.Move(tmp, DataStore.FilePath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception) { /* 남아도 무시한다 */ }
                throw;
            }

            return RestoreMemos(archive);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RestoreMemos(ZipArchive archive)
    {
        var store = new MemoStore(DataStore.Directory, () => true, () => DateTime.Now);
        store.Scan();

        var currentById = new Dictionary<Guid, MemoSummary>();
        foreach (var m in store.Memos) currentById.TryAdd(m.Id, m);

        // 백업의 메모를 id 와 함께 읽는다. 머리말이 없으면 파일 이름에서 나온 id 를 쓴다(MemoStore 와 같다).
        var incoming = new List<(string Name, Guid Id, byte[] Bytes)>();
        foreach (var entry in archive.Entries)
        {
            if (MemoNameOf(entry) is not { } name) continue;

            using var ms = new MemoryStream();
            using (var input = entry.Open()) input.CopyTo(ms);
            var bytes = ms.ToArray();
            var doc = MemoFormat.Parse(Utf8.GetString(bytes), MemoFormat.IdFromPath(name));
            incoming.Add((name, doc.Id, bytes));
        }

        var ok = true;

        // 백업에 없는 지금 메모는 휴지통으로(지우지 않는다). 이름이 비도록 먼저 한다.
        var backupIds = incoming.Select(i => i.Id).ToHashSet();
        foreach (var m in store.Memos.Where(m => !backupIds.Contains(m.Id)).ToList())
            if (!store.Delete(m.Id)) ok = false;

        var folder = store.Folder;
        if (incoming.Count == 0) return ok;
        System.IO.Directory.CreateDirectory(folder);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.IO.Directory.EnumerateFiles(folder)) taken.Add(Path.GetFileName(p));

        var claimed = new HashSet<Guid>();
        foreach (var (name, id, bytes) in incoming)
        {
            string target;
            if (currentById.TryGetValue(id, out var current) && claimed.Add(id)
                && File.Exists(current.Path))
            {
                target = current.Path;
            }
            else
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                var candidate = name;
                for (var n = 2; taken.Contains(candidate); n++) candidate = $"{stem} ({n}){ext}";
                taken.Add(candidate);
                target = Path.Combine(folder, candidate);
            }

            var tmp = target + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception) { /* 남아도 훑기가 무시한다 */ }
                throw;
            }
        }

        return ok;
    }

    private static void AddFile(ZipArchive archive, string entryName, string path)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    /// <summary>memos/ 바로 아래의 메모 파일이면 파일 이름, 아니면 null(휴지통 등 하위 폴더 제외).</summary>
    private static string? MemoNameOf(ZipArchiveEntry entry)
    {
        var full = entry.FullName.Replace('\\', '/');
        if (!full.StartsWith(MemoPrefix, StringComparison.Ordinal)) return null;
        var name = full[MemoPrefix.Length..];
        if (name.Length == 0 || name.Contains('/') || !IsMemoFile(name)) return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name;
    }

    private static bool IsMemoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZip(string path)
        => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 아직 없는 파일 이름을 찾는다.
    /// 초 단위 이름만 쓰면 같은 초에 두 번 백업할 때 이름이 겹쳐 앞의 것이 사라진다.
    /// </summary>
    private static string? NextFreePath(string suffix, string extension)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = Path.Combine(
                Directory, $"{Prefix}{DateTime.Now:yyyyMMdd-HHmmssfff}{suffix}{extension}");

            if (!File.Exists(candidate)) return candidate;
            Thread.Sleep(2);
        }

        return null;
    }

    private static string TagOf(string fileName)
    {
        // data-20260907-150415-update.json  ->  update
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('-');
        return parts.Length >= 4 ? string.Join("-", parts[3..]) : "";
    }

    /// <summary>백업 개수와 가장 최근 백업 시각.</summary>
    public static (int Count, DateTime? Newest) Summary()
    {
        try
        {
            var files = Files();
            if (files.Length == 0) return (0, null);

            return (files.Length, files[0].LastWriteTime);
        }
        catch (Exception)
        {
            return (0, null);
        }
    }

    /// <summary>최근 것부터 정렬된 백업 파일 목록.</summary>
    private static FileInfo[] Files()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        return new DirectoryInfo(Directory)
            .GetFiles($"{Prefix}*")
            .Where(f => IsZip(f.Name) || f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTime)
            .ToArray();
    }

    private static void Prune()
    {
        foreach (var stale in Files().Skip(KeepCount))
        {
            try
            {
                stale.Delete();
            }
            catch (Exception)
            {
                // 하나 못 지워도 다음에 다시 시도한다.
            }
        }
    }
}
