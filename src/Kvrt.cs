using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Transfer
{
    /// <summary>Итог проверки Kaspersky Virus Removal Tool.</summary>
    class KvrtResult
    {
        public bool Ran;                    // утилита отработала
        public string Note = "";            // почему не запускалась / что пошло не так
        public int Quarantined;             // сколько объектов Kaspersky убрал в карантин
        public readonly List<string> Detections = new List<string>();   // строки о находках из вывода утилиты
        public string ReportDir = "";
    }

    /// <summary>
    /// Kaspersky Virus Removal Tool — бесплатная утилита «Лаборатории Касперского». Программа не носит её с собой
    /// (у утилиты встроенные базы, через день они устаревают, а распространять её копию лицензия не разрешает):
    /// перед проверкой свежая версия скачивается с официального сайта в папку tools на диске переноса,
    /// у файла проверяется цифровая подпись Kaspersky, и только потом он запускается.
    /// Параметры — по документации KVRT 2020 для Windows (support.kaspersky.com/15679).
    /// </summary>
    static class Kvrt
    {
        public const string Url = "https://devbuilds.s.kaspersky-labs.com/devbuilds/KVRT/latest/full/KVRT.exe";
        static readonly TimeSpan Fresh = TimeSpan.FromHours(20);     // базы обновляются раз в сутки
        static readonly TimeSpan Usable = TimeSpan.FromDays(7);      // без интернета — старая копия, если ей не больше недели

        /// <summary>Свежий KVRT.exe в папке tools (скачать при необходимости). null — взять негде.</summary>
        public static string Get(string toolsDir, Action<string> progress, Func<bool> cancelled, out string note)
        {
            note = "";
            Directory.CreateDirectory(toolsDir);
            string exe = Path.Combine(toolsDir, "KVRT.exe"), part = exe + ".download";
            DateTime got = DateTime.MinValue;
            try { if (File.Exists(exe)) got = File.GetLastWriteTime(exe); } catch { }
            if (File.Exists(exe) && DateTime.Now - got < Fresh && IsKaspersky(exe)) return exe;

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var rq = (HttpWebRequest)WebRequest.Create(Url);
                rq.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"; rq.Timeout = 30000; rq.ReadWriteTimeout = 60000;
                using (var rs = rq.GetResponse())
                using (var src = rs.GetResponseStream())
                using (var dst = File.Create(part))
                {
                    long total = rs.ContentLength, done = 0; var buf = new byte[1 << 20]; int n; DateTime next = DateTime.MinValue;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled()) { note = "отменено"; break; }
                        dst.Write(buf, 0, n); done += n;
                        if (DateTime.Now >= next) { next = DateTime.Now.AddSeconds(1); progress("скачиваю Kaspersky Virus Removal Tool с сайта Kaspersky: " + Fmt.Size(done) + (total > 0 ? " из " + Fmt.Size(total) : "")); }
                    }
                }
                if (cancelled()) { TryDelete(part); return null; }
                if (!IsKaspersky(part)) { TryDelete(part); note = "скачанный файл не подписан «Лабораторией Касперского» — не запускаю"; Log.Write("kvrt: подпись не прошла"); return UsableOld(exe, got, ref note); }
                TryDelete(exe); File.Move(part, exe); File.SetLastWriteTime(exe, DateTime.Now);
                Log.Write("kvrt: скачан " + new FileInfo(exe).Length + " байт, версия " + FileVersionInfo.GetVersionInfo(exe).ProductVersion);
                return exe;
            }
            catch (Exception e)
            {
                TryDelete(part);
                Log.Write("kvrt: скачать не удалось — " + e.Message);
                note = "не удалось скачать с сайта Kaspersky (" + e.Message + ")";
                return UsableOld(exe, got, ref note);
            }
        }

        static string UsableOld(string exe, DateTime got, ref string note)
        {
            if (File.Exists(exe) && DateTime.Now - got < Usable && IsKaspersky(exe)) { note += "; взята копия от " + got.ToString("dd.MM HH:mm"); return exe; }
            return null;
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        /// <summary>Действительная подпись AO Kaspersky Lab.</summary>
        public static bool IsKaspersky(string path)
        {
            try
            {
                if (!Security.HasValidSignature(path)) return false;
                var c = X509Certificate.CreateFromSignedFile(path);
                return c.Subject.IndexOf("Kaspersky Lab", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Проверить и вылечить папки. level: 1 — только опасное, 2 — плюс рекламное, 3 — всё, включая «потенциально опасные» программы
        /// (к ним Kaspersky относит майнеры: not-a-virus:RiskTool). Найденное лечится, а если нельзя — удаляется; копии уходят в карантин в reportDir.
        /// Без -adinsilent: он перезагружает компьютер сразу после лечения.
        /// </summary>
        public static KvrtResult Scan(string exe, IList<string> folders, string reportDir, int level, Action<string> progress, Func<bool> cancelled)
        {
            var r = new KvrtResult { ReportDir = reportDir };
            Directory.CreateDirectory(reportDir);
            string list = Path.Combine(reportDir, "folders.txt");
            File.WriteAllText(list, string.Join("\r\n", folders.Select(f => f.TrimEnd('\\') + (f.Length <= 3 ? "\\" : ""))), new UTF8Encoding(true));
            string args = "-accepteula -silent -processlevel " + Math.Max(1, Math.Min(3, level)) + " -customlist \"" + list + "\" -d \"" + reportDir.TrimEnd('\\') + "\" -dontencrypt -details -fixednames -noads";
            Log.Write("kvrt " + args);
            var detect = new Regex(@"(not-a-virus|HEUR:|Trojan|Backdoor|Worm|Virus\.|RiskTool|Miner|Exploit|Rootkit|Adware|PDM:|UDS:|detected|обнаруж|заражен|вылечен|удален|deleted|disinfected|quarantin|карантин)", RegexOptions.IgnoreCase);
            Process proc = null; var started = DateTime.Now; bool done = false;
            var watch = new Thread(() =>
            {
                while (!done)
                {
                    Thread.Sleep(1000);
                    progress("Kaspersky проверяет клон · прошло " + Fmt.Clock(DateTime.Now - started) + " · на большом диске это может занять час");
                    if (cancelled())
                    {
                        // KVRT перезапускает себя копией из %TEMP% — гасим всё дерево процессов, иначе копия продолжит проверку
                        try { if (proc != null && !proc.HasExited) Sh.Run("taskkill.exe", "/T /F /PID " + proc.Id, l => { }, Encoding.Default); } catch { }
                        try { foreach (var k in Process.GetProcessesByName("KVRT")) { try { k.Kill(); } catch { } } } catch { }
                    }
                }
            }) { IsBackground = true };
            watch.Start();
            int rc;
            try
            {
                rc = Sh.Run(exe, args, l =>
                {
                    if (string.IsNullOrWhiteSpace(l)) return;
                    string t = l.Trim(); Log.Write("kvrt> " + (t.Length > 300 ? t.Substring(0, 300) : t));
                    if (detect.IsMatch(t) && r.Detections.Count < 200) r.Detections.Add(t);
                }, Encoding.UTF8, p => proc = p);
            }
            finally { done = true; }
            Log.Write("kvrt: код выхода " + rc + ", " + Fmt.Clock(DateTime.Now - started));
            if (cancelled()) { r.Note = "отменено"; return r; }
            r.Ran = true;
            // карантин и отчёты: точный формат у Kaspersky не описан — считаем файлы в папках карантина и вытаскиваем находки из текстовых отчётов
            try
            {
                foreach (var q in Directory.GetDirectories(reportDir, "*", SearchOption.AllDirectories).Where(d => Regex.IsMatch(Path.GetFileName(d), "quarantine|карантин", RegexOptions.IgnoreCase)))
                    r.Quarantined += Directory.GetFiles(q, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
                foreach (var f in Directory.GetFiles(reportDir, "*.*", SearchOption.AllDirectories).Where(f => Regex.IsMatch(f, @"\.(txt|log|xml|htm|html|csv|json)$", RegexOptions.IgnoreCase) && !f.EndsWith("folders.txt")))
                {
                    if (new FileInfo(f).Length > 64 << 20) continue;
                    foreach (var line in File.ReadLines(f))
                        if (detect.IsMatch(line) && r.Detections.Count < 200) r.Detections.Add(line.Trim().Length > 300 ? line.Trim().Substring(0, 300) : line.Trim());
                }
            }
            catch { }
            if (rc != 0 && r.Detections.Count == 0 && r.Quarantined == 0) r.Note = "утилита завершилась с кодом " + rc;
            return r;
        }
    }
}
