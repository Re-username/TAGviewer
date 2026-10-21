using System.Text.Json;

namespace PlcTagViewer.Launcher;

/// <summary>
/// Файловый мост для WebView2 через WebMessage (postMessage / WebMessageReceived).
/// WebView2 не поддерживает File System Access API (showOpenFilePicker, хэндлы),
/// а host object (COM) в self-contained сборке ненадёжен — поэтому выбор/чтение
/// файлов и отслеживание изменений идут сообщениями, с полными путями (включая сетевые диски).
///
/// Протокол: JS шлёт { id, type, payload }, ответ — { id, payload } (JSON-строка).
/// type: pickFiles | readFile(path) | getLastWrite(path)
/// </summary>
public static class FileBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    /// <summary>Диалог выбора файлов → JSON-массив [{id, path, name, size, modified, bytes(base64)}]. "[]" — отмена.
    /// preset — имя файла для подстановки (обновление файла без привязанного пути: пользователю достаточно Enter).</summary>
    public static string PickFiles(string? preset = null)
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = preset == null ? "Выбор файлов тегов" : "Укажите файл для обновления",
            Filter = "Файлы тегов (*.xml;*.export;*.txt;*.csv;*.st)|*.xml;*.export;*.txt;*.csv;*.st|Все файлы (*.*)|*.*",
            FileName = preset ?? ""
        };
        if (dlg.ShowDialog() != DialogResult.OK) return "[]";
        var list = new List<Dictionary<string, object>>();
        foreach (var path in dlg.FileNames)
        {
            var o = ReadFileObj(path);
            if (o != null) list.Add(o);
        }
        return JsonSerializer.Serialize(list, JsonOpts);
    }

    /// <summary>Прочитать файл по полному пути → JSON-объект или null при ошибке.</summary>
    public static string? ReadFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var o = ReadFileObj(path);
        return o == null ? null : JsonSerializer.Serialize(o, JsonOpts);
    }

    /// <summary>Время последней записи файла (тики UTC / 10000 — «миллисекунды» нашего формата). -1 — недоступен.</summary>
    public static long GetLastWrite(string? path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        try { return File.GetLastWriteTimeUtc(path).Ticks / 10000L; }
        catch { return -1; }
    }

    private static Dictionary<string, object>? ReadFileObj(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var fi = new FileInfo(path);
            return new Dictionary<string, object>
            {
                ["id"] = path,
                ["path"] = path,
                ["name"] = fi.Name,
                ["size"] = fi.Length,
                ["modified"] = fi.LastWriteTimeUtc.Ticks / 10000.0,
                ["bytes"] = Convert.ToBase64String(bytes)
            };
        }
        catch { return null; }
    }
}
