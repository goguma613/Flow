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

    /// <summary>머리말·탭·입력칸을 마우스가 올라올 때만 보여주는 모드.</summary>
    public bool CompactMode { get; set; }

    /// <summary>마우스가 벗어났을 때의 창 불투명도(0.3-1.0).</summary>
    public double IdleOpacity { get; set; } = 0.92;

    public bool RunAtStartup { get; set; }
    public bool AcrylicEnabled { get; set; } = true;

    /// <summary>완료된 할 일을 며칠 뒤에 정리할지.</summary>
    public int CompletedRetentionDays { get; set; } = 7;

    /// <summary>GitHub 릴리스에서 새 버전을 하루 한 번 확인할지.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>하루에 한 번, 그리고 업데이트 직전에 자동으로 복사본을 남길지.</summary>
    public bool AutoBackup { get; set; } = true;

    /// <summary>마지막으로 자동 백업한 날. 하루 한 번만 만들기 위해 본다.</summary>
    public DateOnly? LastBackupDate { get; set; }

    /// <summary>마지막으로 업데이트를 확인한 시각.</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>저장된 창 위치. null이면 화면 오른쪽 위에 배치한다. (NaN은 JSON으로 쓸 수 없어 nullable을 쓴다)</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    /// <summary>사용자가 직접 크기를 조절했는지. 그 전까지는 내용에 맞춰 높이가 자동으로 정해진다.</summary>
    public bool WindowSizedByUser { get; set; }

    public double WindowWidth { get; set; } = 340;
    public double WindowHeight { get; set; } = 620;
}

public sealed class AppData
{
    public int Version { get; set; } = 1;

    /// <summary>마지막으로 정산이 끝난 논리적 날짜. 롤오버 판단의 기준점.</summary>
    public DateOnly LastLogicalDate { get; set; }

    public List<Routine> Routines { get; set; } = new();
    public List<TaskItem> Tasks { get; set; } = new();
    public List<DayRecord> History { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}
