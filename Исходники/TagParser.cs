using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlcTagViewer
{
    /// <summary>Узел дерева тегов.</summary>
    /// <summary>Вид узла дерева.</summary>
    public enum NodeKind { Variable, Struct, Array, ArrayItem }

    public sealed class TagNode
    {
        public string Name = "";
        public NodeKind Kind = NodeKind.Variable;
        /// <summary>Количество элементов (для массива/структуры), 0 — не применимо.</summary>
        public int ElementCount;
        public string FullPath = "";
        public string TypeName = "";
        public string Address = "";
        public string Comment = "";
        public readonly List<TagNode> Children = new();

        public bool IsLeaf => Children.Count == 0;

        public int CountLeaves()
        {
            if (Children.Count == 0) return 1;
            int n = 0;
            foreach (var c in Children) n += c.CountLeaves();
            return n;
        }

        public IEnumerable<TagNode> All()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var d in c.All())
                    yield return d;
        }
    }

    /// <summary>Источник = один загруженный файл.</summary>
    public sealed class TagSource
    {
        public string FileName = "";
        /// <summary>Полный путь к файлу (в exe-режиме; в браузере может быть недоступен — null).</summary>
        public string? FilePath;
        public string Vendor = "";
        /// <summary>ID хэндла File System Access API (null — файл без хэндла, обновление через повторный выбор).</summary>
        public string HandleId = null;
        /// <summary>lastModified файла в момент загрузки (мс Unix).</summary>
        public long LastModified;
        /// <summary>Когда файл был загружен/обновлён в приложении.</summary>
        public DateTime LoadedAt = DateTime.Now;
        /// <summary>Файл на диске изменился с момента загрузки.</summary>
        public bool ChangedOnDisk;
        /// <summary>Полные пути тегов, появившиеся при последнем обновлении.</summary>
        public readonly HashSet<string> NewPaths = new();
        public readonly List<TagNode> Roots = new();

        public int CountLeaves()
        {
            int n = 0;
            foreach (var r in Roots) n += r.CountLeaves();
            return n;
        }
    }

    /// <summary>
    /// Парсеры экспорта переменных:
    ///  - CODESYS Symbol XML (SymbolConfiguration)
    ///  - PLCopen XML (globalVars / variable)
    ///  - ST-декларации (VAR ... name : TYPE; ... END_VAR)
    ///  - SYSMAC Studio / CX-Designer таблицы (TXT/CSV, разделитель ; , tab)
    /// </summary>
    public static class TagParser
    {
        // Канонические IEC-типы (для определения, что столбец — это тип)
        private static readonly Regex IecType = new Regex(
            @"^(?:BOOL|S?INT|U?SINT|U?INT|U?DINT|U?LINT|DINT|LINT|BYTE|WORD|DWORD|LWORD|L?REAL|W?STRING(?:\s*[\[(]\s*\d+\s*[\])])?|TIME|LTIME|DATE|LDATE|D|TOD|TIME_OF_DAY|DT|DATE_AND_TIME|LTOD|LDT|CHAR|WCHAR|SAFEBOOL|ENUM|ARRAY\b.*|.*STRUCT.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static TagSource ParseFile(string path)
        {
            return ParseBytes(File.ReadAllBytes(path), Path.GetFileName(path));
        }

        /// <summary>Разбор из массива байт (для браузерной версии, где нет файлового пути).</summary>
        public static TagSource ParseBytes(byte[] bytes, string fileName)
        {
            string raw = Decode(bytes);
            string trimmed = raw.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            string ext = Path.GetExtension(fileName).ToLowerInvariant();

            TagSource src;
            if (ext == ".xml" || trimmed.StartsWith("<"))
                src = ParseXml(trimmed);
            else if (LooksLikeSt(trimmed))
                src = ParseSt(trimmed);
            else
                src = ParseTable(trimmed);

            src.FileName = fileName;
            if (src.Roots.Count == 0)
                throw new Exception("В файле не найдено ни одной переменной.\nПоддерживаются: CODESYS Symbol XML, PLCopen XML, ST-декларации, таблицы SYSMAC Studio / CX-Designer (TXT/CSV).");
            return src;
        }

        // ---------- определение формата ----------

        private static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes);
            // UTF-8 (с BOM или без): пробуем строго, при крахе — cp1251 (русские экспорты SYSMAC)
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch { return Encoding.GetEncoding(1251).GetString(bytes); }
        }

        private static bool LooksLikeSt(string text)
        {
            return Regex.IsMatch(text, @"\bVAR(_GLOBAL|_INPUT|_OUTPUT|_IN_OUT)?\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(text, @"^\s*[\w\.]+\s*:\s*\w+", RegexOptions.Multiline);
        }

        // ---------- XML ----------

        private static TagSource ParseXml(string xml)
        {
            var src = new TagSource();
            XDocument doc = XDocument.Parse(xml);
            string root = doc.Root.Name.LocalName;
            src.Vendor = root.IndexOf("SymbolConfiguration", StringComparison.OrdinalIgnoreCase) >= 0
                ? "CODESYS Symbol XML"
                : "PLCopen XML";

            // CODESYS V3.5 Symbol Configuration (.xml из "Symbol configuration..."):
            // TypeList (словарь типов) + NodeList (дерево узлов).
            if (root.Equals("Symbolconfiguration", StringComparison.OrdinalIgnoreCase)
                && doc.Descendants().Any(e => e.Name.LocalName == "NodeList"))
            {
                return ParseSymbolconfiguration(doc);
            }

            var flat = new List<(string name, string type, string addr, string comment)>();

            // CODESYS Symbol XML: <Symbol><Name>..</Name><Type>..</Type><AccessType/><Comment>..</Comment></Symbol>
            foreach (var s in doc.Descendants().Where(e => e.Name.LocalName == "Symbol"))
            {
                string name = ChildVal(s, "Name");
                if (string.IsNullOrEmpty(name)) continue;
                flat.Add((name, ChildVal(s, "Type"), ChildVal(s, "Address"), CollectComments(s)));
            }

            // PLCopen: <variable name=".." address=".."><type><BOOL/></type><documentation>...</documentation></variable>
            foreach (var v in doc.Descendants().Where(e => e.Name.LocalName == "variable"))
            {
                string name = (string)v.Attribute("name") ?? "";
                if (string.IsNullOrEmpty(name)) continue;
                string addr = (string)v.Attribute("address") ?? "";
                string type = "";
                var typeEl = v.Descendants().FirstOrDefault(e => e.Name.LocalName == "type");
                if (typeEl != null)
                {
                    var inner = typeEl.Elements().FirstOrDefault();
                    type = inner != null ? inner.Name.LocalName : "";
                    if (inner != null && inner.Name.LocalName == "array")
                    {
                        var dim = inner.Descendants().FirstOrDefault(e => e.Name.LocalName == "dimension");
                        var baseT = inner.Descendants().FirstOrDefault(e => e.Name.LocalName != "dimension" && e.Name.LocalName != "array");
                        if (dim != null) type = "ARRAY[" + (string)dim.Attribute("lower") + ".." + (string)dim.Attribute("upper") + "] OF " + (baseT?.Name.LocalName ?? "");
                    }
                }
                string comment = "";
                var d = v.Descendants().FirstOrDefault(e => e.Name.LocalName == "documentation");
                if (d != null)
                {
                    var xhtml = d.Descendants().FirstOrDefault(e => e.Name.LocalName == "xhtml");
                    comment = (xhtml != null ? xhtml.Value : d.Value).Trim();
                }
                flat.Add((name, type, addr, comment));
            }

            foreach (var t in flat)
                AddToTree(src.Roots, t.name, t.type, t.addr, t.comment);
            if (src.Roots.Count == 0)
            {
                src.Vendor = "XML (общий разбор)";
                GenericXmlScan(doc, src);
            }
            FinalizeKinds(src.Roots);
            return src;
        }

        /// <summary>
        /// Разбор CODESYS V3.5 Symbol Configuration:
        /// TypeList — словарь типов (Simple/Array/UserDef с полями),
        /// NodeList — дерево узлов. Структуры и массивы раскрываются по TypeList.
        /// </summary>
        private sealed class ScType
        {
            public string Kind = "Simple";      // Simple | Array | UserDef
            public string IecName = "";         // BOOL, ST_Analog_Input, ARRAY [0..18] OF ...
            public string BaseType = "";        // для Array — базовый тип; для UserDef — базовый тип (INT для enum и пр.)
            public long ArrayMin, ArrayMax;     // для Array
            public bool HasDims;
            public readonly List<(string name, string type)> Fields = new(); // UserDefElement
        }

        private static TagSource ParseSymbolconfiguration(XDocument doc)
        {
            var src = new TagSource { Vendor = "CODESYS Symbol XML (V3.5)" };

            // 1. Словарь типов
            var types = new Dictionary<string, ScType>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in doc.Descendants())
            {
                var ln = el.Name.LocalName;
                if (!ln.StartsWith("Type", StringComparison.Ordinal) || ln == "TypeList") continue;
                string tname = (string)el.Attribute("name");
                if (string.IsNullOrEmpty(tname)) continue;
                var t = new ScType { Kind = ln.Substring(4) };
                t.IecName = (string)el.Attribute("iecname") ?? tname;
                t.BaseType = (string)el.Attribute("basetype") ?? "";
                var dim = el.Elements().FirstOrDefault(e => e.Name.LocalName == "ArrayDim");
                if (dim != null
                    && long.TryParse((string)dim.Attribute("minrange") ?? "", out long mn)
                    && long.TryParse((string)dim.Attribute("maxrange") ?? "", out long mx))
                {
                    t.ArrayMin = mn; t.ArrayMax = mx; t.HasDims = true;
                }
                if (t.Kind == "UserDef")
                {
                    foreach (var f in el.Elements().Where(e => e.Name.LocalName == "UserDefElement"))
                    {
                        string fn = (string)f.Attribute("iecname");
                        string ft = (string)f.Attribute("type");
                        if (!string.IsNullOrEmpty(fn)) t.Fields.Add((fn, ft ?? ""));
                    }
                }
                types[tname] = t;
            }

            // 2. Дерево из NodeList
            foreach (var nl in doc.Descendants().Where(e => e.Name.LocalName == "NodeList"))
            {
                foreach (var node in nl.Elements().Where(e => e.Name.LocalName == "Node"))
                {
                    var n = BuildScNode(node, types, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (n != null) src.Roots.Add(n);
                }
            }

            FinalizeKinds(src.Roots);
            foreach (var r in src.Roots) SetFullPath(r, "");
            if (src.Roots.Count == 0) throw new Exception("В NodeList не найдено ни одного узла.");
            return src;
        }

        private static void SetFullPath(TagNode n, string prefix)
        {
            n.FullPath = prefix.Length == 0 ? n.Name
                : (n.Name.Length > 0 && n.Name[0] == '[' ? prefix + n.Name : prefix + "." + n.Name);
            foreach (var c in n.Children) SetFullPath(c, n.FullPath);
        }

        /// <summary>Строит узел дерева из &lt;Node&gt;, раскрывая типы структур и массивов.</summary>
        private static TagNode BuildScNode(XElement el, Dictionary<string, ScType> types, HashSet<string> expanding)
        {
            string name = (string)el.Attribute("name");
            if (string.IsNullOrWhiteSpace(name)) return null;
            string tname = (string)el.Attribute("type") ?? "";
            var node = new TagNode
            {
                Name = name.Trim(),
                TypeName = TypeDisplay(tname, types),
                Address = (string)el.Attribute("address") ?? "",
                Comment = CollectComments(el)
            };

            // Раскрытие сложных типов
            if (!string.IsNullOrEmpty(tname) && types.TryGetValue(tname, out var t))
            {
                if (t.Kind == "UserDef" && t.Fields.Count > 0 && expanding.Add(tname))
                {
                    foreach (var (fname, ftype) in t.Fields)
                        node.Children.Add(MakeField(fname, ftype, types, expanding));
                    expanding.Remove(tname);
                }
                else if (t.Kind == "Array" && t.HasDims)
                {
                    int cnt = (int)Math.Max(0, t.ArrayMax - t.ArrayMin + 1);
                    if (cnt > 0 && cnt <= 10000)
                    {
                        expanding.Add(tname);
                        for (long i = t.ArrayMin; i <= t.ArrayMax; i++)
                            node.Children.Add(MakeField("[" + i + "]", t.BaseType, types, expanding));
                        expanding.Remove(tname);
                    }
                }
            }
            // Вложенные Node (иерархия приложения)
            foreach (var child in el.Elements().Where(e => e.Name.LocalName == "Node"))
            {
                var c = BuildScNode(child, types, expanding);
                if (c != null) node.Children.Add(c);
            }
            return node;
        }

        /// <summary>Поле структуры / элемент массива с раскрытием вложенного типа.</summary>
        private static TagNode MakeField(string name, string typeName, Dictionary<string, ScType> types, HashSet<string> expanding)
        {
            var n = new TagNode
            {
                Name = name,
                TypeName = TypeDisplay(typeName, types),
                Address = "",
                Comment = ""
            };
            if (!string.IsNullOrEmpty(typeName) && types.TryGetValue(typeName, out var t))
            {
                if (t.Kind == "UserDef" && t.Fields.Count > 0 && expanding.Add(typeName))
                {
                    foreach (var (fname, ftype) in t.Fields)
                        n.Children.Add(MakeField(fname, ftype, types, expanding));
                    expanding.Remove(typeName);
                }
                else if (t.Kind == "Array" && t.HasDims)
                {
                    int cnt = (int)Math.Max(0, t.ArrayMax - t.ArrayMin + 1);
                    if (cnt > 0 && cnt <= 10000)
                    {
                        expanding.Add(typeName);
                        for (long i = t.ArrayMin; i <= t.ArrayMax; i++)
                            n.Children.Add(MakeField("[" + i + "]", t.BaseType, types, expanding));
                        expanding.Remove(typeName);
                    }
                }
            }
            return n;
        }

        /// <summary>Отображаемое имя типа: iecname вместо внутреннего T_*, для массивов — с диапазоном.</summary>
        private static string TypeDisplay(string tname, Dictionary<string, ScType> types)
        {
            if (string.IsNullOrEmpty(tname)) return "";
            if (types.TryGetValue(tname, out var t))
            {
                if (t.Kind == "Array" && t.HasDims)
                {
                    string b = TypeDisplay(t.BaseType, types);
                    return "ARRAY[" + t.ArrayMin + ".." + t.ArrayMax + "] OF " + b;
                }
                return t.IecName;
            }
            return tname;
        }

        /// <summary>Собирает комментарии на всех языках: Comment, Comment_ru, Comment-en и т.п.</summary>
        private static string CollectComments(XElement el)
        {
            var parts = new List<string>();
            foreach (var c in el.Elements())
            {
                var ln = c.Name.LocalName;
                if (!ln.StartsWith("Comment", StringComparison.OrdinalIgnoreCase)) continue;
                var text = c.Value.Trim();
                if (text.Length == 0) continue;
                string suffix = ln.Length > 7 ? ln.Substring(7).Trim('_', '-', ' ') : "";
                string labeled = suffix.Length > 0 ? suffix + ": " + text : text;
                if (!parts.Contains(labeled)) parts.Add(labeled);
            }
            return string.Join("\n", parts);
        }

        /// <summary>Проставляет вид узла и количество элементов по всему дереву.</summary>
        private static void FinalizeKinds(List<TagNode> nodes)
        {
            foreach (var n in nodes) FinalizeKinds(n);
        }

        private static void FinalizeKinds(TagNode n)
        {
            foreach (var c in n.Children) FinalizeKinds(c);
            bool isArrayItem = n.Name.Length > 2 && n.Name[0] == '[' && n.Name[n.Name.Length - 1] == ']';
            bool typeIsArray = n.TypeName.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase);

            if (isArrayItem)
                n.Kind = NodeKind.ArrayItem;
            else if (n.Children.Count > 0 && n.Children.All(c => c.Name.Length > 2 && c.Name[0] == '['))
                n.Kind = NodeKind.Array;
            else if (n.Children.Count > 0)
                n.Kind = NodeKind.Struct;
            else
                n.Kind = typeIsArray ? NodeKind.Array : NodeKind.Variable;

            if (n.Kind == NodeKind.Array)
            {
                if (n.Children.Count > 0) n.ElementCount = n.Children.Count;
                else
                {
                    // ARRAY[2..9] OF X → 8 элементов
                    var m = Regex.Match(n.TypeName, @"\[\s*(-?\d+)\s*\.\.\s*(-?\d+)\s*\]");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int lo) && int.TryParse(m.Groups[2].Value, out int hi))
                        n.ElementCount = Math.Max(0, hi - lo + 1);
                }
            }
            else if (n.Kind == NodeKind.Struct)
                n.ElementCount = n.Children.Count;
        }

        /// <summary>Запасной разбор неизвестного XML (Application Composer и пр.): ищем любые элементы с name/Name.</summary>
        private static int GenericXmlScan(XDocument doc, TagSource src)
        {
            int added = 0;
            foreach (var el in doc.Descendants())
            {
                var ln = el.Name.LocalName;
                if (ln is "Project" or "Application" or "Header") continue;
                string name = (string)el.Attribute("name") ?? ChildVal(el, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                string type = (string)el.Attribute("type") ?? ChildVal(el, "Type");
                string addr = (string)el.Attribute("address") ?? ChildVal(el, "Address");
                string comment = CollectComments(el);
                if (comment.Length == 0) comment = ChildVal(el, "Description");
                AddToTree(src.Roots, name.Trim(), (type ?? "").Trim(), (addr ?? "").Trim(), comment);
                added++;
            }
            return added;
        }

        private static string ChildVal(XElement el, string child)
        {
            var c = el.Elements().FirstOrDefault(e => e.Name.LocalName == child);
            return c != null ? c.Value.Trim() : "";
        }

        // ---------- ST-декларации ----------

        private static readonly Regex StLine = new Regex(
            @"^\s*([\w\.]+(?:\s*,\s*[\w\.]+)*)\s*:\s*([A-Za-z_][\w]*(?:\s*\[[^\]]*\])?(?:\s+OF\s+\w+)?|ARRAY\s*\[[^\]]*\]\s*OF\s+\w+)\s*(?::=\s*(.*?))?\s*;\s*(?://(.*)|\(\*(.*?)\*\))?\s*$",
            RegexOptions.Compiled);

        private static TagSource ParseSt(string text)
        {
            var src = new TagSource { Vendor = "ST-декларации" };
            foreach (var rawLine in text.Split('\n'))
            {
                var m = StLine.Match(rawLine);
                if (!m.Success) continue;
                string names = m.Groups[1].Value;
                string type = Regex.Replace(m.Groups[2].Value.Trim(), @"\s+", " ");
                string comment = m.Groups[4].Success ? m.Groups[4].Value.Trim()
                               : m.Groups[5].Success ? m.Groups[5].Value.Trim() : "";
                foreach (var n in names.Split(','))
                {
                    var name = n.Trim();
                    if (name.Length == 0) continue;
                    AddToTree(src.Roots, name, type, "", comment);
                }
            }
            FinalizeKinds(src.Roots);
            return src;
        }

        // ---------- Таблицы SYSMAC / CX-Designer ----------

        private static TagSource ParseTable(string text)
        {
            var src = new TagSource { Vendor = "SYSMAC Studio (таблица)" };
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return src;

            // Определяем разделитель по первой строке
            char delim = DetectDelimiter(lines[0]);
            var rows = lines.Select(l => SplitRow(l, delim)).Where(r => r.Length >= 2).ToList();
            if (rows.Count == 0) return src;

            // Ищем строку-заголовок
            int nameCol = -1, typeCol = -1, addrCol = -1, commentCol = -1, startRow = 0;
            for (int i = 0; i < Math.Min(3, rows.Count); i++)
            {
                int n = FindCol(rows[i], @"(имя|name|symbol|variable|тег|tag)");
                if (n < 0) continue;
                nameCol = n;
                typeCol = FindCol(rows[i], @"(тип|type|data\s*type)");
                addrCol = FindCol(rows[i], @"(адрес|address|addr|device|устройство)");
                commentCol = FindCol(rows[i], @"(коммент|comment|описание|description)");
                startRow = i + 1;
                break;
            }
            // Заголовка нет — эвристика: колонка типа = та, где чаще всего IEC-тип
            if (nameCol < 0)
            {
                nameCol = 0;
                int maxCols = rows.Max(r => r.Length);
                int bestScore = 0;
                for (int c = 1; c < maxCols; c++)
                {
                    int score = rows.Count(r => c < r.Length && IecType.IsMatch(r[c].Trim()));
                    if (score > bestScore) { bestScore = score; typeCol = c; }
                }
                if (bestScore == 0) typeCol = -1;
                // адрес — колонка с %I/%Q/AT/D100/HR10 и т.п.
                int bestAddr = 0;
                for (int c = 1; c < maxCols; c++)
                {
                    if (c == typeCol) continue;
                    int score = rows.Count(r => c < r.Length && Regex.IsMatch(r[c], @"^(%[IQM][XBWD]?\d|AT\s|^[A-Z]{1,3}\d+$|\d+\.\d+$)"));
                    if (score > bestAddr) { bestAddr = score; addrCol = c; }
                }
                if (bestAddr == 0) addrCol = -1;
                commentCol = -1;
            }

            for (int i = startRow; i < rows.Count; i++)
            {
                var r = rows[i];
                if (nameCol >= r.Length) continue;
                string name = r[nameCol].Trim().Trim('"');
                if (name.Length == 0) continue;
                string type = typeCol >= 0 && typeCol < r.Length ? r[typeCol].Trim().Trim('"') : "";
                type = NormalizeArrayType(type);
                string addr = addrCol >= 0 && addrCol < r.Length ? r[addrCol].Trim().Trim('"') : "";
                string comment = commentCol >= 0 && commentCol < r.Length ? r[commentCol].Trim().Trim('"') : "";
                AddToTree(src.Roots, name, type, addr, comment);
            }
            FinalizeKinds(src.Roots);
            return src;
        }

        private static char DetectDelimiter(string line)
        {
            int sc = line.Count(ch => ch == ';');
            int tc = line.Count(ch => ch == '\t');
            int cc = line.Count(ch => ch == ',');
            if (tc >= sc && tc >= cc) return '\t';
            if (sc >= cc) return ';';
            return ',';
        }

        private static string[] SplitRow(string line, char delim)
        {
            // Разделение с учётом кавычек
            var parts = new List<string>();
            var sb = new StringBuilder();
            bool inQ = false;
            foreach (var ch in line)
            {
                if (ch == '"') { inQ = !inQ; continue; }
                if (ch == delim && !inQ) { parts.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(ch);
            }
            parts.Add(sb.ToString());
            return parts.ToArray();
        }

        private static int FindCol(string[] row, string pattern)
        {
            for (int i = 0; i < row.Length; i++)
                if (Regex.IsMatch(row[i].Trim(), pattern, RegexOptions.IgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>BOOL[0..15] → ARRAY[0..15] OF BOOL — единый вид массивов из TXT/CSV с CODESYS XML.</summary>
        private static string NormalizeArrayType(string type)
        {
            var m = Regex.Match(type, @"^([A-Za-z_][\w.]*)\s*\[\s*(-?\d+)\s*\.\.\s*(-?\d+)\s*\]$");
            if (m.Success)
                return $"ARRAY[{m.Groups[2].Value}..{m.Groups[3].Value}] OF {m.Groups[1].Value}";
            return type;
        }

        // ---------- Построение дерева ----------

        /// <summary>Имя вида GVL.Motor[3].Speed → ветка GVL / Motor / [3] / Speed.</summary>
        private static void AddToTree(List<TagNode> roots, string fullName, string type, string addr, string comment)
        {
            var parts = SplitPath(fullName);
            List<TagNode> level = roots;
            TagNode node = null;
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (sb.Length > 0) sb.Append(sb.ToString().EndsWith("]") ? "" : ".");
                sb.Append(p);
                node = level.FirstOrDefault(n => n.Name == p);
                if (node == null)
                {
                    node = new TagNode { Name = p, FullPath = sb.ToString() };
                    level.Add(node);
                }
                level = node.Children;
            }
            if (node != null)
            {
                node.FullPath = fullName;
                node.TypeName = type;
                node.Address = addr;
                node.Comment = comment;
            }
        }

        private static List<string> SplitPath(string name)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            foreach (var seg in name.Split('.'))
            {
                sb.Clear();
                foreach (var ch in seg)
                {
                    if (ch == '[')
                    {
                        if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
                        sb.Append('[');
                    }
                    else if (ch == ']')
                    {
                        sb.Append(']');
                        parts.Add(sb.ToString());
                        sb.Clear();
                    }
                    else sb.Append(ch);
                }
                if (sb.Length > 0) parts.Add(sb.ToString());
            }
            return parts;
        }
    }
}
