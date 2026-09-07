using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Flow.Models;

namespace Flow.Services;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppData))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>
/// JSON 파일 하나로 모든 상태를 보관한다. DB 엔진도, 백그라운드 프로세스도 없다.
/// 저장은 임시 파일에 쓴 뒤 교체하는 방식이라 쓰기 도중 전원이 꺼져도 원본이 깨지지 않는다.
/// </summary>
public sealed class DataStore : IDisposable
{
    private const int SaveDebounceMs = 700;

    private readonly Lock _gate = new();
    private readonly Timer _saveTimer;
    private AppData? _pending;
    private bool _disposed;

    public DataStore()
    {
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>저장된 파일이 없어 예시 데이터로 시작했는지. 첫 실행에 사용법을 띄우는 데 쓴다.</summary>
    public bool StartedFresh { get; private set; }

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flow");

    public static string FilePath { get; } = Path.Combine(Directory, "data.json");

    private static string TempPath => FilePath + ".tmp";
    private static string BackupPath => FilePath + ".bak";

    public AppData Load()
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
            // 파일이 손상되었더라도 앱은 떠야 한다. 빈 데이터로 시작한다.
        }

        StartedFresh = true;
        return CreateSeed();
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

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(TempPath, JsonSerializer.Serialize(data, AppJsonContext.Default.AppData));

            if (File.Exists(FilePath)) File.Replace(TempPath, FilePath, BackupPath, true);
            else File.Move(TempPath, FilePath);
        }
        catch (Exception ex)
        {
            // 저장 실패가 앱을 죽여선 안 되지만, 흔적은 남긴다.
            LastError = ex;
            TryLog(ex);
        }
    }

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
