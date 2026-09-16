using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ChatConversationViewer.ViewModels;

namespace ChatConversationViewer.Converters;

/// <summary>Maps a conversation source to its mark color.</summary>
public sealed class SourceBrushConverter : IValueConverter
{
    public static SourceBrushConverter Instance { get; } = new();

    private static readonly IBrush Claude = new SolidColorBrush(Color.Parse("#D97757"));
    private static readonly IBrush OpenCode = new SolidColorBrush(Color.Parse("#14B8A6"));
    private static readonly IBrush CopilotChat = new SolidColorBrush(Color.Parse("#8250DF"));
    private static readonly IBrush CopilotCli = new SolidColorBrush(Color.Parse("#24292F"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            ConversationSource.Claude => Claude,
            ConversationSource.OpenCode => OpenCode,
            ConversationSource.CopilotChat => CopilotChat,
            ConversationSource.CopilotCli => CopilotCli,
            _ => Brushes.Gray,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
