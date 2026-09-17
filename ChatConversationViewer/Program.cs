using System;
using System.Diagnostics;
using Avalonia;

namespace ChatConversationViewer;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // WebKitGTK fails to create its DMABUF render buffers ("Failed to create GBM
        // buffer") on systems without a usable GBM device (VMs, some drivers) — the
        // NativeWebView detail view then shows only a gray area. The documented
        // workaround WEBKIT_DISABLE_DMABUF_RENDERER=1 must be present at process start:
        // WebKit's helper processes snapshot the environment before Main runs, so setting
        // it in-process is too late. Re-exec ourselves once with it set; CPU rendering is
        // fine for this read-only viewer. An explicit export of the variable wins.
        if (OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("CCV_WEBKIT_WORKAROUND") is null
            && Environment.GetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER") is null)
        {
            try
            {
                if (Environment.ProcessPath is { } appPath)
                {
                    var psi = new ProcessStartInfo(appPath) { UseShellExecute = false };
                    foreach (var arg in args)
                        psi.ArgumentList.Add(arg);
                    psi.EnvironmentVariables["CCV_WEBKIT_WORKAROUND"] = "1";
                    psi.EnvironmentVariables["WEBKIT_DISABLE_DMABUF_RENDERER"] = "1";
                    Process.Start(psi);
                    return;
                }
            }
            catch
            {
                // fall through and start without the workaround
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Windows 11 25H2 (build 26200) fails to create layered child windows
            // (WS_EX_LAYERED), which WinUIComposition-mode windows use for the
            // NativeControlHost holder — NativeWebView then dies on attach with
            // "Unable to create child window for native control host". RedirectionSurface
            // windows use a non-layered holder instead. No transparent/acrylic effects
            // are needed here. No effect on Linux (Win32-only option).
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[] { Win32CompositionMode.RedirectionSurface }
            })
            .WithInterFont()
            .LogToTrace();
}
