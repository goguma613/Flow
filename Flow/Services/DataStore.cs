using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Flow.Models;

namespace Flow.Services;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppData))]
[JsonSerializable(typeof(DeviceState))]
[JsonSerializable(typeof(DataLocationPointer))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>
/// JSON 파일 하나로 모든 상태를 보관한다. DB 엔진도, 백그라운드 프로세스도 없다.
/// 저장은 임시 파일에 쓴 뒤 교체하는 방식이라 쓰기 도중 전원이 꺼져도 원본이 깨지지 않는다.
/// </summary>
public sealed class DataStore : IDisposable
{
    private const int SaveDebounceMs = 700;

    private readonly Lock _gate = new();

    // 마지막으로 우리가 읽거나 쓴 파일의 모습. 이것과 달라지면 다른 PC가 바꾼 것이다.
    private long _stampTicks;
    private long _stampLength;
    private readonly Timer _saveTimer;
    private AppData? _pending;
    private bool _disposed;

    public DataStore()
    {
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>저장된 파일이 없어 예시 데이터로 시작했는지. 첫 실행에 사용법을 띄우는 데 쓴다.</summary>
    public bool StartedFresh { get; private set; }

    /// <summary>
    /// 예전에 쓰던 저장 위치인데 지금 읽을 수 없는 상태.
    ///
    /// 구글 드라이브 같은 클라우드 폴더는 그 프로그램이 떠야 나타난다.
    /// 부팅 직후처럼 아직 안 붙었을 때 예시 데이터로 시작해 버리면,
    /// 잠시 뒤 폴더가 붙는 순간 그 예시가 진짜 데이터를 덮어쓴다.
    /// 그래서 이 상태에서는 <b>아무것도 저장하지 않고</b> 폴더가 돌아오기를 기다린다.
    /// </summary>
    public bool WaitingForFolder { get; private set; }

    /// <summary>
    /// 데이터는 실행 파일 옆 data 폴더에 둔다.
    /// 앱을 여러 벌 두면(실제 사용본 · 시험본) 각자 자기 데이터를 쓰게 되어 서로 건드리지 않는다.
    /// 폴더에 쓸 수 없으면 사용자 프로필로 물러난다.
    /// </summary>
    public static string Directory { get; } = ResolveDirectory();

    public static string FilePath { get; } = Path.Combine(Directory, "data.json");

    /// <summary>예전 버전이 쓰던 위치. 여기 있던 내용은 한 번만 새 위치로 옮겨 온다.</summary>
    private static string LegacyDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flow");

    private static string ResolveDirectory()
    {
        // 시험할 때 실제 사용 데이터를 건드리지 않도록 강제로 지정할 수 있다.
        var overridden = Environment.GetEnvironmentVariable("FLOW_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        // 사용자가 직접 고른 폴더가 있으면 그곳. 클라우드 폴더를 가리킬 때 쓴다.
        // 폴더가 지금 없더라도 그대로 가리킨다 — 없으면 Load 가 기다린다.
        if (DataLocation.Chosen is { } chosen) return chosen;

        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDirectory) && IsWritable(exeDirectory))
            return Path.Combine(exeDirectory, "data");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flow");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".flow-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>예전 위치(%APPDATA%\Flow)에 있던 내용을 새 위치로 한 번 복사해 온다.</summary>
    private static void MigrateFromLegacyIfNeeded()
    {
        try
        {
            if (string.Equals(LegacyDirectory, Directory, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(FilePath)) return;

            var legacyFile = Path.Combine(LegacyDirectory, "data.json");
            if (!File.Exists(legacyFile)) return;

            System.IO.Directory.CreateDirectory(Directory);

            // 옮기는 게 아니라 복사한다. 원본은 그대로 남겨 둔다.
            File.Copy(legacyFile, FilePath);
        }
        catch (Exception)
        {
            // 옮겨오지 못하면 새 위치에서 새로 시작할 뿐이다.
        }
    }

    private static string TempPath => FilePath + ".tmp";
    private static string BackupPath => FilePath + ".bak";

    public AppData Load()
    {
        // 기기별 파일을 먼저 읽어야 "여기 데이터가 있었는지"를 알 수 있다.
        var everInitialized = DeviceStore.Load()?.DataFolderInitialized == true;

        // 옛 위치에서 끌어오는 것은 정말 처음일 때만이다.
        // 이미 쓰던 자리인데 지금 안 보이는 것뿐이라면, 옛 데이터를 부어 넣는 순간
        // 클라우드가 붙었을 때 최신 내용과 충돌하거나 그것을 덮어쓴다.
        if (!everInitialized) MigrateFromLegacyIfNeeded();

        if (TryRead() is { } found)
        {
            DeviceStore.MarkDataFolderReady();
            RememberStamp();
            return WithDeviceState(found);
        }

        if (everInitialized)
        {
            // 있어야 할 데이터가 없다. 예시로 시작하면 그것이 진짜를 덮어쓴다.
            WaitingForFolder = true;
            return WithDeviceState(new AppData());
        }

        StartedFresh = true;
        DeviceStore.MarkDataFolderReady();
        return WithDeviceState(CreateSeed());
    }

    /// <summary>
    /// 기다리던 폴더가 돌아왔는지 다시 본다. 읽어냈으면 그 데이터를 돌려주고,
    /// 아직이면 null. 폴더가 붙기 전까지는 저장이 멈춰 있다.
    /// </summary>
    public AppData? TryRecover()
    {
        if (!WaitingForFolder) return null;
        if (TryRead() is not { } found) return null;

        WaitingForFolder = false;
        DeviceStore.MarkDataFolderReady();
        RememberStamp();
        return WithDeviceState(found);
    }

    private static AppData? TryRead()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = Deserialize(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }

            if (File.Exists(BackupPath))
            {
                var recovered = Deserialize(File.ReadAllText(BackupPath));
                if (recovered is not null) return recovered;
            }
        }
        catch (Exception)
        {
            // 파일이 손상되었더라도 앱은 떠야 한다.
        }

        return null;
    }

    /// <summary>
    /// 창 위치와 알림 상태처럼 이 PC 것인 값들을 얹는다.
    /// data.json 은 클라우드를 타고 다른 PC로 갈 수 있으므로 그런 값이 들어 있지 않다.
    /// </summary>
    private static AppData WithDeviceState(AppData data)
    {
        DeviceStore.Apply(data);
        return data;
    }

    /// <summary>변경을 예약한다. 연속 입력 중에는 디스크를 건드리지 않는다.</summary>
    public void RequestSave(AppData data)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = data;
            _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>예약된 변경을 즉시 디스크에 반영한다.</summary>
    public void Flush()
    {
        AppData? data;
        lock (_gate)
        {
            data = _pending;
            _pending = null;
        }

        if (data is null) return;

        // 폴더가 아직 안 왔으면 아무것도 쓰지 않는다. 쓰는 순간 진짜 데이터를 덮어쓴다.
        if (WaitingForFolder) return;

        // 이 PC 몫은 따로 챙긴다. data.json 에는 들어가지 않는 값들이다.
        DeviceStore.Capture(data);

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(TempPath, JsonSerializer.Serialize(data, AppJsonContext.Default.AppData));

            if (File.Exists(FilePath)) File.Replace(TempPath, FilePath, BackupPath, true);
            else File.Move(TempPath, FilePath);

            // 방금 우리가 쓴 모습을 기억한다. 이것과 달라지면 남이 바꾼 것이다.
            RememberStamp();
        }
        catch (Exception ex)
        {
            // 저장 실패가 앱을 죽여선 안 되지만, 흔적은 남긴다.
            LastError = ex;
            TryLog(ex);
        }
    }

    private void RememberStamp()
    {
        try
        {
            var info = new FileInfo(FilePath);

            _stampTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
            _stampLength = info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            _stampTicks = 0;
            _stampLength = 0;
        }
    }

    /// <summary>
    /// 파일이 우리가 마지막으로 남긴 모습과 다른지. 다르면 다른 PC가 바꿔 놓은 것이다.
    /// 저장할 것이 밀려 있으면 보지 않는다 — 곧 우리가 덮어쓸 참이라 비교가 의미 없다.
    /// </summary>
    public bool ChangedOutside()
    {
        lock (_gate)
        {
            if (_pending is not null) return false;
        }

        if (WaitingForFolder) return false;

        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) return false;

            return info.LastWriteTimeUtc.Ticks != _stampTicks || info.Length != _stampLength;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 다른 PC가 올려 놓은 내용을 읽어 온다. 읽기만 하고 <b>쓰지 않는다</b> —
    /// 여기서 되쓰면 저쪽이 그것을 또 남의 변경으로 보고, 둘이 끝없이 주고받는다.
    /// </summary>
    public AppData? ReadOutside()
    {
        if (TryRead() is not { } found) return null;

        RememberStamp();
        return WithDeviceState(found);
    }

    /// <summary>지금 메모리에 있는 내용을 그대로 글자로 만든다. 덮어쓰기 전에 남겨 두는 데 쓴다.</summary>
    public static string Serialize(AppData data)
        => JsonSerializer.Serialize(data, AppJsonContext.Default.AppData);

    private static AppData? Deserialize(string json)
    {
        var data = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppData);
        if (data is null) return null;

        data.Routines ??= [];
        data.Tasks ??= [];
        data.History ??= [];
        data.Settings ??= new AppSettings();
        return data;
    }

    /// <summary>첫 실행에서 보여줄 예시. 지우고 본인 것으로 채우면 된다.</summary>
    private static AppData CreateSeed()
    {
        return new AppData
        {
            Routines =
            [
                new Routine { Title = "물 2L 마시기", Order = 0 },
                new Routine { Title = "스트레칭 10분", Order = 1 },
                new Routine
                {
                    Title = "하루 정산 기록",
                    Order = 2,
                    Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]
                }
            ]
        };
    }

    /// <summary>마지막 저장 실패. 정상일 때는 null.</summary>
    public Exception? LastError { get; private set; }

    private static void TryLog(Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(
                Path.Combine(Directory, "error.log"),
                $"{DateTime.Now:u}  {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 로그조차 못 쓰는 상황이면 더 할 수 있는 게 없다.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _saveTimer.Dispose();
        Flush();
    }
}
