using System;
using System.Collections.Generic;

namespace Flow.Models;

/// <summary>이 PC에서 그 알림이 어떻게 되었는지. 기기마다 따로 센다.</summary>
public sealed class ReminderState
{
    /// <summary>주인(루틴 또는 할 일)의 Id.</summary>
    public Guid Id { get; set; }

    public DateOnly? Handled { get; set; }
    public DateTime? SnoozedUntil { get; set; }
}

/// <summary>
/// 이 PC에만 해당하는 것들. 동기화 폴더 밖(%APPDATA%)에 따로 저장한다.
///
/// 왜 갈랐는가:
/// - 창 위치·크기를 다른 PC로 옮기면 모니터가 달라 창이 화면 밖으로 나간다.
/// - 알림이 '울렸음' 표시까지 따라가면, 집에서 울린 것을 회사 PC가 물려받아 안 울린다.
///   각 PC에서 다 울리려면 이 표시만은 기기별이어야 한다.
/// - 부팅 시 자동 시작은 그 PC 레지스트리에 거는 것이라 옮길 수 없다.
/// - 마지막 업데이트 확인·백업 시각도 그 PC 사정이다.
/// </summary>
public sealed class DeviceState
{
    /// <summary>저장된 창 위치. null이면 화면 오른쪽 위에 배치한다.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    /// <summary>사용자가 직접 크기를 조절했는지.</summary>
    public bool WindowSizedByUser { get; set; }

    public double WindowWidth { get; set; } = 340;
    public double WindowHeight { get; set; } = 620;

    /// <summary>큰 모니터와 노트북에서 원하는 바가 다르므로 기기별로 둔다.</summary>
    public bool CompactMode { get; set; }

    public bool RunAtStartup { get; set; }

    public DateOnly? LastBackupDate { get; set; }
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>
    /// 이 저장 위치에서 데이터를 성공적으로 읽거나 만든 적이 있는지.
    ///
    /// 클라우드 폴더(예: 구글 드라이브 G:)는 그 프로그램이 떠 있어야만 존재한다.
    /// 부팅 직후 Flow 가 먼저 뜨면 폴더가 아직 없는데, 그것을 '첫 실행'으로 오해하면
    /// 예시 데이터로 시작했다가 잠시 뒤 드라이브가 붙는 순간 진짜 데이터를 덮어쓴다.
    /// 이 표시가 있으면 "없는 게 아니라 아직 안 온 것"으로 보고 기다린다.
    /// </summary>
    public bool DataFolderInitialized { get; set; }

    /// <summary>이 PC에서 각 알림이 어떻게 되었는지. 주인이 사라지면 함께 지운다.</summary>
    public List<ReminderState> Reminders { get; set; } = new();
}
