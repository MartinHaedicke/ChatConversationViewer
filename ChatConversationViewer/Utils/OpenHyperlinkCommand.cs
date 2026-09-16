using System;
using System.Diagnostics;
using System.Windows.Input;

namespace ChatConversationViewer.Utils;

/// <summary>Opens http(s) links from rendered markdown in the default browser.</summary>
public sealed class OpenHyperlinkCommand : ICommand
{
    public static OpenHyperlinkCommand Instance { get; } = new();

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (parameter is string url
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // ignore failures to launch a browser
            }
        }
    }
}
