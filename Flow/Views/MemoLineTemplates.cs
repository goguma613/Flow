using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>
/// 읽기 모드에서 줄 종류(MemoLineKind 이름)마다 다른 DataTemplate 을 고른다.
/// 맞는 것이 없으면 "Text" 모양으로 그린다.
/// </summary>
public sealed class MemoLineTemplates : IDataTemplate
{
    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = new();

    public Control? Build(object? param)
    {
        if (param is not MemoLineView line) return null;
        if (Templates.TryGetValue(line.Kind.ToString(), out var template)) return template.Build(param);
        return Templates.TryGetValue("Text", out var text) ? text.Build(param) : new TextBlock { Text = line.Text };
    }

    public bool Match(object? data) => data is MemoLineView;
}
