using System.Runtime.InteropServices;

namespace PlcTagViewer.Launcher;

/// <summary>
/// Главное (и единственное) окно лаунчера.
/// Если есть WebView2 — показывает приложение в своём окне.
/// Иначе — открывает браузер и показывает маленькую панель управления.
/// </summary>
public sealed class MainForm : Form
{
    // Тёмный заголовок окна (Windows 10 1809+)
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private Microsoft.Web.WebView2.WinForms.WebView2 _webView;
    private Panel _fallbackPanel;

    public MainForm()
    {
        Text = "Просмотрщик тегов ПЛК";
        Width = 1440;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 33, 40);
        try
        {
            // Иконка из ресурсов самого exe
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        }
        catch { }

        Load += OnLoad;
    }

    private async void OnLoad(object sender, EventArgs e)
    {
        SetDarkTitleBar();

        if (Program.WebView2Available(out _))
        {
            try
            {
                await ShowWebView();
                return;
            }
            catch { /* не получилось — уходим в браузерный режим */ }
        }
        ShowFallback();
    }

    private void SetDarkTitleBar()
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        }
        catch { }
    }

    /// <summary>Общая папка профиля WebView2 — все окна (включая выносные) делят localStorage.</summary>
    public static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlcTagViewer", "WebView2Data");

    private async Task ShowWebView()
    {
        _webView = new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = DockStyle.Fill,
        };
        Controls.Add(_webView);

        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
            .CreateAsync(null, UserDataFolder);
        await _webView.EnsureCoreWebView2Async(env);

        // Мост файловой системы через WebMessage (см. FileBridge):
        // выбор/чтение файлов с полными путями, отслеживание изменений.
        _webView.CoreWebView2.WebMessageReceived += OnWebMessage;

        // Выносные окна — свои чистые формы без адресной строки
        _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

        _webView.CoreWebView2.Navigate(Program.Server.RootUrl);
    }

    private void OnNewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var win = new PopupForm(e.Uri);
        win.Show();
    }

    /// <summary>Ответы на сообщения из web-части (файловый мост).</summary>
    private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString();
            var type = root.GetProperty("type").GetString();
            var payload = root.TryGetProperty("payload", out var pl) && pl.ValueKind == System.Text.Json.JsonValueKind.String
                ? pl.GetString() : null;

            string? answer = type switch
            {
                "pickFiles" => FileBridge.PickFiles(string.IsNullOrEmpty(payload) ? null : payload),
                "readFile" => FileBridge.ReadFile(payload),
                "getLastWrite" => FileBridge.GetLastWrite(payload).ToString(),
                _ => null
            };
            Respond(id, answer);
        }
        catch
        {
            Respond(id, null);
        }
    }

    private void Respond(string? id, string? payload)
    {
        if (id == null || _webView?.CoreWebView2 == null) return;
        var json = payload == null
            ? $"{{\"id\":{System.Text.Json.JsonSerializer.Serialize(id)},\"payload\":null}}"
            : $"{{\"id\":{System.Text.Json.JsonSerializer.Serialize(id)},\"payload\":{System.Text.Json.JsonSerializer.Serialize(payload)}}}";
        try { _webView.CoreWebView2.PostWebMessageAsJson(json); } catch { }
    }

    private void ShowFallback()
    {
        Program.OpenInBrowser(Program.Server.RootUrl);

        _fallbackPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 33, 40) };
        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 120,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 11f),
            Text = "Приложение открыто в браузере.\r\n" +
                   "Не закрывайте это окно, пока работаете с программой.\r\n" +
                   Program.Server.RootUrl
        };
        var btnOpen = new Button
        {
            Text = "Открыть снова",
            Width = 160,
            Height = 36,
            Left = 40,
            Top = 140,
        };
        btnOpen.Click += (_, _) => Program.OpenInBrowser(Program.Server.RootUrl);
        var btnExit = new Button
        {
            Text = "Выход",
            Width = 160,
            Height = 36,
            Left = 220,
            Top = 140,
        };
        btnExit.Click += (_, _) => Close();

        _fallbackPanel.Controls.Add(label);
        _fallbackPanel.Controls.Add(btnOpen);
        _fallbackPanel.Controls.Add(btnExit);
        Controls.Add(_fallbackPanel);

        Width = 460;
        Height = 260;
        CenterToScreen();
    }
}
