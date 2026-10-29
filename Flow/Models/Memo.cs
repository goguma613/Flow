using System;
using System.Collections.Generic;

namespace Flow.Models;

// 메모 파일 하나를 읽은 모양. 순수 데이터라 UI 패키지를 참조하지 않는다.

/// <summary>
/// 파일 하나. 머리말(id·pinned·daily·reset·deleted·tags)과 본문으로 나뉜다.
/// HasHeader 가 false 면 머리말이 없거나 깨진 파일이고, Id 는 임시 값이다.
/// Tags 는 머리말 tags 의 이름들(# 없이, null 이면 없음). 본문 #태그 와 합친 것은 MemoFormat.TagsOfDoc.
/// Extra 는 모르는 키를 순서대로 보존한 것이다(새 버전이 쓴 키를 지우지 않으려고).
/// </summary>
public sealed record MemoDocument(Guid Id, bool HasHeader, bool Pinned, bool Daily,
    DateOnly? Reset, DateOnly? Deleted, IReadOnlyList<KeyValuePair<string, string>> Extra, string Body,
    IReadOnlyList<string>? Tags = null);

public enum MemoLineKind { Blank, Text, Heading1, Heading2, Numbered, Bullet, Check }

/// <summary>본문 한 줄의 분류. Marker 는 번호 단계의 "3." 이고 그 밖은 null, Text 는 기호를 뗀 글이다.</summary>
public sealed record MemoLine(int Index, MemoLineKind Kind, string Text, string? Marker, bool Checked);
