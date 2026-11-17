using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Transfer
{
    /// <summary>Одна находка Sentinel (файл, точка автозапуска, параметр конфигурации).</summary>
    class SentinelFinding
    {
        public string Area = "", Name = "", Path = "", Rule = "", Description = "", Sha256 = "", Action = "", ActionMessage = "";
        public string SeverityName = "";   // Info | Low | Medium | High | Critical
        public int Severity;               // 0..4, как в Sentinel

        /// <summary>
        /// Определённое срабатывание: сигнатура, база хешей известных угроз, облачная репутация, EICAR.
        /// Только эвристика (балл угрозы, неподписанный файл под исключением Защитника) — не определённое.
        /// </summary>
        public bool Definitive
        {
            get
            {
                var parts = Rule.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p != "").ToList();
                return parts.Count > 0 && parts.Any(p => !p.StartsWith("Heuristic.", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Файл можно автоматически убрать в карантин: определённое срабатывание уровня Medium и выше.</summary>
        public bool Isolate { get { return Definitive && Severity >= 2 && Path != ""; } }

        public override string ToString()
        {
            return "[" + (SeverityName != "" ? SeverityName : Severity.ToString()) + "] " + (Name != "" ? Name : Rule) + (Path != "" ? " — " + Path : "") +
                   (Rule != "" && Rule != Name ? " (" + Rule + ")" : "");
        }
    }

    /// <summary>Итог одного или нескольких запусков Sentinel.</summary>
    class SentinelResult
    {
        public bool Ran;                    // хотя бы одна проверка отработала (код 0 или 1)
        public string Note = "";            // что пошло не так
        public long Checked;                // проверено объектов (файлов)
        public long ReadErrors;             // не удалось прочитать
        public string ReportDir = "";
        public readonly List<SentinelFinding> Findings = new List<SentinelFinding>();
        public readonly List<string> HtmlReports = new List<string>();
    }

    /// <summary>
    /// Sentinel — собственный антивирус (исходники — репозиторий Antivirus-tool). Встроен в Перенос.exe (src/sentinel/sentinel.zip);
    /// если в сборке его нет — берётся установленный C:\Program Files\Sentinel\Sentinel.Cli.exe или tools\Sentinel на диске переноса.
    /// Заменил Kaspersky Virus Removal Tool: ничего не скачивает из интернета, не перезапускает себя из %TEMP%
    /// и проверяет только те места клона, где живут программы и данные (системные файлы Windows сверяет sfc на следующем этапе).
    ///
    /// Используемый интерфейс командной строки (версия 1.3.x):
    ///   Sentinel.Cli.exe scan &lt;путь&gt; [--no-recurse] [--report-json &lt;файл&gt;] [--report-html &lt;файл&gt;]
    ///   Sentinel.Cli.exe audit-startup|audit-config [--report-json &lt;файл&gt;] [--report-html &lt;файл&gt;]
    /// Коды возврата: 0 — чисто, 1 — найдены угрозы или подозрения, 2 — ошибка.
    /// В текстовом режиме scan печатает строку на каждый файл: «[чисто   ] путь», «[УГРОЗА  ] путь» и т. п. — по ним считается прогресс.
    /// JSON-отчёт: { Areas:[{Name,Checked,Found,Note}], Findings:[{Area,Name,Path,Sha256,Rule,Severity,Description,ActionStatus,ActionMessage}] }.
    /// Разбор устойчив к формату: если JSON не прочитался, находки берутся из текстового вывода.
    /// </summary>
    static class SentinelScan
    {
        public const string ExeName = "Sentinel.Cli.exe";

        static readonly Regex FileLine = new Regex(@"^\[(?<mark>[^\]]{1,12})\]\s+(?<path>(?:[A-Za-z]:\\|\\\\).+)$", RegexOptions.Compiled);
        static readonly Regex DetLine = new Regex(@"^\s{4,}(?<sev>Info|Low|Medium|High|Critical)\s+(?<name>\S+)\s+\[(?<src>[^\]]*)\]\s*[—-]\s*(?<desc>.*)$", RegexOptions.Compiled);
        static readonly string[] SevNames = { "Info", "Low", "Medium", "High", "Critical" };

        // Папки клона, которые не проверяются: служебные и чужие копии Windows. Сама папка Windows проверяется выборочно (см. Targets).
        static readonly HashSet<string> SkipTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Windows", "Windows.old", "System Volume Information", "Recovery", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "$GetCurrent", "ESD" };
        // Места внутри Windows, где прячутся вирусы. Остальное — системные файлы, их сверяет sfc с оригиналами Microsoft.
        static readonly string[] WindowsParts = { "Temp", "Tasks", "Setup", @"System32\Tasks", @"System32\drivers", @"System32\GroupPolicy", @"SysWOW64\GroupPolicy", @"System32\spool\drivers" };

        public const string ResourceName = "Transfer.sentinel.zip";

        /// <summary>
        /// Дополнительные параметры запуска: если Sentinel на компьютере не установлен, его служебные данные (настройки, кэш)
        /// живут в %LOCALAPPDATA%\Perenos\sentinel-data, а не в ProgramData чужого компьютера. Если установлен — берутся его данные
        /// (там обновлённая база угроз и белый список пользователя).
        /// </summary>
        public static string ExtraArgs = "";

        static string LocalBase { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Perenos"); } }

        /// <summary>
        /// Sentinel, встроенный в Перенос.exe (ресурс Transfer.sentinel.zip, собирается scripts/update-sentinel.sh): распаковывается
        /// один раз в %LOCALAPPDATA%\Perenos\sentinel\&lt;отпечаток&gt;\. null — в этой сборке Sentinel не встроен или распаковать не удалось.
        /// </summary>
        static string Embedded(out string version)
        {
            version = "";
            try
            {
                using (var s = typeof(SentinelScan).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (s == null) { Log.Write("sentinel: в эту сборку Sentinel не встроен"); return null; }
                    string id;
                    using (var sha = System.Security.Cryptography.SHA1.Create()) id = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
                    s.Position = 0;
                    string baseDir = Path.Combine(LocalBase, "sentinel"), dir = Path.Combine(baseDir, id), exe = Path.Combine(dir, ExeName), ok = Path.Combine(dir, ".ok");
                    if (!File.Exists(ok) || !File.Exists(exe))
                    {
                        Directory.CreateDirectory(baseDir);
                        // старые распакованные версии больше не нужны
                        foreach (var d in Directory.GetDirectories(baseDir)) { try { Directory.Delete(d, true); } catch { } }
                        string tmp = dir + ".part";
                        Directory.CreateDirectory(tmp);
                        string full = Path.GetFullPath(tmp).TrimEnd('\\') + "\\";
                        using (var zip = new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Read))
                            foreach (var e in zip.Entries)
                            {
                                if (string.IsNullOrEmpty(e.Name)) continue;          // папка
                                string dst = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', '\\')));
                                if (!dst.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;    // путь вне папки — не распаковываем
                                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                                System.IO.Compression.ZipFileExtensions.ExtractToFile(e, dst, true);
                            }
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        Directory.Move(tmp, dir);
                        File.WriteAllText(ok, DateTime.Now.ToString("s"));
                        Log.Write("sentinel: встроенный Sentinel распакован в " + dir);
                    }
                    try { version = File.ReadAllText(Path.Combine(dir, "sentinel-version.txt")).Trim(); } catch { }
                    var m = Regex.Match(version, @"Sentinel\s+([\d\.]+)"); if (m.Success) version = m.Groups[1].Value;
                    return File.Exists(exe) ? exe : null;
                }
            }
            catch (Exception e) { Log.Write("sentinel: не удалось распаковать встроенный Sentinel — " + e.Message); return null; }
        }

        /// <summary>
        /// Где взять Sentinel: встроенный в программу (основной путь — ничего не нужно ставить и скачивать),
        /// затем установленный (Program Files), затем копия в папке tools на диске переноса.
        /// </summary>
        public static string Find(string root, out string version, out string note)
        {
            version = ""; note = "";
            string pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sentinel");
            string own = Path.Combine(LocalBase, "sentinel-data");
            ExtraArgs = Directory.Exists(pd) ? "" : " --data-dir " + Sh.Quote(own);
            string emb = Embedded(out version);
            if (emb != null) { Log.Write("sentinel: встроенный " + version + (ExtraArgs != "" ? ", данные в " + own : ", данные установленного Sentinel")); return emb; }
            var cands = new List<string>();
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("ProgramFiles") })
                if (!string.IsNullOrEmpty(pf)) cands.Add(Path.Combine(pf, "Sentinel", ExeName));
            if (!string.IsNullOrEmpty(root))
            {
                cands.Add(Path.Combine(root, "tools", "Sentinel", ExeName));
                cands.Add(Path.Combine(root, "tools", ExeName));
            }
            foreach (var c in cands.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(c)) continue;
                    var vi = FileVersionInfo.GetVersionInfo(c);
                    string id = (vi.ProductName ?? "") + " " + (vi.FileDescription ?? "") + " " + (vi.OriginalFilename ?? "") + " " + (vi.InternalName ?? "");
                    // У однофайловой сборки .NET сведения о версии могут быть пустыми — тогда достаточно имени файла.
                    if (id.Trim() != "" && id.IndexOf("Sentinel", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        Log.Write("sentinel: " + c + " — не Sentinel (" + id.Trim() + "), пропускаю");
                        continue;
                    }
                    version = vi.ProductVersion ?? vi.FileVersion ?? "";
                    int plus = version.IndexOf('+'); if (plus > 0) version = version.Substring(0, plus);
                    Log.Write("sentinel: найден " + c + (version != "" ? ", версия " + version : ""));
                    return c;
                }
                catch (Exception e) { Log.Write("sentinel: " + c + " — " + e.Message); }
            }
            note = "Sentinel не встроен в эту сборку программы и не установлен на компьютере";
            return null;
        }

        /// <summary>Что проверять в развёрнутом клоне: файлы в корне, все папки верхнего уровня, кроме служебных, и уязвимые места внутри Windows.</summary>
        public static List<KeyValuePair<string, bool>> Targets(string cloneRoot)
        {
            string r = cloneRoot.TrimEnd('\\') + "\\";
            var list = new List<KeyValuePair<string, bool>>();      // путь, рекурсивно
            list.Add(new KeyValuePair<string, bool>(r, false));
            try
            {
                foreach (var d in Directory.GetDirectories(r).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    if (!SkipTop.Contains(Path.GetFileName(d))) list.Add(new KeyValuePair<string, bool>(d, true));
                }
            }
            catch { }
            foreach (var w in WindowsParts)
            {
                string d = Path.Combine(r, "Windows", w);
                if (Directory.Exists(d)) list.Add(new KeyValuePair<string, bool>(d, true));
            }
            return list;
        }

        /// <summary>Сколько файлов увидит Sentinel (он тоже не заходит в ссылки-переходы). Для процента и оставшегося времени.</summary>
        public static long CountFiles(string dir, bool recurse, Func<bool> stop)
        {
            long n = 0; var stack = new Stack<string>(); stack.Push(dir);
            while (stack.Count > 0)
            {
                if (stop()) return -1;
                string cur = stack.Pop();
                try
                {
                    foreach (var e in new DirectoryInfo(cur).EnumerateFileSystemInfos())
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((e.Attributes & FileAttributes.Directory) != 0) { if (recurse) stack.Push(e.FullName); }
                        else n++;
                    }
                }
                catch { }
            }
            return n;
        }

        /// <summary>
        /// Проверить клон файловым сканером Sentinel (только обнаружение — изоляцию делает программа по своим правилам).
        /// onFile(путь, всего проверено, найдено) вызывается на каждый проверенный файл. stop — отмена или «Пропустить проверку».
        /// </summary>
        public static SentinelResult ScanClone(string exe, List<KeyValuePair<string, bool>> targets, string reportDir, Action<string, long, int> onFile, Action<string> status, Func<bool> stop)
        {
            var res = new SentinelResult { ReportDir = reportDir };
            Directory.CreateDirectory(reportDir);
            long done = 0; int found = 0; int errors2 = 0, ran = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                if (stop()) break;
                string dir = targets[i].Key; bool recurse = targets[i].Value;
                string name = (i + 1).ToString("00") + "-" + Safe(recurse ? Path.GetFileName(dir.TrimEnd('\\')) : "root");
                string json = Path.Combine(reportDir, name + ".json"), html = Path.Combine(reportDir, name + ".html");
                string args = "scan " + Sh.Quote(dir) + (recurse ? "" : " --no-recurse") + " --report-json " + Sh.Quote(json) + " --report-html " + Sh.Quote(html) + ExtraArgs;
                status("Sentinel проверяет " + Fmt.ShortPath(dir, 80));
                var text = new List<SentinelFinding>(); SentinelFinding cur = null; var errs = new List<string>();
                var started = DateTime.Now;
                Log.Write("sentinel " + args);
                int rc = Sh.Run(exe, args, line =>
                {
                    var m = FileLine.Match(line);
                    if (m.Success)
                    {
                        string mark = m.Groups["mark"].Value.Trim(); string path = m.Groups["path"].Value.Trim();
                        long n = Interlocked.Increment(ref done);
                        cur = null;
                        if (mark.StartsWith("УГРОЗА", StringComparison.OrdinalIgnoreCase) || mark.StartsWith("ПОДОЗР", StringComparison.OrdinalIgnoreCase))
                        {
                            cur = new SentinelFinding { Area = "Файлы", Path = path, Name = Path.GetFileName(path), Severity = mark.StartsWith("УГРОЗА", StringComparison.OrdinalIgnoreCase) ? 3 : 1 };
                            lock (text) text.Add(cur);
                            Interlocked.Increment(ref found);
                            Log.Write("sentinel> " + line.Trim());
                        }
                        else if (mark.StartsWith("ОШИБКА", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref res.ReadErrors);
                        if (onFile != null) onFile(path, n, found);
                        return;
                    }
                    var d = DetLine.Match(line);
                    if (d.Success && cur != null)
                    {
                        int sev = Array.IndexOf(SevNames, d.Groups["sev"].Value);
                        string src = d.Groups["src"].Value; int colon = src.IndexOf(':');
                        string rule = d.Groups["name"].Value + (colon > 0 ? " [" + src.Substring(colon + 1).Trim() + "]" : "");
                        cur.Rule = cur.Rule == "" ? rule : cur.Rule + "; " + rule;
                        cur.Description = (cur.Description + " " + d.Groups["desc"].Value).Trim();
                        if (sev > cur.Severity || cur.SeverityName == "") { cur.Severity = Math.Max(cur.Severity, sev); cur.SeverityName = SevNames[cur.Severity]; }
                        Log.Write("sentinel> " + line.Trim());
                        return;
                    }
                    string t = line.Trim();
                    if (t.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Unhandled", StringComparison.OrdinalIgnoreCase) || t.StartsWith("[правила]"))
                    { lock (errs) { if (errs.Count < 5) errs.Add(t); } Log.Write("sentinel> " + t); }
                    else if (t.StartsWith("Проверено файлов") || t.StartsWith("Правил загружено") || t.StartsWith("JSON-отчёт") || t.StartsWith("HTML-отчёт")) Log.Write("sentinel> " + t);
                }, Encoding.UTF8, null, stop);
                Log.Write("sentinel: " + Fmt.ShortPath(dir, 120) + " -> код " + rc + ", " + Fmt.Clock(DateTime.Now - started));
                if (stop()) break;
                if (rc == 0 || rc == 1)
                {
                    ran++;
                    var fromJson = ReadReport(json, res);
                    // JSON — основной источник. Если его нет (другая версия Sentinel), берём находки из текстового вывода.
                    foreach (var f in (fromJson ?? text)) AddUnique(res, f);
                    if (File.Exists(html)) res.HtmlReports.Add(html);
                    if (rc == 1 && (fromJson ?? text).Count == 0) Log.Write("sentinel: код 1, но находок в отчёте нет");
                }
                else
                {
                    errors2++;
                    if (res.Note == "") res.Note = "Sentinel завершился с ошибкой (код " + rc + ")" + (errs.Count > 0 ? ": " + errs[0] : "");
                }
            }
            if (res.Checked < done) res.Checked = done;
            res.Ran = ran > 0;
            if (res.Ran && errors2 > 0) res.Note = "часть папок не проверена: " + res.Note;
            return res;
        }

        /// <summary>Аудит живой системы (audit-startup, audit-config): быстро, без полного прохода по диску. Для снятия клона.</summary>
        public static SentinelResult Audit(string exe, string[] verbs, string reportDir, Action<string> status, Func<bool> stop)
        {
            var res = new SentinelResult { ReportDir = reportDir };
            Directory.CreateDirectory(reportDir);
            int ran = 0;
            foreach (var verb in verbs)
            {
                if (stop()) break;
                string json = Path.Combine(reportDir, verb + ".json"), html = Path.Combine(reportDir, verb + ".html");
                status("Sentinel: " + (verb == "audit-startup" ? "автозагрузка и закрепление в системе" : verb == "audit-config" ? "настройки защиты Windows" : verb));
                string first = "";
                int rc = Sh.Run(exe, verb + " --report-json " + Sh.Quote(json) + " --report-html " + Sh.Quote(html) + ExtraArgs, l => { if (first == "" && l.Trim() != "") first = l.Trim(); }, Encoding.UTF8, null, stop);
                Log.Write("sentinel " + verb + " -> код " + rc);
                if (stop()) break;
                if (rc == 0 || rc == 1)
                {
                    ran++;
                    var f = ReadReport(json, res);
                    if (f != null) foreach (var x in f) AddUnique(res, x);
                    if (File.Exists(html)) res.HtmlReports.Add(html);
                }
                else if (res.Note == "") res.Note = verb + ": код " + rc + (first != "" ? " (" + first + ")" : "");
            }
            res.Ran = ran > 0;
            return res;
        }

        static void AddUnique(SentinelResult res, SentinelFinding f)
        {
            if (f.Path != "" && res.Findings.Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase) && x.Rule == f.Rule)) return;
            if (res.Findings.Count < 5000) res.Findings.Add(f);
        }

        static string Safe(string s) { s = Regex.Replace(s ?? "", @"[^\w\-\.]", "_"); return s.Length > 40 ? s.Substring(0, 40) : s == "" ? "root" : s; }

        /// <summary>Находки из JSON-отчёта Sentinel; null — отчёта нет или он не читается.</summary>
        static List<SentinelFinding> ReadReport(string path, SentinelResult res)
        {
            if (!File.Exists(path)) return null;
            var d = Json.ReadFile(path);
            if (d == null) { Log.Write("sentinel: не читается " + path); return null; }
            var list = new List<SentinelFinding>();
            foreach (var a in Items(Get(d, "Areas")))
            {
                long n; if (long.TryParse(Str(a, "Checked"), out n)) res.Checked += n;
                string note = Str(a, "Note"); long e;
                var me = Regex.Match(note, @"(\d+)"); if (note.IndexOf("ошиб", StringComparison.OrdinalIgnoreCase) >= 0 && me.Success && long.TryParse(me.Groups[1].Value, out e)) res.ReadErrors += e;
            }
            foreach (var f in Items(Get(d, "Findings")))
            {
                var x = new SentinelFinding
                {
                    Area = Str(f, "Area"), Name = Str(f, "Name"), Path = Str(f, "Path"), Rule = Str(f, "Rule"), Description = Str(f, "Description"),
                    Sha256 = Str(f, "Sha256"), Action = Str(f, "ActionStatus"), ActionMessage = Str(f, "ActionMessage")
                };
                string sev = Str(f, "Severity"); int sv;
                if (int.TryParse(sev, out sv)) x.Severity = Math.Max(0, Math.Min(4, sv));
                else { int ix = Array.FindIndex(SevNames, s => string.Equals(s, sev, StringComparison.OrdinalIgnoreCase)); x.Severity = ix < 0 ? 1 : ix; }
                x.SeverityName = SevNames[x.Severity];
                list.Add(x);
            }
            return list;
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            foreach (var kv in d) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        static string Str(Dictionary<string, object> d, string key) { var v = Get(d, key); return v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }

        static IEnumerable<Dictionary<string, object>> Items(object v)
        {
            var e = v as IEnumerable;
            if (e == null || v is string) yield break;
            foreach (var o in e) { var d = o as Dictionary<string, object>; if (d != null) yield return d; }
        }
    }

    /// <summary>
    /// Карантин на диске переноса: найденное в клоне не удаляется, а переносится в data\quarantine\&lt;дата&gt;\ с расширением .quarantine,
    /// чтобы его нельзя было случайно запустить. В manifest.txt — откуда взят каждый файл: при ложном срабатывании его можно вернуть.
    /// </summary>
    static class Quarantine
    {
        public static string Dir(string root, string stamp) { return Path.Combine(root, "data", "quarantine", stamp); }

        /// <summary>Перенести файл из клона в карантин. Возвращает путь в карантине или null.</summary>
        public static string Put(string qdir, string cloneRoot, string file, string reason)
        {
            try
            {
                if (!File.Exists(file)) return null;
                string r = cloneRoot.TrimEnd('\\') + "\\";
                string rel = file.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? file.Substring(r.Length) : Path.GetFileName(file);
                string dst = Path.Combine(qdir, rel) + ".quarantine";
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (File.Exists(dst)) dst = Path.Combine(Path.GetDirectoryName(dst), Path.GetFileNameWithoutExtension(dst) + "-" + DateTime.Now.ToString("HHmmssfff") + ".quarantine");
                File.SetAttributes(file, FileAttributes.Normal);
                File.Copy(file, dst, false);
                if (new FileInfo(dst).Length != new FileInfo(file).Length) { File.Delete(dst); return null; }
                File.Delete(file);
                File.AppendAllText(Path.Combine(qdir, "manifest.txt"), DateTime.Now.ToString("s") + "\t" + file + "\t→\t" + dst + "\t" + reason + Environment.NewLine, new UTF8Encoding(true));
                Log.Write("карантин: " + file + " → " + dst + " (" + reason + ")");
                return dst;
            }
            catch (Exception e) { Log.Write("карантин: не удалось перенести " + file + " — " + e.Message); return null; }
        }
    }
}
