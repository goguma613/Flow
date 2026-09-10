using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Flow.Services;

/// <summary>사용자가 고른 저장 위치를 적어 두는 쪽지.</summary>
public sealed class DataLocationPointer
{
    public string? Directory { get; set; }
}

/// <summary>
/// 데이터를 어느 폴더에 둘지 기억한다.
///
/// 이 쪽지만은 늘 %APPDATA% 에 있어야 한다 — 데이터 폴더 안에 두면
/// "어디를 봐야 하는지 알려면 먼저 거기를 봐야 한다"는 순환에 빠진다.
///
/// 쪽지 이름에 실행 파일 경로를 섞는 이유: 실제 사용본과 시험본이 각자 다른 곳을
/// 가리킬 수 있어야 한다. 한쪽을 옮겨도 다른 쪽은 그대로다.
/// </summary>
public static class DataLocation
{
    private static readonly string PointerPath = ResolvePointerPath();

    /// <summary>사용자가 고른 폴더. 고른 적이 없으면 null.</summary>
    public static string? Chosen => Read()?.Directory;

    /// <summary>이 폴더를 앞으로 쓰겠다고 적어 둔다. 다음 실행부터 여기를 본다.</summary>
    public static bool Remember(string directory)
    {
        try
        {
            var home = Path.GetDirectoryName(PointerPath);
            if (!string.IsNullOrEmpty(home)) Directory.CreateDirectory(home);

            var json = JsonSerializer.Serialize(
                new DataLocationPointer { Directory = directory },
                AppJsonContext.Default.DataLocationPointer);

            File.WriteAllText(PointerPath, json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>고른 폴더를 잊고 기본 자리(실행 파일 옆)로 돌아간다.</summary>
    public static void Forget()
    {
        try
        {
            if (File.Exists(PointerPath)) File.Delete(PointerPath);
        }
        catch (Exception)
        {
            // 못 지워도 큰일은 아니다.
        }
    }

    /// <summary>
    /// 새 폴더로 옮길 준비를 한다.
    ///
    /// 옮긴다기보다 <b>맞춰 간다</b>에 가깝다:
    /// - 그쪽에 이미 데이터가 있으면(다른 PC가 먼저 올려 둔 경우) 그것을 그대로 쓴다.
    ///   여기 것을 부어 넣으면 그 PC의 최신 내용을 덮어쓴다.
    /// - 그쪽이 비어 있으면 여기 것을 <b>복사</b>해 넣는다. 원본은 지우지 않는다 —
    ///   옮기다 잘못돼도 돌아갈 자리가 있어야 한다.
    /// </summary>
    public static MoveResult Prepare(string target)
    {
        try
        {
            Directory.CreateDirectory(target);

            var probe = Path.Combine(target, ".flow-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            var targetData = Path.Combine(target, "data.json");
            if (File.Exists(targetData)) return MoveResult.AdoptedExisting;

            if (File.Exists(DataStore.FilePath))
            {
                File.Copy(DataStore.FilePath, targetData, overwrite: false);
                CopyFolder(Path.Combine(DataStore.Directory, "backup"), Path.Combine(target, "backup"));
                return MoveResult.CopiedHere;
            }

            return MoveResult.StartedEmpty;
        }
        catch (Exception)
        {
            return MoveResult.Failed;
        }
    }

    private static void CopyFolder(string from, string to)
    {
        if (!Directory.Exists(from)) return;

        Directory.CreateDirectory(to);

        foreach (var file in Directory.GetFiles(from))
        {
            var destination = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
    }

    private static DataLocationPointer? Read()
    {
        try
        {
            if (!File.Exists(PointerPath)) return null;

            var pointer = JsonSerializer.Deserialize(
                File.ReadAllText(PointerPath), AppJsonContext.Default.DataLocationPointer);

            return string.IsNullOrWhiteSpace(pointer?.Directory) ? null : pointer;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ResolvePointerPath()
    {
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flow");

        var exe = (Environment.ProcessPath ?? "flow").ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(exe));

        return Path.Combine(home, $"location-{Convert.ToHexString(hash, 0, 6)}.json");
    }
}

/// <summary>폴더를 바꿀 때 실제로 무슨 일이 있었는지.</summary>
public enum MoveResult
{
    /// <summary>그쪽에 이미 데이터가 있어 그것을 쓰기로 했다. 다른 PC에서 올려 둔 것이다.</summary>
    AdoptedExisting,

    /// <summary>그쪽이 비어 있어 여기 것을 복사해 넣었다.</summary>
    CopiedHere,

    /// <summary>양쪽 다 비어 있어 새로 시작한다.</summary>
    StartedEmpty,

    /// <summary>쓸 수 없는 폴더다.</summary>
    Failed
}
