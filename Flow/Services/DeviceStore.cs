using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 이 PC에만 해당하는 상태를 동기화 폴더 <b>밖</b>에 보관한다.
///
/// 데이터 폴더를 구글 드라이브 같은 곳에 두면 data.json 은 PC 사이를 오간다.
/// 그런데 창 위치나 '알림 울렸음' 표시까지 따라다니면 곤란하다 —
/// 창은 다른 모니터에서 화면 밖으로 나가고, 알림은 한 PC에서만 울린다.
/// 그래서 그것들만 %APPDATA% 에 남긴다.
///
/// 파일 이름에 데이터 폴더를 섞는 이유: 같은 PC에서 실제 사용본과 시험본을 함께 돌려도
/// 서로의 창 위치와 알림 상태를 건드리지 않아야 한다. 둘은 남남이다.
/// </summary>
public static class DeviceStore
{
    private static readonly string Path_ = Resolve();

    private static bool _dataFolderInitialized;

    public static string FilePath => Path_;

    /// <summary>이 저장 위치에서 예전에 데이터를 읽은 적이 있는지.</summary>
    public static bool DataFolderInitialized => _dataFolderInitialized;

    /// <summary>데이터를 성공적으로 읽었거나 새로 만들었다. 다음부터는 없으면 기다린다.</summary>
    public static void MarkDataFolderReady() => _dataFolderInitialized = true;

    /// <summary>
    /// 이 PC의 상태를 읽어 데이터에 입힌다.
    /// 파일이 아직 없으면, 예전 data.json 안에 있던 값을 한 번만 옮겨 온다.
    /// </summary>
    public static void Apply(AppData data)
    {
        var state = Load() ?? Migrate(data);
        _dataFolderInitialized |= state.DataFolderInitialized;

        data.Settings.WindowLeft = state.WindowLeft;
        data.Settings.WindowTop = state.WindowTop;
        data.Settings.WindowSizedByUser = state.WindowSizedByUser;
        data.Settings.WindowWidth = state.WindowWidth;
        data.Settings.WindowHeight = state.WindowHeight;
        data.Settings.CompactMode = state.CompactMode;
        data.Settings.RunAtStartup = state.RunAtStartup;
        data.Settings.HotKey = state.HotKey ?? "";
        data.Settings.LastBackupDate = state.LastBackupDate;
        data.Settings.LastUpdateCheck = state.LastUpdateCheck;

        if (state.Reminders.Count == 0) return;

        var byId = new Dictionary<Guid, ReminderState>(state.Reminders.Count);
        foreach (var entry in state.Reminders) byId[entry.Id] = entry;

        foreach (var routine in data.Routines)
        {
            if (!byId.TryGetValue(routine.Id, out var entry)) continue;

            routine.RemindHandled = entry.Handled;
            routine.RemindSnoozedUntil = entry.SnoozedUntil;
        }

        foreach (var task in data.Tasks)
        {
            if (!byId.TryGetValue(task.Id, out var entry)) continue;

            task.RemindHandled = entry.Handled;
            task.RemindSnoozedUntil = entry.SnoozedUntil;
        }
    }

    /// <summary>
    /// 데이터에서 이 PC 몫을 뽑아 저장한다. data.json 을 저장할 때마다 함께 불린다.
    /// 주인이 사라진 알림 상태는 여기서 저절로 빠진다 — 살아 있는 항목만 훑기 때문이다.
    /// </summary>
    public static void Capture(AppData data)
    {
        var state = new DeviceState
        {
            WindowLeft = data.Settings.WindowLeft,
            WindowTop = data.Settings.WindowTop,
            WindowSizedByUser = data.Settings.WindowSizedByUser,
            WindowWidth = data.Settings.WindowWidth,
            WindowHeight = data.Settings.WindowHeight,
            CompactMode = data.Settings.CompactMode,
            RunAtStartup = data.Settings.RunAtStartup,
            HotKey = data.Settings.HotKey,
            LastBackupDate = data.Settings.LastBackupDate,
            LastUpdateCheck = data.Settings.LastUpdateCheck,
            DataFolderInitialized = _dataFolderInitialized
        };

        foreach (var routine in data.Routines)
        {
            if (routine.RemindHandled is null && routine.RemindSnoozedUntil is null) continue;

            state.Reminders.Add(new ReminderState
            {
                Id = routine.Id,
                Handled = routine.RemindHandled,
                SnoozedUntil = routine.RemindSnoozedUntil
            });
        }

        foreach (var task in data.Tasks)
        {
            if (task.RemindHandled is null && task.RemindSnoozedUntil is null) continue;

            state.Reminders.Add(new ReminderState
            {
                Id = task.Id,
                Handled = task.RemindHandled,
                SnoozedUntil = task.RemindSnoozedUntil
            });
        }

        Save(state);
    }

    public static DeviceState? Load()
    {
        try
        {
            if (!File.Exists(Path_)) return null;

            return JsonSerializer.Deserialize(File.ReadAllText(Path_), AppJsonContext.Default.DeviceState);
        }
        catch (Exception)
        {
            // 이 파일이 깨져도 잃는 것은 창 위치와 알림 표시뿐이다. 기본값으로 간다.
            return null;
        }
    }

    private static void Save(DeviceState state)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path_);
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);

            var temporary = Path_ + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, AppJsonContext.Default.DeviceState));

            if (File.Exists(Path_)) File.Replace(temporary, Path_, null, true);
            else File.Move(temporary, Path_);
        }
        catch (Exception)
        {
            // 저장 실패가 앱을 멈춰서는 안 된다. 다음 저장 때 다시 시도한다.
        }
    }

    /// <summary>
    /// 이 기능이 생기기 전 data.json 안에 있던 값들을 한 번만 건져 온다.
    /// 업데이트하자마자 창이 엉뚱한 자리로 튀지 않게 하려는 것이다.
    /// </summary>
    private static DeviceState Migrate(AppData data)
    {
        var state = new DeviceState();

        try
        {
            if (!File.Exists(DataStore.FilePath)) return state;

            using var document = JsonDocument.Parse(File.ReadAllText(DataStore.FilePath));
            if (!document.RootElement.TryGetProperty("Settings", out var settings)) return state;

            if (settings.TryGetProperty("WindowLeft", out var left) && left.ValueKind == JsonValueKind.Number)
                state.WindowLeft = left.GetDouble();
            if (settings.TryGetProperty("WindowTop", out var top) && top.ValueKind == JsonValueKind.Number)
                state.WindowTop = top.GetDouble();
            if (settings.TryGetProperty("WindowWidth", out var width) && width.ValueKind == JsonValueKind.Number)
                state.WindowWidth = width.GetDouble();
            if (settings.TryGetProperty("WindowHeight", out var height) && height.ValueKind == JsonValueKind.Number)
                state.WindowHeight = height.GetDouble();
            if (settings.TryGetProperty("WindowSizedByUser", out var sized))
                state.WindowSizedByUser = sized.ValueKind == JsonValueKind.True;
            if (settings.TryGetProperty("CompactMode", out var compact))
                state.CompactMode = compact.ValueKind == JsonValueKind.True;
            if (settings.TryGetProperty("RunAtStartup", out var startup))
                state.RunAtStartup = startup.ValueKind == JsonValueKind.True;
            if (settings.TryGetProperty("LastBackupDate", out var backup)
                && backup.ValueKind == JsonValueKind.String
                && DateOnly.TryParse(backup.GetString(), out var backupDate))
                state.LastBackupDate = backupDate;
            if (settings.TryGetProperty("LastUpdateCheck", out var check)
                && check.ValueKind == JsonValueKind.String
                && DateTime.TryParse(check.GetString(), out var checkedAt))
                state.LastUpdateCheck = checkedAt;
        }
        catch (Exception)
        {
            // 못 건져도 기본값으로 시작하면 그만이다.
        }

        // 알림 상태는 옮기지 않는다. 옮겨봐야 한 번 더 울리거나 덜 울리는 차이뿐이고,
        // 옛 파일의 그 값은 '어느 PC에서' 울렸는지를 모르기 때문이다.
        return state;
    }

    private static string Resolve()
    {
        var home = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flow");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(DataStore.Directory.ToLowerInvariant()));

        return System.IO.Path.Combine(home, $"device-{Convert.ToHexString(hash, 0, 6)}.json");
    }
}
