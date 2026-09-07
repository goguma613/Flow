using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Services;

public sealed record UpdateInfo(Version Version, string DownloadUrl);

/// <summary>
/// GitHub Releases 에서 새 버전을 확인하고 받아 둔다.
/// 확인은 하루 한 번, 수 KB짜리 요청 하나뿐이라 평소 동작에 영향을 주지 않는다.
/// 네트워크가 없거나 GitHub 이 응답하지 않아도 앱은 그대로 돌아간다.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/goguma613/Flow/releases/latest";
    public const string ReleasesPage = "https://github.com/goguma613/Flow/releases";

    /// <summary>릴리스에 올라가는 실행 파일 이름. 이 이름이 아니면 찾지 못한다.</summary>
    private const string AssetName = "Flow.exe";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>현재 실행 중인 앱의 버전.</summary>
    public static Version Current { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    /// <summary>받아 둔 새 실행 파일이 놓이는 곳.</summary>
    private static string StagedPath => Path.Combine(DataStore.Directory, "update", AssetName);

    /// <summary>내려받기까지 끝난 새 버전. 아직 적용 전.</summary>
    public Version? StagedVersion { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// 새 버전이 있으면 내려받아 둔다. 성공하면 적용 준비가 된 버전을 돌려준다.
    /// 예외는 밖으로 던지지 않는다 — 업데이트 실패가 앱을 방해해선 안 된다.
    /// </summary>
    public async Task<Version?> CheckAndStageAsync(CancellationToken token = default)
    {
        try
        {
            LastError = null;

            var info = await FetchLatestAsync(token).ConfigureAwait(false);
            if (info is null || info.Version <= Current) return null;

            await DownloadAsync(info, token).ConfigureAwait(false);

            StagedVersion = info.Version;
            return info.Version;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private static async Task<UpdateInfo?> FetchLatestAsync(CancellationToken token)
    {
        using var http = new HttpClient { Timeout = RequestTimeout };

        // GitHub API 는 User-Agent 가 없으면 403 을 준다.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Flow", CurrentText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var json = await http.GetStringAsync(LatestReleaseUrl, token).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagElement)) return null;
        if (ParseVersion(tagElement.GetString()) is not { } version) return null;

        if (!root.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name)) continue;
            if (!string.Equals(name.GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!asset.TryGetProperty("browser_download_url", out var url)) continue;

            if (url.GetString() is { Length: > 0 } downloadUrl)
                return new UpdateInfo(version, downloadUrl);
        }

        return null;
    }

    private static async Task DownloadAsync(UpdateInfo info, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(StagedPath)!;
        Directory.CreateDirectory(directory);

        var temporary = StagedPath + ".part";

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Flow", CurrentText));

            await using var source = await http.GetStreamAsync(info.DownloadUrl, token).ConfigureAwait(false);
            await using var target = File.Create(temporary);
            await source.CopyToAsync(target, token).ConfigureAwait(false);
        }

        // 다 받은 뒤에 제자리로 옮긴다. 중간에 끊겨도 반쪽짜리가 남지 않는다.
        File.Move(temporary, StagedPath, overwrite: true);
    }

    /// <summary>
    /// 받아 둔 파일로 교체하고 앱을 다시 띄운다.
    /// 실행 중인 exe 는 덮어쓸 수 없지만 이름은 바꿀 수 있다. 그 성질을 이용한다.
    /// </summary>
    public bool ApplyAndRestart()
    {
        try
        {
            if (StagedVersion is null || !File.Exists(StagedPath)) return false;
            if (Environment.ProcessPath is not { Length: > 0 } currentPath) return false;

            var retired = currentPath + ".old";
            if (File.Exists(retired)) File.Delete(retired);

            File.Move(currentPath, retired);

            try
            {
                File.Copy(StagedPath, currentPath, overwrite: true);
            }
            catch (Exception)
            {
                // 복사에 실패하면 원래 파일을 되돌려 놓는다. 앱이 사라지면 안 된다.
                File.Move(retired, currentPath);
                throw;
            }

            Process.Start(new ProcessStartInfo(currentPath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>지난 교체에서 남은 옛 실행 파일을 치운다. 시작할 때 한 번 부른다.</summary>
    public static void CleanUpAfterUpdate()
    {
        try
        {
            if (Environment.ProcessPath is not { Length: > 0 } currentPath) return;

            var retired = currentPath + ".old";
            if (File.Exists(retired)) File.Delete(retired);

            if (File.Exists(StagedPath)) File.Delete(StagedPath);
        }
        catch (Exception)
        {
            // 다음 실행에서 다시 시도하면 된다.
        }
    }

    /// <summary>"v1.2.3" 또는 "1.2.3" 을 버전으로 읽는다.</summary>
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var cleaned = new string(tag.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return Version.TryParse(cleaned, out var version) ? version : null;
    }
}
