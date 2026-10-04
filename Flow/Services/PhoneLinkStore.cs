using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 폰과 주고받는 두 파일을 읽고 쓴다.
///
/// 두 파일 모두 <b>폰이 만든다.</b> 폰 앱은 구글이 심사 없이 내주는 가장 좁은 권한(drive.file)만 쓰는데,
/// 그 권한으로는 폰 앱이 직접 만든 파일만 만질 수 있다. 그래서
/// - PC는 이 파일들을 새로 만들지 않는다. 폰이 연결을 마쳐 파일이 있을 때만 움직인다.
///   폰을 안 쓰는 사람에게는 아무 일도 일어나지 않고 업로드도 늘지 않는다.
/// - phone-view.json 은 지우고 새로 만들지 않고 <b>제자리에서 덮어쓴다.</b>
///   새 파일이 되면 드라이브 안의 번호가 바뀌어 폰 앱이 더는 그 파일에 손을 못 댄다.
/// </summary>
public sealed class PhoneLinkStore
{
    /// <summary>폰 앱이 내 드라이브에 만드는 폴더 이름. 폰 쪽과 반드시 같아야 한다.</summary>
    public const string FolderName = "Flow 폰";
    public const string InboxFileName = "phone-inbox.json";
    public const string ViewFileName = "phone-view.json";

    /// <summary>데이터 폴더에서 몇 단계 위까지 연결 폴더를 찾을지. 보통은 바로 위(내 드라이브)에 있다.</summary>
    private const int SearchDepth = 3;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _dataDirectory;
    private (DateTime WrittenAt, long Length)? _inboxStamp;
    private string? _lastViewBody;

    public PhoneLinkStore(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    /// <summary>
    /// 폰이 만든 연결 폴더. 없으면 null.
    /// 데이터 폴더 안, 그 위, 그 위의 위 순서로 찾는다 —
    /// 데이터가 "내 드라이브\Flow" 에 있으면 "내 드라이브\Flow 폰" 이 걸린다.
    /// </summary>
    public string? FindFolder()
    {
        try
        {
            var directory = new DirectoryInfo(_dataDirectory);
            for (var depth = 0; directory is not null && depth < SearchDepth; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, FolderName);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        catch (Exception)
        {
            // 경로가 이상하거나 드라이브가 빠졌다. 연결이 없는 것으로 본다.
        }

        return null;
    }

    /// <summary>
    /// inbox 가 지난번 읽은 뒤로 바뀌었으면 읽어 온다. 안 바뀌었거나, 없거나, 아직 덜 받아져 깨져 있으면 null.
    /// 깨져 있을 때는 표시를 갱신하지 않아서 다음 번에 다시 읽는다.
    /// </summary>
    public IReadOnlyList<PhoneOp>? ReadInboxIfChanged()
    {
        if (FindFolder() is not { } folder) return null;

        var path = Path.Combine(folder, InboxFileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            var stamp = (info.LastWriteTimeUtc, info.Length);
            if (_inboxStamp == stamp) return null;

            var inbox = ParseInbox(File.ReadAllText(path));
            if (inbox is null) return null;

            _inboxStamp = stamp;
            return inbox.Ops;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>바뀌었는지 따지지 않고 읽는다. 앱을 켤 때 쓴다.</summary>
    public IReadOnlyList<PhoneOp>? ReadInbox()
    {
        _inboxStamp = null;
        return ReadInboxIfChanged();
    }

    /// <summary>
    /// 폰이 볼 오늘 화면을 쓴다. 폰이 만든 파일이 있을 때만, 내용이 바뀌었을 때만 쓴다.
    /// 만든 시각만 다르고 내용이 같으면 쓰지 않는다 — 쓰면 그게 곧 업로드다.
    /// </summary>
    /// <returns>실제로 썼으면 true.</returns>
    public bool WriteView(PhoneView view)
    {
        if (FindFolder() is not { } folder) return false;

        var path = Path.Combine(folder, ViewFileName);
        if (!File.Exists(path)) return false;

        try
        {
            var generatedAt = view.GeneratedAt;
            view.GeneratedAt = default;
            var body = JsonSerializer.Serialize(view, AppJsonContext.Default.PhoneView);
            view.GeneratedAt = generatedAt;

            if (body == _lastViewBody) return false;

            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(view, AppJsonContext.Default.PhoneView));

            // FileMode.Open — 있는 파일을 연다. Create 나 '임시 파일 후 교체'를 쓰면
            // 드라이브에서 다른 파일이 되어 폰 앱이 권한을 잃는다.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(0);
                stream.Write(bytes, 0, bytes.Length);
            }

            _lastViewBody = body;
            return true;
        }
        catch (Exception)
        {
            // 드라이브가 잠시 잡고 있거나 빠졌다. 다음 변경 때 다시 쓴다.
            return false;
        }
    }

    public static PhoneInbox? ParseInbox(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.PhoneInbox);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
