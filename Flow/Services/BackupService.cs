using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Flow.Services;

/// <summary>백업 파일 하나에 대한 정보.</summary>
public sealed record BackupEntry(string Path, DateTime CreatedAt, int Routines, int Tasks, string Tag);

/// <summary>
/// data.json 을 백업 폴더로 복사해 둔다.
/// 파일 하나짜리 앱이라 백업도 복사 한 번이면 끝난다 — 몇 KB짜리라 부담이 없다.
/// </summary>
public static class BackupService
{
    /// <summary>보관할 개수. 넘으면 오래된 것부터 지운다.</summary>
    private const int KeepCount = 20;

    private const string Prefix = "data-";

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
            if (NextFreePath(suffix) is not { } target) return null;

            // 기존 백업을 덮어쓰지 않는다. 되돌리기 직전에 만드는 안전망이 사라지면 안 된다.
            File.Copy(DataStore.FilePath, target, overwrite: false);
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

            if (NextFreePath("-" + tag) is not { } target) return null;

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

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));
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

            entries.Add(new BackupEntry(file.FullName, file.LastWriteTime, routines, tasks, TagOf(file.Name)));
        }

        return entries;
    }

    /// <summary>
    /// 백업 파일을 현재 데이터 자리에 되돌린다.
    /// 되돌리기 자체도 되돌릴 수 있도록, 지금 상태를 먼저 백업해 둔다.
    /// </summary>
    public static bool Restore(string backupPath)
    {
        try
        {
            if (!File.Exists(backupPath)) return false;

            // 되돌린 게 잘못이었을 때를 대비한 안전망.
            Create("before-restore");

            File.Copy(backupPath, DataStore.FilePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 아직 없는 파일 이름을 찾는다.
    /// 초 단위 이름만 쓰면 같은 초에 두 번 백업할 때 이름이 겹쳐 앞의 것이 사라진다.
    /// </summary>
    private static string? NextFreePath(string suffix)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = Path.Combine(
                Directory, $"{Prefix}{DateTime.Now:yyyyMMdd-HHmmssfff}{suffix}.json");

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
            .GetFiles($"{Prefix}*.json")
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
