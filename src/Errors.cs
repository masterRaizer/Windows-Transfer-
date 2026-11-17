using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Transfer
{
    /// <summary>Одна ошибка копирования из журнала robocopy.</summary>
    class CopyError
    {
        public int Code;
        public string Path = "";
        public string Job = "";
    }

    /// <summary>Группа одинаковых по смыслу ошибок, описанная человеческим языком.</summary>
    class Problem
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Explain { get; set; }
        public string Action { get; set; }
        /// <summary>0 — не страшно, 1 — стоит посмотреть, 2 — серьёзно.</summary>
        public int Severity { get; set; }
        public int Count { get; set; }
        public List<string> Samples { get; set; }

        public string CountText { get { return Fmt.Count(Count, "файл", "файла", "файлов"); } }
        public string SeverityText { get { return Severity == 0 ? "НЕ СТРАШНО" : Severity == 1 ? "СТОИТ ПРОВЕРИТЬ" : "ВАЖНО"; } }
        public string SamplesText { get { return Samples == null ? "" : string.Join(Environment.NewLine, Samples); } }
        public Problem() { Samples = new List<string>(); }
    }

    static class Errors
    {
        // «2026/11/12 15:38:33 ОШИБКА 1920 (0x00000780) Копирование файла C:\…\python.exe»
        static readonly Regex Line = new Regex(@"^\d{4}/\d\d/\d\d \d\d:\d\d:\d\d\s+\S+\s+(\d+)\s+\(0x[0-9A-Fa-f]{8}\)\s+.+?\s+([A-Za-z]:\\.*|\\\\.*)$", RegexOptions.Compiled);

        public static bool TryParse(string line, string job, out CopyError err)
        {
            err = null;
            var m = Line.Match(line ?? "");
            if (!m.Success) return false;
            err = new CopyError { Code = int.Parse(m.Groups[1].Value), Path = m.Groups[2].Value.Trim(), Job = job };
            return true;
        }

        public static List<CopyError> ParseLog(string file, string job)
        {
            var list = new List<CopyError>();
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs, Encoding.Unicode, true))
                {
                    string l;
                    while ((l = r.ReadLine()) != null) { CopyError e; if (TryParse(l, job, out e)) list.Add(e); }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Журналы последнего сохранения, сделанного старой (скриптовой) версией.</summary>
        public static List<CopyError> ParseLegacyLogs(string logsDir)
        {
            var all = new List<CopyError>();
            if (!Directory.Exists(logsDir)) return all;
            var rx = new Regex(@"^robocopy-(\d{8}-\d{6})-(.+)\.log$");
            var files = Directory.GetFiles(logsDir, "robocopy-*.log").Select(f => new { f, m = rx.Match(System.IO.Path.GetFileName(f)) }).Where(x => x.m.Success).ToList();
            if (files.Count == 0) return all;
            string last = files.Max(x => x.m.Groups[1].Value);
            foreach (var x in files.Where(x => x.m.Groups[1].Value == last)) all.AddRange(ParseLog(x.f, x.m.Groups[2].Value));
            return all;
        }

        static string AppOf(string path)
        {
            var m = Regex.Match(path, @"\\AppData\\(?:Local|Roaming|LocalLow)\\([^\\]+)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
            if (!m.Success) m = Regex.Match(path, @"\\Program Files(?: \(x86\))?\\([^\\]+)", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(path, @"\\ProgramData\\([^\\]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        static bool IsDeviceCode(int c)
        {
            return c == 21 || c == 55 || c == 1167 || c == 433 || c == 1117 || c == 31 || c == 121 || c == 483 || c == 1006 || c == 15;
        }

        /// <summary>Ключ группы: по нему одинаковые по смыслу ошибки складываются вместе.</summary>
        static string KeyOf(CopyError e, string destRoot)
        {
            bool onDest = !string.IsNullOrEmpty(destRoot) && e.Path.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase);
            string p = e.Path.ToLowerInvariant();
            if (IsDeviceCode(e.Code)) return onDest ? "device-dest" : "device-src";
            if (e.Code == 112 || e.Code == 39) return "full";
            if ((e.Code == 1920 || (e.Code >= 4390 && e.Code <= 4394)) && p.Contains(@"\windowsapps\")) return "store-alias";
            if (e.Code == 1920 || (e.Code >= 4390 && e.Code <= 4394)) return "special";
            if (e.Code >= 360 && e.Code <= 404) return "cloud";
            if (e.Code == 32 || e.Code == 33) return "busy";
            if (e.Code == 2 || e.Code == 3) return onDest ? "device-dest" : "gone";
            if (e.Code == 5 || e.Code == 1314) return "denied";
            if (e.Code == 23 || e.Code == 1392 || e.Code == 1393) return "corrupt";
            if (e.Code == 206 || e.Code == 123 || e.Code == 111 || e.Code == 87) return "name";
            if (e.Code == 225 || e.Code == 226) return "antivirus";
            return "other-" + e.Code;
        }

        static Problem Describe(string key, List<CopyError> items)
        {
            var apps = items.Select(i => AppOf(i.Path)).Where(a => a != "").GroupBy(a => a).OrderByDescending(g => g.Count()).Take(4).Select(g => g.Key).ToList();
            string appList = apps.Count > 0 ? " (" + string.Join(", ", apps) + ")" : "";
            var p = new Problem { Key = key };
            switch (key)
            {
                case "store-alias":
                    p.Severity = 0; p.Title = "Ярлыки приложений из Microsoft Store";
                    p.Explain = "Это не настоящие файлы, а пустые системные ярлыки-заглушки вроде python.exe и winget.exe. Windows создаёт их сама, когда устанавливается приложение из Магазина, и скопировать их нельзя в принципе.";
                    p.Action = "Ничего делать не нужно: на новом компьютере они появятся сами вместе с приложениями.";
                    break;
                case "special":
                    p.Severity = 0; p.Title = "Служебные файлы-заглушки Windows";
                    p.Explain = "Это специальные системные файлы-ссылки, которые существуют только внутри этой Windows. Данных программ в них нет.";
                    p.Action = "Ничего делать не нужно.";
                    break;
                case "cloud":
                    p.Severity = 1; p.Title = "Файлы, которые лежат только в облаке (OneDrive)";
                    p.Explain = "На диске от этих файлов есть только значок, а само содержимое хранится в облаке, поэтому копировать нечего.";
                    p.Action = "Они останутся в облаке и появятся на новом компьютере после входа в OneDrive. Если нужны копии на диске — открой OneDrive, выбери «Всегда хранить на этом устройстве» и нажми «Досохранить».";
                    break;
                case "busy":
                    p.Severity = 1; p.Title = "Файлы были заняты работающей программой" + appList;
                    p.Explain = "Программа держала эти файлы открытыми, и Windows не дала их прочитать.";
                    p.Action = "Закрой эти программы (включая значки возле часов) и нажми «Досохранить» — скопируется только недостающее.";
                    break;
                case "gone":
                    p.Severity = 0; p.Title = "Временные файлы, которые исчезли во время копирования";
                    p.Explain = "Программы постоянно создают и удаляют временные файлы. Пока шло копирование, эти файлы уже были удалены самой программой.";
                    p.Action = "Ничего делать не нужно.";
                    break;
                case "denied":
                    p.Severity = 1; p.Title = "Windows не разрешила прочитать файлы" + appList;
                    p.Explain = "Доступ к этим файлам закрыт системой или антивирусом даже для администратора. Обычно это защищённые служебные данные, а не твои документы.";
                    p.Action = "Посмотри список ниже. Если там есть что-то нужное — на время отключи защиту антивируса и нажми «Досохранить».";
                    break;
                case "corrupt":
                    p.Severity = 2; p.Title = "Файлы повреждены на этом компьютере";
                    p.Explain = "Диск не смог прочитать эти файлы — так бывает при сбойных участках диска. Проблема в исходном диске, а не в переносе.";
                    p.Action = "Эти файлы, скорее всего, уже были испорчены. Проверь диск: правой кнопкой по диску → Свойства → Сервис → Проверить.";
                    break;
                case "name":
                    p.Severity = 1; p.Title = "Слишком длинные или недопустимые имена файлов";
                    p.Explain = "Путь к этим файлам длиннее, чем разрешает Windows, либо в имени есть недопустимые символы.";
                    p.Action = "Если файлы нужны — переименуй папки покороче и нажми «Досохранить».";
                    break;
                case "antivirus":
                    p.Severity = 1; p.Title = "Антивирус заблокировал файлы";
                    p.Explain = "Защитник Windows или другой антивирус посчитал эти файлы опасными и не дал их скопировать.";
                    p.Action = "Если ты уверен в этих файлах — добавь их в исключения антивируса и нажми «Досохранить».";
                    break;
                case "full":
                    p.Severity = 2; p.Title = "На диске закончилось место";
                    p.Explain = "Диск, на который шла запись, заполнился, и часть файлов не поместилась.";
                    p.Action = "Освободи место на диске и нажми «Досохранить» — скопируется только недостающее.";
                    break;
                case "device-dest":
                    p.Severity = 2; p.Title = "Диск переноса отключался или работал с ошибками";
                    p.Explain = "Во время записи съёмный диск пропадал из системы или не отвечал. Чаще всего виноват кабель, разъём или USB-хаб.";
                    p.Action = "Подключи диск напрямую в разъём на корпусе, по возможности другим кабелем, и нажми «Досохранить».";
                    break;
                case "device-src":
                    p.Severity = 2; p.Title = "Диск компьютера отвечал с ошибками";
                    p.Explain = "Windows сообщила об ошибке устройства при чтении этих файлов.";
                    p.Action = "Нажми «Досохранить». Если ошибка повторится на тех же файлах — проверь диск (Свойства диска → Сервис → Проверить).";
                    break;
                default:
                    int code = items[0].Code; string msg;
                    try { msg = new System.ComponentModel.Win32Exception(code).Message; } catch { msg = ""; }
                    p.Severity = 1; p.Title = "Другая ошибка Windows (№ " + code + ")";
                    p.Explain = "Windows сообщила: «" + msg + "».";
                    p.Action = "Посмотри список файлов ниже. Если среди них есть нужные — нажми «Досохранить»; если не поможет, пришли этот отчёт тому, кто помогает с компьютером.";
                    break;
            }
            return p;
        }

        /// <summary>Сворачивает сырые ошибки в понятные группы (повторные попытки одного файла считаются один раз).</summary>
        public static List<Problem> Group(IEnumerable<CopyError> errors, string destRoot, string snapPrefix = null, string snapReal = null)
        {
            var unique = new Dictionary<string, CopyError>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in errors)
            {
                if (snapPrefix != null && e.Path.StartsWith(snapPrefix, StringComparison.OrdinalIgnoreCase)) e.Path = snapReal + e.Path.Substring(snapPrefix.Length);
                unique[e.Path] = e;   // последняя попытка
            }
            var res = new List<Problem>();
            foreach (var g in unique.Values.GroupBy(e => KeyOf(e, destRoot)))
            {
                var items = g.ToList();
                var p = Describe(g.Key, items);
                p.Count = items.Count;
                p.Samples = items.Select(i => i.Path).OrderBy(s => s).Take(200).ToList();
                res.Add(p);
            }
            return res.OrderByDescending(p => p.Severity).ThenByDescending(p => p.Count).ToList();
        }

        public static string Report(string title, IEnumerable<string> summary, List<Problem> problems)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title); sb.AppendLine(new string('=', title.Length)); sb.AppendLine();
            foreach (var s in summary) sb.AppendLine(s);
            sb.AppendLine();
            if (problems.Count == 0) { sb.AppendLine("Все файлы скопированы без замечаний."); return sb.ToString(); }
            sb.AppendLine("ЧТО НЕ СКОПИРОВАЛОСЬ И ПОЧЕМУ"); sb.AppendLine();
            foreach (var p in problems)
            {
                sb.AppendLine("[" + p.SeverityText + "] " + p.Title + " — " + p.CountText);
                sb.AppendLine("  Что это: " + p.Explain);
                sb.AppendLine("  Что делать: " + p.Action);
                foreach (var s in p.Samples.Take(40)) sb.AppendLine("     " + s);
                if (p.Count > 40) sb.AppendLine("     … и ещё " + (p.Count - 40));
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
