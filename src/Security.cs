using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;

namespace Transfer
{
    /// <summary>Одна находка проверки на вирусы.</summary>
    class Threat
    {
        public int Level;          // 1 — подозрительно, 2 — опасно
        public string What = "";   // коротко, что это
        public string Path = "";   // файл / ключ / задача
        public string Why = "";    // почему так решили
        public string File = "";   // что можно удалить, чтобы обезвредить (файл программы или задачи)
        public override string ToString() { return (Level >= 2 ? "[ОПАСНО] " : "[ПОДОЗРИТЕЛЬНО] ") + What + (Path != "" ? " — " + Path : "") + (Why != "" ? " (" + Why + ")" : ""); }
    }

    class SecurityReport
    {
        public readonly List<Threat> Items = new List<Threat>();
        public bool DefenderUsed;           // проверял ли Защитник
        public string DefenderNote = "";    // что с Защитником
        public int Dangerous { get { return Items.Count(t => t.Level >= 2); } }
        public int Suspicious { get { return Items.Count(t => t.Level == 1); } }
        public string Verdict { get { return Dangerous > 0 ? "infected" : Suspicious > 0 ? "suspicious" : "clean"; } }

        public string ToText(string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title); sb.AppendLine(new string('=', Math.Min(78, title.Length))); sb.AppendLine("Дата: " + DateTime.Now.ToString("s"));
            sb.AppendLine("Защитник Windows: " + DefenderNote);
            sb.AppendLine("Итог: " + (Dangerous > 0 ? "НАЙДЕНЫ ВИРУСЫ / МАЙНЕРЫ (" + Dangerous + ")" : Suspicious > 0 ? "опасного нет, подозрительного: " + Suspicious : "чисто"));
            sb.AppendLine();
            foreach (var t in Items.OrderByDescending(x => x.Level)) sb.AppendLine(t.ToString());
            return sb.ToString();
        }
    }

    /// <summary>
    /// Проверка Windows на майнеры и типичные следы заражения. Работает и на запущенной системе (перед снятием клона),
    /// и на развёрнутой копии в папке (после разворачивания, до записи загрузчика). Если на компьютере работает
    /// Защитник Windows — сначала проверяет он, затем собственные эвристики: они нужны, потому что майнеры
    /// часто сами отключают Защитник или добавляют себя в его исключения.
    /// </summary>
    static class Security
    {
        // строки, по которым узнаются майнеры (XMRig и подобные); нужно совпадение минимум двух
        static readonly string[] MinerMarks = { "stratum+tcp", "stratum+ssl", "xmrig", "donate-level", "randomx", "cryptonight", "nicehash", "kawpow", "--cpu-max-threads-hint", "pool.minexmr", "moneroocean", "2miners.com", "nanopool.org" };
        // скачать из интернета и сразу выполнить
        static readonly Regex RemoteCode = new Regex(
            @"(\birm\b|\biwr\b|Invoke-RestMethod|Invoke-WebRequest|DownloadString|DownloadData|Net\.WebClient|Start-BitsTransfer)[^\r\n]{0,200}?(\|\s*(iex|Invoke-Expression)\b|;\s*(iex|Invoke-Expression)\b)" +
            @"|\b(iex|Invoke-Expression)\s*\(\s*(New-Object|\(New-Object|irm|iwr|Invoke-RestMethod|Invoke-WebRequest)" +
            @"|-e(nc|ncodedcommand)?\s+[A-Za-z0-9+/=]{60,}|\bmshta(\.exe)?\s+[""']?(https?:|vbscript:|javascript:)|certutil(\.exe)?\s+[^\r\n]*-urlcache",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // имена системных программ Windows — вне System32 это маскировка
        static readonly HashSet<string> SystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "svchost.exe", "csrss.exe", "lsass.exe", "winlogon.exe", "smss.exe", "services.exe", "wininit.exe", "spoolsv.exe", "dwm.exe",
            "taskhostw.exe", "dllhost.exe", "conhost.exe", "runtimebroker.exe", "sihost.exe", "ctfmon.exe", "audiodg.exe", "msmpeng.exe", "nissrv.exe", "searchindexer.exe", "wuauclt.exe" };
        // подмена букв в «системных» словах: lnspection (L вместо I), micros0ft, svch0st…
        static readonly Regex Homoglyph = new Regex(@"lnspection|lnstaller|rnicrosoft|micr0soft|micros0ft|mlcrosoft|wlndows|wind0ws|svch0st|scvhost|svhost|svchosts|csrs\.|lsas\.|expl0rer|explorer\s*\.exe\.|defendr|def3nder|updat3", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SystemLike = new Regex(@"microsoft|windows|defender|realtime|security|system|svchost|runtime|update|service|host", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly HashSet<string> Interpreters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "powershell.exe", "pwsh.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe", "msiexec.exe", "explorer.exe", "svchost.exe", "conhost.exe" };
        static readonly string[] SkipDirs = { "node_modules", ".git", "site-packages", "__pycache__", "\\packages\\", "\\user data\\", "\\profiles\\", "\\tdata", "\\cache", "\\code cache", "npm-cache", "\\pip\\", "\\conda", "\\temp\\claude", "\\windows defender\\", "\\microsoft\\windows\\wer\\", "\\steamapps\\", "\\.nuget\\", "\\.vscode\\extensions\\", "\\jetbrains\\" };
        const long MaxFile = 96L << 20, ReadBudget = 6L << 30;

        class Ctx
        {
            public string Root;          // «C:\» или папка развёрнутой копии
            public bool Live;            // это запущенная сейчас Windows
            public RegistryKey Soft, Sys, User;    // HKLM\SOFTWARE, HKLM\SYSTEM\<текущий набор>, HKCU (только для живой)
            public SecurityReport Rep = new SecurityReport();
            public Action<string> Progress = s => { };
            public Func<bool> Cancelled = () => false;
            public readonly Dictionary<string, string[]> MinerCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, bool> SigCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> ExcludedPaths = new List<string>();
            public long Read;
            public void Add(int level, string what, string path, string why, string file = null)
            {
                string key = level + "|" + what + "|" + path;
                if (!Reported.Add(key)) return;
                // одна и та же вещь могла найтись дважды — оставляем самый высокий уровень
                var same = Rep.Items.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) && path != "");
                if (same != null) { if (level > same.Level) { same.Level = level; same.What = what; same.Why = why; } if (!string.IsNullOrEmpty(file) && same.File == "") same.File = file; else if (!same.Why.Contains(why)) same.Why += "; " + why; return; }
                Rep.Items.Add(new Threat { Level = level, What = what, Path = path, Why = why, File = file ?? "" });
                Log.Write("security: " + Rep.Items.Last());
            }
        }

        /// <summary>Проверить Windows на диске root. live — это запущенная сейчас система (можно спросить реестр и Защитник напрямую).</summary>
        public static SecurityReport Scan(string root, bool live, Action<string> progress, Func<bool> cancelled)
        {
            var c = new Ctx { Root = root.TrimEnd('\\') + "\\", Live = live };
            if (progress != null) c.Progress = progress;
            if (cancelled != null) c.Cancelled = cancelled;
            string softHive = null, sysHive = null;
            try
            {
                if (live)
                {
                    c.Soft = Registry.LocalMachine.OpenSubKey("SOFTWARE");
                    c.Sys = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet");
                    c.User = Registry.CurrentUser;
                }
                else
                {
                    c.Progress("открываю реестр развёрнутой копии");
                    softHive = LoadHive(Path.Combine(c.Root, @"Windows\System32\config\SOFTWARE"), "XFER_SCAN_SOFT");
                    sysHive = LoadHive(Path.Combine(c.Root, @"Windows\System32\config\SYSTEM"), "XFER_SCAN_SYS");
                    if (softHive != null) c.Soft = Registry.LocalMachine.OpenSubKey(softHive);
                    if (sysHive != null)
                    {
                        int cur = 1;
                        try { using (var sel = Registry.LocalMachine.OpenSubKey(sysHive + @"\Select")) if (sel != null) cur = Convert.ToInt32(sel.GetValue("Current", 1)); } catch { }
                        c.Sys = Registry.LocalMachine.OpenSubKey(sysHive + @"\ControlSet" + cur.ToString("000"));
                    }
                }

                Step(c, "Защитник Windows", DefenderStatus);
                if (live) Step(c, "проверка Защитником", DefenderQuickScan);
                else Step(c, "проверка Защитником", DefenderFolderScan);
                Step(c, "исключения Защитника", DefenderExclusions);
                Step(c, "автозапуск", Autoruns);
                Step(c, "задачи планировщика", Tasks);
                Step(c, "службы", Services);
                Step(c, "брандмауэр", Firewall);
                Step(c, "файлы в папках, где обычно прячутся вирусы", Files);
            }
            finally
            {
                foreach (var k in new[] { c.Soft, c.Sys }) try { if (k != null) k.Close(); } catch { }
                c.Soft = c.Sys = null;
                if (softHive != null) UnloadHive(softHive);
                if (sysHive != null) UnloadHive(sysHive);
            }
            return c.Rep;
        }

        static void Step(Ctx c, string name, Action<Ctx> a)
        {
            if (c.Cancelled()) return;
            c.Progress(name);
            try { a(c); } catch (Exception e) { Log.Write("security: " + name + " — ошибка " + e.Message); }
        }

        // ------------------------------------------------------------------------------------------------ Защитник
        static string MpCmdRun()
        {
            string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
            try
            {
                string plat = @"C:\ProgramData\Microsoft\Windows Defender\Platform";
                if (Directory.Exists(plat))
                {
                    var best = Directory.GetDirectories(plat).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).Select(d => Path.Combine(d, "MpCmdRun.exe")).FirstOrDefault(File.Exists);
                    if (best != null) return best;
                }
            }
            catch { }
            return File.Exists(pf) ? pf : null;
        }

        static bool DefenderRunning() { try { return Process.GetProcessesByName("MsMpEng").Length > 0; } catch { return false; } }

        static void DefenderStatus(Ctx c)
        {
            var off = new List<string>();
            if (c.Soft != null)
            {
                using (var p = c.Soft.OpenSubKey(@"Policies\Microsoft\Windows Defender"))
                    if (p != null)
                    {
                        if (Convert.ToInt32(p.GetValue("DisableAntiSpyware", 0)) == 1) off.Add("политика DisableAntiSpyware");
                        if (Convert.ToInt32(p.GetValue("DisableAntiVirus", 0)) == 1) off.Add("политика DisableAntiVirus");
                        using (var rt = p.OpenSubKey("Real-Time Protection")) if (rt != null && Convert.ToInt32(rt.GetValue("DisableRealtimeMonitoring", 0)) == 1) off.Add("защита в реальном времени выключена политикой");
                    }
            }
            if (c.Sys != null) using (var s = c.Sys.OpenSubKey(@"Services\WinDefend")) if (s == null) off.Add("служба Защитника удалена");
            bool here = DefenderRunning() && MpCmdRun() != null;
            if (off.Count > 0)
            {
                c.Add(1, "Защитник Windows отключён в " + (c.Live ? "этой Windows" : "клоне"), "", string.Join(", ", off) + ". Так делают «оптимизаторы» Windows, но и майнеры тоже — без Защитника их никто не ловит");
            }
            c.Rep.DefenderNote = here ? "работает на этом компьютере" : "на этом компьютере не работает — проверка только встроенными правилами программы";
        }

        static void DefenderQuickScan(Ctx c)
        {
            string mp = MpCmdRun();
            if (mp == null || !DefenderRunning()) return;
            c.Progress("Защитник Windows проверяет систему (быструю проверку, несколько минут)");
            c.Rep.DefenderUsed = true;
            RunMp(c, mp, "-Scan -ScanType 1 -DisableRemediation");
        }

        static void DefenderFolderScan(Ctx c)
        {
            string mp = MpCmdRun();
            if (mp == null || !DefenderRunning()) return;
            c.Rep.DefenderUsed = true;
            var dirs = new List<string> { Path.Combine(c.Root, "ProgramData"), Path.Combine(c.Root, @"Windows\Temp"), Path.Combine(c.Root, "Users\\Public") };
            try { foreach (var u in Directory.GetDirectories(Path.Combine(c.Root, "Users"))) dirs.Add(Path.Combine(u, "AppData")); } catch { }
            foreach (var d in dirs.Where(Directory.Exists))
            {
                if (c.Cancelled()) return;
                c.Progress("Защитник Windows проверяет " + Fmt.ShortPath(d, 70));
                RunMp(c, mp, "-Scan -ScanType 3 -File \"" + d.TrimEnd('\\') + "\" -DisableRemediation");
            }
        }

        static void RunMp(Ctx c, string mp, string args)
        {
            string threat = null; var found = new List<string>();
            int rc = Sh.Run(mp, args, l =>
            {
                string t = (l ?? "").Trim();
                var m = Regex.Match(t, @"^Threat\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (m.Success) { threat = m.Groups[1].Value.Trim(); found.Add(threat); return; }
                var f = Regex.Match(t, @"^(file|regkey|process|service)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (f.Success && threat != null) c.Add(2, "Защитник Windows нашёл: " + threat, f.Groups[2].Value.Trim(), "угроза из базы Защитника");
            }, Sh.Oem, null, c.Cancelled);      // отмена и «Пропустить проверку» останавливают и Защитника
            Log.Write("security: MpCmdRun " + args + " -> " + rc);
            if (c.Cancelled()) return;
            if (rc == 2 && found.Count == 0) c.Add(2, "Защитник Windows нашёл угрозы", "", "подробности — в журнале защиты Windows");
            foreach (var t in found.Distinct()) if (!c.Rep.Items.Any(x => x.What.EndsWith(t))) c.Add(2, "Защитник Windows нашёл: " + t, "", "угроза из базы Защитника");
        }

        static void DefenderExclusions(Ctx c)
        {
            if (c.Soft == null) return;
            foreach (var basePath in new[] { @"Microsoft\Windows Defender\Exclusions", @"Policies\Microsoft\Windows Defender\Exclusions" })
            {
                using (var ex = c.Soft.OpenSubKey(basePath))
                {
                    if (ex == null) continue;
                    using (var k = ex.OpenSubKey("Processes")) if (k != null) foreach (var n in k.GetValueNames())
                    {
                        string leaf = SafeLeaf(n);
                        if (Interpreters.Contains(leaf)) c.Add(2, "Защитнику запрещено проверять " + leaf, "исключение Защитника: " + n, "так вирусы прячут свои скрипты: всё, что запускается через " + leaf + ", Защитник пропускает");
                        else { string p = Map(c, n); if (IsRisky(c, p)) c.Add(1, "Исключение Защитника для программы в подозрительной папке", n, "Защитник её не проверяет"); }
                    }
                    using (var k = ex.OpenSubKey("Extensions")) if (k != null) foreach (var n in k.GetValueNames())
                        if (Regex.IsMatch(n.TrimStart('.', '*'), @"^(exe|dll|ps1|bat|cmd|vbs|js|scr|com|msi|sys)$", RegexOptions.IgnoreCase))
                            c.Add(2, "Защитнику запрещено проверять все файлы *." + n.TrimStart('.', '*'), "исключение Защитника: " + n, "программы этого типа Защитник не проверяет вообще");
                    using (var k = ex.OpenSubKey("Paths")) if (k != null) foreach (var n in k.GetValueNames())
                    {
                        string p = Map(c, n).TrimEnd('\\');
                        string rel = Rel(c, p).TrimEnd('\\').ToLowerInvariant();
                        c.ExcludedPaths.Add(p);
                        if (rel == "" || Regex.IsMatch(rel, @"^(windows|windows\\system32|programdata|users|program files|program files \(x86\)|users\\[^\\]+|users\\[^\\]+\\appdata(\\(local|roaming))?|users\\[^\\]+\\downloads|windows\\temp|users\\[^\\]+\\appdata\\local\\temp)$"))
                            c.Add(2, "Защитнику запрещено проверять целую системную папку", n, "в такой папке вирус может лежать незаметно");
                        else if (Directory.Exists(p))
                        {
                            var bad = SafeFiles(p, "*.exe", 2).Where(f => !Signed(c, f)).Take(3).ToList();
                            if (bad.Count > 0 && IsRisky(c, p))
                                c.Add(2, "Неподписанная программа спрятана от Защитника", bad[0], "её папка " + n + " добавлена в исключения Защитника, а лежит она там, где обычно прячутся вирусы", bad[0]);
                            else if (bad.Count > 0) c.Add(1, "Исключение Защитника для папки с неподписанными программами", n, "Защитник не проверяет: " + string.Join(", ", bad.Select(System.IO.Path.GetFileName)));
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ автозапуск
        static void Autoruns(Ctx c)
        {
            var runs = new List<KeyValuePair<string, string>>();
            Action<RegistryKey, string, string> read = (hive, sub, label) =>
            {
                if (hive == null) return;
                using (var k = hive.OpenSubKey(sub)) if (k != null) foreach (var n in k.GetValueNames()) runs.Add(new KeyValuePair<string, string>(label + " «" + n + "»", Convert.ToString(k.GetValue(n, "", RegistryValueOptions.None))));
            };
            foreach (var sub in new[] { @"Microsoft\Windows\CurrentVersion\Run", @"Microsoft\Windows\CurrentVersion\RunOnce", @"WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", @"Microsoft\Windows\CurrentVersion\Policies\Explorer\Run" })
                read(c.Soft, sub, "автозапуск HKLM\\" + sub.Replace(@"Microsoft\Windows\CurrentVersion\", ""));
            if (c.User != null) foreach (var sub in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
                read(c.User, sub, "автозапуск пользователя " + sub.Substring(sub.LastIndexOf('\\') + 1));
            if (c.Soft != null)
            {
                using (var w = c.Soft.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion\Winlogon"))
                    if (w != null)
                    {
                        string shell = Convert.ToString(w.GetValue("Shell", "explorer.exe")), ui = Convert.ToString(w.GetValue("Userinit", ""));
                        if (!Regex.IsMatch(shell.Trim(), @"^explorer\.exe$", RegexOptions.IgnoreCase)) runs.Add(new KeyValuePair<string, string>("оболочка Winlogon\\Shell", shell));
                        foreach (var part in ui.Split(',').Select(s => s.Trim()).Where(s => s != "" && !s.EndsWith("userinit.exe", StringComparison.OrdinalIgnoreCase)))
                            runs.Add(new KeyValuePair<string, string>("Winlogon\\Userinit", part));
                    }
                using (var ifeo = c.Soft.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion\Image File Execution Options"))
                    if (ifeo != null) foreach (var n in ifeo.GetSubKeyNames())
                        using (var k = ifeo.OpenSubKey(n))
                        {
                            string dbg = k == null ? null : k.GetValue("Debugger") as string;
                            if (string.IsNullOrEmpty(dbg) || Regex.IsMatch(dbg, @"taskkill\.exe|vsjitdebugger|^""?%windir%\\System32\\", RegexOptions.IgnoreCase)) continue;
                            runs.Add(new KeyValuePair<string, string>("перехват запуска " + n + " (IFEO Debugger)", dbg));
                        }
            }
            // папки «Автозагрузка» всех пользователей
            var startDirs = new List<string> { Path.Combine(c.Root, @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp") };
            try { foreach (var u in Directory.GetDirectories(Path.Combine(c.Root, "Users"))) startDirs.Add(Path.Combine(u, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup")); } catch { }
            foreach (var d in startDirs.Where(Directory.Exists))
                foreach (var f in SafeFiles(d, "*", 0))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".ini") continue;
                    if (ext == ".lnk") { string t = LnkTarget(f); runs.Add(new KeyValuePair<string, string>("папка Автозагрузка: " + Path.GetFileName(f), t ?? f)); }
                    else runs.Add(new KeyValuePair<string, string>("папка Автозагрузка: " + Path.GetFileName(f), "\"" + f + "\""));
                }
            foreach (var r in runs) CheckCommand(c, r.Key, r.Value);
        }

        static void Tasks(Ctx c)
        {
            string dir = Path.Combine(c.Root, @"Windows\System32\Tasks");
            if (!Directory.Exists(dir)) return;
            foreach (var f in SafeFiles(dir, "*", 12))
            {
                if (c.Cancelled()) return;
                string xml; try { xml = File.ReadAllText(f); } catch { continue; }
                if (xml.IndexOf("<Exec", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string name = f.Substring(dir.Length);
                try
                {
                    var doc = new XmlDocument(); doc.LoadXml(xml);
                    var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                    foreach (XmlNode ex in doc.SelectNodes("//t:Exec", ns))
                    {
                        string cmd = (ex.SelectSingleNode("t:Command", ns) ?? doc.CreateElement("x")).InnerText.Trim();
                        string args = (ex.SelectSingleNode("t:Arguments", ns) ?? doc.CreateElement("x")).InnerText.Trim();
                        string wd = (ex.SelectSingleNode("t:WorkingDirectory", ns) ?? doc.CreateElement("x")).InnerText.Trim();
                        string q = cmd.StartsWith("\"") ? cmd : "\"" + cmd + "\"";
                        CheckCommand(c, "задача планировщика " + name, q + " " + args, wd, f);
                    }
                }
                catch (XmlException) { if (RemoteCode.IsMatch(xml)) c.Add(2, "Задача скачивает и запускает код из интернета", "задача планировщика " + name, "при каждом срабатывании выполняет то, что лежит на чужом сайте", f); }
            }
        }

        static void Services(Ctx c)
        {
            if (c.Sys == null) return;
            using (var svcs = c.Sys.OpenSubKey("Services"))
            {
                if (svcs == null) return;
                foreach (var n in svcs.GetSubKeyNames())
                {
                    if (c.Cancelled()) return;
                    try
                    {
                        using (var k = svcs.OpenSubKey(n))
                        {
                            if (k == null) continue;
                            int start = Convert.ToInt32(k.GetValue("Start", 4)); if (start > 2) continue;     // только то, что запускается само
                            string img = Convert.ToString(k.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                            if (img == "") continue;
                            CheckCommand(c, "служба " + n, img);
                        }
                    }
                    catch { }
                }
            }
        }

        static void Firewall(Ctx c)
        {
            if (c.Sys == null) return;
            using (var k = c.Sys.OpenSubKey(@"Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"))
            {
                if (k == null) return;
                foreach (var id in k.GetValueNames())
                {
                    string v = Convert.ToString(k.GetValue(id, ""));
                    var app = Regex.Match(v, @"\|App=([^|]+)"); if (!app.Success) continue;
                    if (v.IndexOf("Action=Allow", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var nm = Regex.Match(v, @"\|Name=([^|]*)"); string name = nm.Success ? nm.Groups[1].Value : "";
                    bool fakeName = Regex.IsMatch(name, @"^(Core Networking|Windows|Microsoft|Network Discovery|File and Printer)", RegexOptions.IgnoreCase) || v.IndexOf("EmbedCtxt=Core Networking", StringComparison.OrdinalIgnoreCase) >= 0;
                    string p = Map(c, app.Groups[1].Value);
                    if (!IsRisky(c, p)) continue;
                    if (fakeName) c.Add(2, "Правило брандмауэра выдаёт себя за системное", p, "называется «" + name + "», но разрешает сеть программе из " + Rel(c, Path.GetDirectoryName(p)));
                    else if (File.Exists(p) && Miner(c, p).Length >= 2) c.Add(2, "Майнеру открыт доступ в сеть", p, "правило брандмауэра «" + name + "»", p);
                }
            }
        }

        /// <summary>Проверка одной команды автозапуска.</summary>
        static void CheckCommand(Ctx c, string where, string command, string workDir = null, string taskFile = null)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            if (RemoteCode.IsMatch(command))
            {
                var u = Regex.Match(command, @"https?://[^\s'""|;)]+");
                c.Add(2, "Скачивает и запускает код из интернета", where, (u.Success ? "адрес " + u.Value + ". " : "") + "при каждом запуске выполняет то, что сейчас лежит на чужом сайте — туда можно подложить что угодно", taskFile);
                return;
            }
            string exe = ExeOf(command);
            if (exe == null) return;
            string p = Map(c, exe);
            if (!Path.IsPathRooted(p) && !string.IsNullOrEmpty(workDir)) p = Path.Combine(Map(c, workDir), p);
            if (!Path.IsPathRooted(p)) return;
            if (!File.Exists(p)) return;
            string leaf = Path.GetFileName(p);
            // скрипт через интерпретатор: смотрим и сам скрипт
            if (Interpreters.Contains(leaf))
            {
                var sm = Regex.Match(command, @"[""']?([A-Za-z]:\\[^""'|]+?\.(ps1|vbs|js|bat|cmd|wsf|hta))[""']?", RegexOptions.IgnoreCase);
                if (sm.Success)
                {
                    string sp = Map(c, sm.Groups[1].Value);
                    if (File.Exists(sp)) CheckScript(c, where, sp, taskFile);
                }
                return;
            }
            bool risky = IsRisky(c, p);
            string[] marks = risky || !Signed(c, p) ? Miner(c, p) : new string[0];
            if (marks.Length >= 2) { c.Add(2, "Майнер в автозапуске", p, where + "; внутри: " + string.Join(", ", marks), p); return; }
            if (!risky) return;
            if (Signed(c, p)) return;
            bool excluded = c.ExcludedPaths.Any(e => p.StartsWith(e.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(p, e, StringComparison.OrdinalIgnoreCase));
            bool fake = SystemNames.Contains(leaf) || Homoglyph.IsMatch(leaf) || Homoglyph.IsMatch(p);
            // что лежит рядом: майнер часто разделён на загрузчик и сам майнер
            var near = SafeFiles(Path.GetDirectoryName(p), "*.exe", 1).Where(f => !string.Equals(f, p, StringComparison.OrdinalIgnoreCase)).ToList();
            var nearMiner = near.FirstOrDefault(f => !Signed(c, f) && Miner(c, f).Length >= 2);
            if (nearMiner != null) { c.Add(2, "Загрузчик майнера в автозапуске", p, where + "; рядом лежит майнер " + Path.GetFileName(nearMiner), p); c.Add(2, "Майнер", nearMiner, "внутри: " + string.Join(", ", Miner(c, nearMiner)), nearMiner); return; }
            if (excluded) { c.Add(2, "Неподписанная программа в автозапуске спрятана от Защитника", p, where + "; её папка в исключениях Защитника", p); return; }
            if (fake) { c.Add(2, "Программа выдаёт себя за системную", p, where + "; имя подделано под файл Windows", p); return; }
            c.Add(1, "Неподписанная программа в автозапуске", p, where + "; лежит в " + Rel(c, Path.GetDirectoryName(p)) + " — проверь, знаешь ли ты её");
        }

        static void CheckScript(Ctx c, string where, string path, string taskFile)
        {
            try
            {
                var fi = new FileInfo(path); if (fi.Length > (2 << 20)) return;
                string s = File.ReadAllText(path);
                if (RemoteCode.IsMatch(s)) c.Add(2, "Скрипт в автозапуске скачивает и запускает код из интернета", path, where, taskFile ?? path);
                else if (MinerMarks.Count(m => s.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) >= 2) c.Add(2, "Скрипт в автозапуске запускает майнер", path, where, taskFile ?? path);
            }
            catch { }
        }

        // ------------------------------------------------------------------------------------------------ файлы
        static void Files(Ctx c)
        {
            var roots = new List<KeyValuePair<string, int>> {
                new KeyValuePair<string, int>(Path.Combine(c.Root, "ProgramData"), 4),
                new KeyValuePair<string, int>(Path.Combine(c.Root, "Users\\Public"), 4),
                new KeyValuePair<string, int>(Path.Combine(c.Root, @"Windows\Temp"), 3),
                new KeyValuePair<string, int>(Path.Combine(c.Root, @"Windows\Fonts"), 0),
                new KeyValuePair<string, int>(Path.Combine(c.Root, @"Windows\Tasks"), 2),
                new KeyValuePair<string, int>(Path.Combine(c.Root, @"Windows\Debug"), 2),
                new KeyValuePair<string, int>(Path.Combine(c.Root, "PerfLogs"), 3) };
            try
            {
                foreach (var u in Directory.GetDirectories(Path.Combine(c.Root, "Users")))
                {
                    roots.Add(new KeyValuePair<string, int>(Path.Combine(u, @"AppData\Roaming"), 4));
                    roots.Add(new KeyValuePair<string, int>(Path.Combine(u, @"AppData\Local"), 3));
                    roots.Add(new KeyValuePair<string, int>(Path.Combine(u, @"AppData\LocalLow"), 3));
                }
            }
            catch { }
            foreach (var r in roots.Where(x => Directory.Exists(x.Key)))
            {
                foreach (var f in SafeFiles(r.Key, "*", r.Value))
                {
                    if (c.Cancelled()) return;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".exe" && ext != ".dll" && ext != ".scr" && ext != ".com" && ext != ".pif") continue;
                    string leaf = Path.GetFileName(f);
                    bool fakeName = SystemNames.Contains(leaf) || Homoglyph.IsMatch(leaf);
                    long len; try { len = new FileInfo(f).Length; } catch { continue; }
                    if (len < 32 << 10 || len > MaxFile) { if (fakeName && !Signed(c, f)) c.Add(2, "Программа выдаёт себя за системную", f, "имя подделано под файл Windows, подписи нет", f); continue; }
                    if (Signed(c, f)) continue;
                    c.Progress("файлы: " + Fmt.ShortPath(f, 80));
                    var marks = Miner(c, f);
                    if (marks.Length >= 2) { c.Add(2, "Майнер", f, "внутри: " + string.Join(", ", marks), f); continue; }
                    if (fakeName) c.Add(2, "Программа выдаёт себя за системную", f, "имя подделано под файл Windows, подписи нет", f);
                    else if (ext == ".exe" && SystemLike.IsMatch(Path.GetFileNameWithoutExtension(leaf)) && leaf.Contains(" ") && f.StartsWith(Path.Combine(c.Root, "ProgramData"), StringComparison.OrdinalIgnoreCase))
                        c.Add(1, "Неподписанная программа с «системным» названием", f, "настоящие программы Microsoft подписаны");
                }
            }
        }

        static string[] Miner(Ctx c, string path)
        {
            string[] r;
            if (c.MinerCache.TryGetValue(path, out r)) return r;
            r = new string[0];
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length <= MaxFile && c.Read + fi.Length <= ReadBudget)
                {
                    c.Read += fi.Length;
                    byte[] b = File.ReadAllBytes(path);
                    r = MinerMarks.Where(m => IndexOf(b, Encoding.ASCII.GetBytes(m)) >= 0 || IndexOf(b, Encoding.Unicode.GetBytes(m)) >= 0).ToArray();
                }
            }
            catch { }
            c.MinerCache[path] = r;
            return r;
        }

        /// <summary>Поиск подстроки в байтах без учёта регистра латиницы.</summary>
        static int IndexOf(byte[] hay, byte[] needle)
        {
            if (needle.Length == 0 || hay.Length < needle.Length) return -1;
            byte f0 = Lower(needle[0]);
            int last = hay.Length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                if (Lower(hay[i]) != f0) continue;
                int j = 1;
                while (j < needle.Length && Lower(hay[i + j]) == Lower(needle[j])) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
        static byte Lower(byte b) { return b >= (byte)'A' && b <= (byte)'Z' ? (byte)(b + 32) : b; }

        // ------------------------------------------------------------------------------------------------ пути
        static bool IsRisky(Ctx c, string p)
        {
            string rel = Rel(c, p).ToLowerInvariant();
            if (rel.StartsWith("programdata\\") && !rel.StartsWith(@"programdata\microsoft\windows defender\") && !rel.StartsWith(@"programdata\package cache\")) return true;
            if (Regex.IsMatch(rel, @"^users\\[^\\]+\\appdata\\") && !Regex.IsMatch(rel, @"^users\\[^\\]+\\appdata\\local\\microsoft\\(windowsapps|onedrive|teams)\\")) return true;
            return rel.StartsWith(@"users\public\") || rel.StartsWith(@"windows\temp\") || rel.StartsWith(@"windows\fonts\") || rel.StartsWith(@"windows\tasks\") ||
                   rel.StartsWith(@"windows\debug\") || rel.StartsWith("perflogs\\") || rel.StartsWith("$recycle.bin\\") || rel.Contains("\\temp\\");
        }

        static string Rel(Ctx c, string p)
        {
            if (p == null) return "";
            return p.StartsWith(c.Root, StringComparison.OrdinalIgnoreCase) ? p.Substring(c.Root.Length) : p.Length > 3 && p[1] == ':' ? p.Substring(3) : p;
        }

        /// <summary>Путь из реестра/задачи → настоящий путь на проверяемом диске.</summary>
        static string Map(Ctx c, string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.Trim().Trim('"');
            if (p.StartsWith(@"\??\")) p = p.Substring(4);
            if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = "%SystemRoot%\\" + p.Substring(12);
            if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase)) p = "%SystemRoot%\\" + p;
            string win = Path.Combine(c.Root, "Windows");
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "SystemRoot", win }, { "windir", win }, { "SystemDrive", c.Root.TrimEnd('\\') }, { "ProgramData", Path.Combine(c.Root, "ProgramData") }, { "ALLUSERSPROFILE", Path.Combine(c.Root, "ProgramData") },
                { "ProgramFiles", Path.Combine(c.Root, "Program Files") }, { "ProgramW6432", Path.Combine(c.Root, "Program Files") }, { "ProgramFiles(x86)", Path.Combine(c.Root, "Program Files (x86)") },
                { "CommonProgramFiles", Path.Combine(c.Root, @"Program Files\Common Files") }, { "CommonProgramFiles(x86)", Path.Combine(c.Root, @"Program Files (x86)\Common Files") }, { "PUBLIC", Path.Combine(c.Root, @"Users\Public") } };
            p = Regex.Replace(p, @"%([^%]+)%", m =>
            {
                string v;
                if (env.TryGetValue(m.Groups[1].Value, out v)) return v;
                if (c.Live) { string e = Environment.GetEnvironmentVariable(m.Groups[1].Value); if (!string.IsNullOrEmpty(e)) return e; }
                return m.Value;
            });
            if (!c.Live && p.Length > 2 && p[1] == ':' && !p.StartsWith(c.Root, StringComparison.OrdinalIgnoreCase)) p = Path.Combine(c.Root, p.Substring(3));
            return p;
        }

        static string ExeOf(string cmd)
        {
            cmd = cmd.Trim();
            if (cmd.StartsWith("\"")) { int e = cmd.IndexOf('"', 1); return e > 1 ? cmd.Substring(1, e - 1) : null; }
            var m = Regex.Match(cmd, @"^(.+?\.(exe|com|scr|bat|cmd|dll|sys|pif))(\s|,|$)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            return cmd.Split(' ')[0];
        }

        static string SafeLeaf(string p) { try { return Path.GetFileName(p.Trim().Trim('"')); } catch { return p; } }

        static IEnumerable<string> SafeFiles(string dir, string mask, int depth)
        {
            var stack = new Stack<KeyValuePair<string, int>>(); stack.Push(new KeyValuePair<string, int>(dir, 0));
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                string low = cur.Key.ToLowerInvariant() + "\\";
                if (cur.Value > 0 && SkipDirs.Any(s => low.Contains(s))) continue;
                string[] files = new string[0], dirs = new string[0];
                try
                {
                    var di = new DirectoryInfo(cur.Key);
                    if (cur.Value > 0 && (di.Attributes & FileAttributes.ReparsePoint) != 0) continue;     // ссылки не обходим — можно зациклиться
                    files = Directory.GetFiles(cur.Key, mask);
                    if (cur.Value < depth) dirs = Directory.GetDirectories(cur.Key);
                }
                catch { }
                foreach (var f in files) yield return f;
                foreach (var d in dirs) stack.Push(new KeyValuePair<string, int>(d, cur.Value + 1));
            }
        }

        static string LnkTarget(string lnk)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell"); if (t == null) return null;
                object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                var st = sc.GetType();
                string target = st.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                string args = st.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                return string.IsNullOrEmpty(target) ? null : "\"" + target + "\" " + args;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------------------------------------ подпись файла
        /// <summary>У файла есть действительная встроенная цифровая подпись.</summary>
        public static bool HasValidSignature(string path) { return Trust.Valid(path); }

        static bool Signed(Ctx c, string path)
        {
            bool r;
            if (c.SigCache.TryGetValue(path, out r)) return r;
            r = Trust.Valid(path);
            c.SigCache[path] = r;
            return r;
        }

        static class Trust
        {
            [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
            static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct FileInfoW { public uint cbStruct; [MarshalAs(UnmanagedType.LPWStr)] public string path; public IntPtr hFile; public IntPtr known; }

            [StructLayout(LayoutKind.Sequential)]
            struct Data
            {
                public uint cbStruct; public IntPtr policy; public IntPtr sip; public uint ui; public uint revocation; public uint union; public IntPtr file;
                public uint stateAction; public IntPtr state; public IntPtr url; public uint provFlags; public uint uiContext; public IntPtr sigSettings;
            }

            static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            /// <summary>У файла есть действительная цифровая подпись (встроенная). Без обращения в интернет.</summary>
            public static bool Valid(string path)
            {
                IntPtr pf = IntPtr.Zero, pd = IntPtr.Zero;
                try
                {
                    var fi = new FileInfoW { cbStruct = (uint)Marshal.SizeOf(typeof(FileInfoW)), path = path };
                    pf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfoW))); Marshal.StructureToPtr(fi, pf, false);
                    var d = new Data { cbStruct = (uint)Marshal.SizeOf(typeof(Data)), ui = 2 /*NONE*/, revocation = 0 /*NONE*/, union = 1 /*FILE*/, file = pf, stateAction = 0, provFlags = 0x1000 /*CACHE_ONLY_URL_RETRIEVAL*/ | 0x80 /*REVOCATION_CHECK_NONE*/ };
                    pd = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Data))); Marshal.StructureToPtr(d, pd, false);
                    return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, pd) == 0;
                }
                catch { return false; }
                finally
                {
                    if (pf != IntPtr.Zero) { Marshal.DestroyStructure(pf, typeof(FileInfoW)); Marshal.FreeHGlobal(pf); }
                    if (pd != IntPtr.Zero) Marshal.FreeHGlobal(pd);
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ реестр клона
        static string LoadHive(string file, string name)
        {
            if (!File.Exists(file)) return null;
            UnloadHive(name);
            int rc = Sh.Run("reg.exe", "load HKLM\\" + name + " \"" + file + "\"", l => { }, Sh.Oem);
            Log.Write("security: reg load " + name + " -> " + rc);
            return rc == 0 ? name : null;
        }

        static void UnloadHive(string name)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            for (int i = 0; i < 5; i++)
            {
                using (var k = Registry.LocalMachine.OpenSubKey(name)) if (k == null) return;
                Sh.Run("reg.exe", "unload HKLM\\" + name, l => { }, Sh.Oem);
                System.Threading.Thread.Sleep(300);
            }
        }
    }
}
