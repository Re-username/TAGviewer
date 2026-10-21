using System.Diagnostics;

namespace PlcTagViewer.Launcher;

internal static class Program
{
    public static StaticServer Server;

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Порт 17893: стабильный origin (127.0.0.1:17893) — WebView2 и браузер
        // запоминают разрешения доступа к файлам между запусками.
        Server = new StaticServer(17893);
        Server.Start();

        Application.Run(new MainForm());
        Server.Dispose();
    }

    /// <summary>Открыть URL в браузере по умолчанию.</summary>
    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Доступен ли рантайм Edge WebView2.</summary>
    public static bool WebView2Available(out string version)
    {
        version = null;
        try
        {
            version = Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch { return false; }
    }
}
