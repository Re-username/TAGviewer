using System.Runtime.InteropServices;

namespace PlcTagViewer.Launcher;

/// <summary>
/// Выносное окно (вкладка/файл) — чистая форма с WebView2, без адресной строки.
/// Делит профиль WebView2 с главным окном, поэтому localStorage общий
/// и синхронизация с основным окном работает.
/// </summary>
public sealed class PopupForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private Microsoft.Web.WebView2.WinForms.WebView2 _webView;
    private readonly string _url;

    public PopupForm(string url)
    {
        _url = url;
        Text = "Просмотрщик тегов ПЛК";
        Width = 1000;
        Height = 750;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        }
        catch { }

        _webView = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
            .CreateAsync(null, MainForm.UserDataFolder);
        await _webView.EnsureCoreWebView2Async(env);

        // вложенные выносы (⧉ из выносного окна) — тоже чистые формы
        _webView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            new PopupForm(args.Uri).Show();
        };
        _webView.CoreWebView2.DocumentTitleChanged += (_, _) =>
        {
            var t = _webView.CoreWebView2.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(t)) Text = t;
        };

        _webView.CoreWebView2.Navigate(_url);
    }
}
