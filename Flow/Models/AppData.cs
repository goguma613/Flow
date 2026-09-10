using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Flow.Models;

public enum Priority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>매일/요일별로 반복되는 루틴. 하루가 지나면 체크가 자동으로 풀린다.</summary>
public sealed class Routine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";

    /// <summary>비어 있으면 매일. 값이 있으면 해당 요일에만 표시된다.</summary>
    public List<DayOfWeek> Days { get; set; } = new();

    public bool DoneToday { get; set; }
    public int Streak { get; set; }
    public int BestStreak { get; set; }
    public DateOnly? LastDoneDate { get; set; }
    public int Order { get; set; }
    public bool Archived { get; set; }

    /// <summary>
    /// 알릴 시각(선택). "매일 오전 9시 약 먹기"의 09:00.
    /// 파서는 예전부터 이 시각을 읽어내고 있었지만 담을 곳이 없어 버리고 있었다.
    /// </summary>
    public TimeOnly? Time { get; set; }

    /// <summary>
    /// 그 시각에 알릴지. Time 과 따로 두는 이유는 "알림만 끄고 시각은 남기기"가 되어야 하기 때문이다.
    /// 기본값이 false 라서, 업데이트 전에 만들어진 항목은 저절로 조용하다 — 마이그레이션이 필요 없다.
    /// </summary>
    public bool Remind { get; set; }

    /// <summary>
    /// 몫을 다한 논리적 날짜. '울렸다'가 아니라 '처리가 끝났다'는 뜻이다.
    /// 유예를 넘겨 건너뛴 것도 여기 적어 다음 틱에서 또 걸리지 않게 한다.
    /// 하루에 한 칸만 덮어쓰므로 기록이 자라지 않는다.
    ///
    /// 이 표시는 data.json 에 쓰지 않는다. 따라다니면 한 PC에서 울린 것을
    /// 다른 PC가 물려받아 안 울린다. 기기별로 세야 각자에서 다 울린다.
    /// </summary>
    [JsonIgnore] public DateOnly? RemindHandled { get; set; }

    /// <summary>
    /// 미뤄 둔 시각. 앱이 죽어도 살아남아야 해서 파일에 남긴다 —
    /// "10분 뒤"가 앱과 함께 사라지면 그 약을 안 먹게 된다.
    /// 벽시계가 아니라 '그 순간'이 기준이라 절대 시각으로 둔다.
    /// 이것도 RemindHandled 와 같은 이유로 기기별이다.
    /// </summary>
    [JsonIgnore] public DateTime? RemindSnoozedUntil { get; set; }

    /// <summary>오늘 체크하기 직전의 스트릭 값. 같은 날 체크 해제를 정확히 되돌리기 위해 보관한다.</summary>
    public int StreakSnapshot { get; set; }

    /// <summary>오늘 체크하기 직전의 마지막 완료일.</summary>
    public DateOnly? LastDoneSnapshot { get; set; }

    [JsonIgnore]
    public bool IsEveryDay => Days.Count == 0;
}

/// <summary>일회성 할 일.</summary>
public sealed class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public Priority Priority { get; set; } = Priority.None;

    /// <summary>마감일. null이면 언젠가 할 일(예정 없음).</summary>
    public DateOnly? Due { get; set; }

    /// <summary>마감 시각(선택). Due가 있을 때만 의미가 있다.</summary>
    public TimeOnly? DueTime { get; set; }

    /// <summary>미완료로 다음 날로 넘어간 횟수.</summary>
    public int CarryOverCount { get; set; }

    /// <summary>
    /// DueTime 에 알릴지. 시각을 여기 다시 적지 않는 이유는 두 벌이 어긋날 수 있기 때문이다.
    /// 기본값 false 라서 업데이트 전에 만들어진 항목은 저절로 조용하다.
    /// </summary>
    public bool Remind { get; set; }

    /// <summary>몫을 다한 논리적 날짜. Routine.RemindHandled 와 같은 뜻.</summary>
    [JsonIgnore] public DateOnly? RemindHandled { get; set; }

    /// <summary>미뤄 둔 시각. Routine.RemindSnoozedUntil 과 같은 뜻.</summary>
    [JsonIgnore] public DateTime? RemindSnoozedUntil { get; set; }

    public DateOnly CreatedDate { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public int Order { get; set; }
}

/// <summary>하루치 결산 기록. 스트릭과 주간 히트맵의 원본 데이터.</summary>
public sealed class DayRecord
{
    public DateOnly Date { get; set; }
    public int RoutinesScheduled { get; set; }
    public int RoutinesDone { get; set; }
    public int TasksDone { get; set; }
}

public sealed class AppSettings
{
    /// <summary>하루가 시작되는 시각(0-23). 기본 4시 — 새벽 작업을 전날로 집계한다.</summary>
    public int DayStartHour { get; set; } = 4;

    /// <summary>미완료 할 일을 다음 날로 자동 이월할지 여부.</summary>
    public bool CarryOverIncomplete { get; set; } = true;

    public bool AlwaysOnTop { get; set; } = true;

    // ── 아래 값들은 이 PC 것이라 data.json 에 쓰지 않는다.
    //    데이터 폴더를 클라우드에 두면 이 파일이 PC 사이를 오가는데,
    //    창 위치가 따라가면 다른 모니터에서 화면 밖으로 나가고,
    //    자동 시작은 그 PC 레지스트리에 거는 것이라 옮길 수 없다.
    //    실제 보관은 DeviceStore 가 %APPDATA% 에 맡는다.

    /// <summary>머리말·탭·입력칸을 마우스가 올라올 때만 보여주는 모드. 큰 모니터와 노트북에서 다르다.</summary>
    [JsonIgnore] public bool CompactMode { get; set; }

    /// <summary>알림 전체 스위치. 끄면 엔진이 아무것도 내놓지 않고 기록도 남기지 않는다.</summary>
    public bool RemindersEnabled { get; set; } = true;

    /// <summary>
    /// 창이 안 보일 때 Windows 알림에 소리를 낼지.
    /// 창이 보일 때는 눈으로 알 수 있으니 소리를 내지 않는다.
    /// </summary>
    public bool ReminderSound { get; set; } = true;

    /// <summary>
    /// 예정 시각을 지나서도 알릴 수 있는 한계(분).
    /// 자거나 꺼 둔 사이 지나간 것을 아침에 몰아서 띄우지 않으려는 장치다. 0이면 정시에만.
    /// </summary>
    public int MissedGraceMinutes { get; set; } = 60;

    /// <summary>'나중에'를 눌렀을 때 미룰 시간(분).</summary>
    public int SnoozeMinutes { get; set; } = 10;

    /// <summary>마우스가 벗어났을 때의 창 불투명도(0.3-1.0).</summary>
    public double IdleOpacity { get; set; } = 0.92;

    [JsonIgnore] public bool RunAtStartup { get; set; }
    public bool AcrylicEnabled { get; set; } = true;

    /// <summary>완료된 할 일을 며칠 뒤에 정리할지.</summary>
    public int CompletedRetentionDays { get; set; } = 7;

    /// <summary>GitHub 릴리스에서 새 버전을 하루 한 번 확인할지.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>하루에 한 번, 그리고 업데이트 직전에 자동으로 복사본을 남길지.</summary>
    public bool AutoBackup { get; set; } = true;

    /// <summary>마지막으로 자동 백업한 날. 하루 한 번만 만들기 위해 본다.</summary>
    [JsonIgnore] public DateOnly? LastBackupDate { get; set; }

    /// <summary>마지막으로 업데이트를 확인한 시각.</summary>
    [JsonIgnore] public DateTime? LastUpdateCheck { get; set; }

    /// <summary>저장된 창 위치. null이면 화면 오른쪽 위에 배치한다. (NaN은 JSON으로 쓸 수 없어 nullable을 쓴다)</summary>
    [JsonIgnore] public double? WindowLeft { get; set; }
    [JsonIgnore] public double? WindowTop { get; set; }

    /// <summary>사용자가 직접 크기를 조절했는지. 그 전까지는 내용에 맞춰 높이가 자동으로 정해진다.</summary>
    [JsonIgnore] public bool WindowSizedByUser { get; set; }

    [JsonIgnore] public double WindowWidth { get; set; } = 340;
    [JsonIgnore] public double WindowHeight { get; set; } = 620;
}

public sealed class AppData
{
    /// <summary>
    /// 2 = 알림 추가. 읽는 쪽은 이 값을 조건으로 쓰지 않는다 — 없는 필드는 기본값으로 채워지면 그만이다.
    /// 주의: 기존 enum(Priority, Days)에 값을 추가하면 구버전 앱이 파일 전체를 못 읽고
    /// 씨앗 데이터로 시작해 사용자 기록을 덮어쓴다. 새 갈래가 필요하면 enum 대신 필드를 늘린다.
    /// </summary>
    public int Version { get; set; } = 2;

    /// <summary>마지막으로 정산이 끝난 논리적 날짜. 롤오버 판단의 기준점.</summary>
    public DateOnly LastLogicalDate { get; set; }

    public List<Routine> Routines { get; set; } = new();
    public List<TaskItem> Tasks { get; set; } = new();
    public List<DayRecord> History { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}
