using System;
using System.IO;
using System.Linq;

namespace Flow.Services;

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
            var target = Path.Combine(Directory, $"{Prefix}{DateTime.Now:yyyyMMdd-HHmmss}{suffix}.json");

            File.Copy(DataStore.FilePath, target, overwrite: true);
            Prune();

            return target;
        }
        catch (Exception)
        {
            // 백업 실패가 앱을 방해해선 안 된다.
            return null;
        }
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
