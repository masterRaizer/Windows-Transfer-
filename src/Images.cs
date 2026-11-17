using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Transfer
{
    /// <summary>Windows, найденная на одном из дисков этого компьютера.</summary>
    class WinInstall
    {
        public string Root;        // «E:\»
        public string Name;        // «Windows 10 Home 22H2 (сборка 19045)»
        public bool Current;       // это запущенная сейчас система
        public long Used;

        public string Product = "", Version = "", Build = "", Custom = "";

        /// <summary>«версия 24H2, сборка 26100.2894» — строка под названием.</summary>
        public string VersionLine
        {
            get { return ((Version != "" ? "версия " + Version : "") + (Version != "" && Build != "" ? ", " : "") + (Build != "" ? "сборка " + Build : "")).Trim(); }
        }

        static string Val(RegistryKey k, string name) { return k == null ? "" : Convert.ToString(k.GetValue(name) ?? "").Trim(); }

        /// <summary>
        /// Название, версия, сборка и — если это переделанная сборка (ReviOS, AtlasOS, Tiny11, Ghost Spectre и подобные) — её имя.
        /// Авторы таких сборок подписывают их в реестре: в полях EditionSub*, в «организации» владельца или в сведениях об изготовителе.
        /// </summary>
        void Fill(RegistryKey cv, RegistryKey oem)
        {
            Product = Val(cv, "ProductName"); Version = Val(cv, "DisplayVersion"); if (Version == "") Version = Val(cv, "ReleaseId");
            string build = Val(cv, "CurrentBuild"), ubr = Val(cv, "UBR");
            Build = build + (ubr != "" && ubr != "0" ? "." + ubr : "");
            int b; if (int.TryParse(build, out b) && b >= 22000) Product = Product.Replace("Windows 10", "Windows 11");   // Windows 11 в реестре называет себя «Windows 10»

            string subVer = Val(cv, "EditionSubVersion"), subStr = Val(cv, "EditionSubstring"), subMan = Val(cv, "EditionSubManufacturer");
            string org = Val(cv, "RegisteredOrganization"), oemModel = Val(oem, "Model"), oemMan = Val(oem, "Manufacturer");
            var known = new System.Text.RegularExpressions.Regex(@"revi|atlas|tiny\s*1[01]|ghost|spectre|x-?lite|liteos|\bame\b|arcos|kernelos|flibustier|lopatkin|smokie|zosma", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool marked = (subVer + subStr + subMan) != "" && !(subVer + subStr + subMan).ToLowerInvariant().Contains("microsoft");
            string key = subVer != "" ? subVer : subStr;
            if (marked)
            {
                // самое полное имя — то, где есть и название, и номер сборки автора («ReviOS 11 25.10»)
                if (key != "" && org.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) Custom = org;
                else if (key != "" && oemModel.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) Custom = oemModel;
                else Custom = (key + (subMan != "" && subMan.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0 ? " (" + subMan + ")" : "")).Trim();
            }
            else if (known.IsMatch(oemModel)) Custom = oemModel;
            else if (known.IsMatch(org)) Custom = org;
            else if (known.IsMatch(oemMan)) Custom = oemMan;

            Name = (Product + (Version != "" ? " " + Version : "")).Trim() + (Custom != "" ? " · " + Custom : "");
            if (Name == "") Name = null;
        }

        void FillOffline()
        {
            string hive = Path.Combine(Root, @"Windows\System32\config\SOFTWARE"), key = "XFERW" + Guid.NewGuid().ToString("N").Substring(0, 6);
            if (Sh.Run("reg.exe", "load HKLM\\" + key + " \"" + hive + "\"", null, null) != 0) return;
            try
            {
                using (var cv = Registry.LocalMachine.OpenSubKey(key + @"\Microsoft\Windows NT\CurrentVersion"))
                using (var oem = Registry.LocalMachine.OpenSubKey(key + @"\Microsoft\Windows\CurrentVersion\OEMInformation"))
                    Fill(cv, oem);
            }
            catch { }
            finally { GC.Collect(); GC.WaitForPendingFinalizers(); Sh.Run("reg.exe", "unload HKLM\\" + key, null, null); }
        }

        /// <summary>Все установленные Windows на встроенных дисках (кроме диска переноса).</summary>
        public static List<WinInstall> Find(string transferRoot)
        {
            var list = new List<WinInstall>();
            string sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").Substring(0, 2);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string l = d.Name.Substring(0, 2);
                    if (transferRoot != null && transferRoot.StartsWith(l, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(d.Name + @"Windows\System32\ntoskrnl.exe") || !File.Exists(d.Name + @"Windows\System32\config\SOFTWARE")) continue;
                    var w = new WinInstall { Root = d.Name, Current = l.Equals(sys, StringComparison.OrdinalIgnoreCase), Used = d.TotalSize - d.AvailableFreeSpace };
                    if (w.Current)
                        using (var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                        using (var oem = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation"))
                            w.Fill(cv, oem);
                    else w.FillOffline();
                    if (string.IsNullOrEmpty(w.Name))
                    {
                        try { w.Name = "Windows (сборка " + FileVersionInfo.GetVersionInfo(d.Name + @"Windows\System32\ntoskrnl.exe").FileBuildPart + ")"; } catch { w.Name = "Windows"; }
                    }
                    list.Add(w);
                }
                catch { }
            }
            return list.OrderByDescending(w => w.Current).ThenBy(w => w.Root).ToList();
        }
    }

    /// <summary>Образ, который можно развернуть: снятый клон или шаблон (чистый образ Windows).</summary>
    class ImageRef
    {
        public string Id;            // папка клона в images\
        public string TemplateRel;   // для шаблона: путь к файлу относительно папки переноса
        public int Index = 1;
        public string Title = "", Sub = "", Label = "";
        public long ImageBytes, WimBytes;
        public string Computer = "", Version = "";
        public HwProfile SrcHw;      // железо компьютера, с которого снят клон
        public string OsName = "";
        public bool IsTemplate { get { return TemplateRel != null; } }
    }

    static class ImageLib
    {
        public static string ImagesDir(string root) { return Path.Combine(root, "images"); }
        public static string TemplatesDir(string root) { return Path.Combine(root, "templates"); }

        public static string Ps(string script)
        {
            var sb = new StringBuilder();
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8;try{" + script + "}catch{'ERR:'+$_.Exception.Message}"));
            Sh.Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, l => { lock (sb) sb.AppendLine(l); }, Encoding.UTF8);
            return sb.ToString().Trim();
        }

        /// <summary>Варианты (редакции) внутри файла образа: номер, название, размер после распаковки.</summary>
        public static List<Dictionary<string, object>> TemplateInfo(string file)
        {
            string cache = file + ".info.json";
            try
            {
                if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(file))
                {
                    var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var c = js.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(cache, Encoding.UTF8));
                    if (c != null && c.Count > 0) return c;
                }
            }
            catch { }
            string info = "";
            Sh.Run("dism.exe", "/English /Get-ImageInfo /ImageFile:\"" + file + "\"", l => info += l + "\n", Sh.Oem);
            var res = new List<Dictionary<string, object>>();
            foreach (Match m in Regex.Matches(info, @"Index\s*:\s*(\d+)\s*\n\s*Name\s*:\s*(.*?)\s*\n\s*Description\s*:.*?\n\s*Size\s*:\s*([^\n]*?)\s*bytes", RegexOptions.Singleline))
            {
                long size; long.TryParse(Regex.Replace(m.Groups[3].Value, @"[^\d]", ""), out size);
                res.Add(new Dictionary<string, object> { { "Index", int.Parse(m.Groups[1].Value) }, { "Name", m.Groups[2].Value.Trim() }, { "Bytes", size } });
            }
            try { if (res.Count > 0) Json.WriteFile(cache, res); } catch { }
            return res;
        }

        /// <summary>Все образы на диске переноса: сначала клоны (новые сверху), потом шаблоны.</summary>
        public static List<ImageRef> List(string root)
        {
            var list = new List<ImageRef>();
            try
            {
                if (Directory.Exists(ImagesDir(root)))
                    foreach (var dir in Directory.GetDirectories(ImagesDir(root)).OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)))
                    {
                        var m = Json.ReadFile(Path.Combine(dir, "clone.json"));
                        if (m == null || !File.Exists(Path.Combine(dir, "windows.wim"))) continue;
                        var r = new ImageRef { Id = Path.GetFileName(dir), Computer = Json.Str(m, "Computer"), Version = (Json.Str(m, "Version") + " " + Json.Str(m, "Edition")).Trim() };
                        long.TryParse(Json.Str(m, "ImageBytes"), out r.ImageBytes); long.TryParse(Json.Str(m, "WimBytes"), out r.WimBytes);
                        DateTime dt; string when = DateTime.TryParse(Json.Str(m, "Date"), out dt) ? dt.ToString("dd.MM.yyyy HH:mm") : "";
                        string os = Json.Str(m, "Name"); if (os == "") os = "Windows " + r.Version;
                        r.Title = "Клон: " + os; r.Label = "клон с " + r.Computer; r.OsName = os;
                        object hwo; if (m.TryGetValue("Hw", out hwo)) r.SrcHw = HwProfile.FromDict(hwo);
                        string vl = Json.Str(m, "VersionLine");
                        r.Sub = (vl != "" ? vl + " · " : "") + "с компьютера " + r.Computer + ", диск " + Json.Str(m, "Source").TrimEnd('\\') + " · снят " + when + " · файл " + Fmt.Size(r.WimBytes) + " · развернётся в " + Fmt.Size(r.ImageBytes);
                        list.Add(r);
                    }
            }
            catch { }
            try
            {
                if (Directory.Exists(TemplatesDir(root)))
                    foreach (var f in Directory.GetFiles(TemplatesDir(root)).Where(x => x.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x))
                    {
                        long fl = new FileInfo(f).Length;
                        foreach (var e in TemplateInfo(f))
                        {
                            var r = new ImageRef { TemplateRel = Path.Combine("templates", Path.GetFileName(f)), WimBytes = fl };
                            r.Index = Convert.ToInt32(e["Index"]); long.TryParse(Convert.ToString(e["Bytes"]), out r.ImageBytes);
                            string name = Convert.ToString(e["Name"]);
                            r.Title = "Шаблон: " + name; r.Label = "шаблон " + name; r.Version = name; r.OsName = name;
                            r.Sub = "чистая Windows из файла " + Path.GetFileName(f) + " (" + Fmt.Size(fl) + ") · развернётся в " + Fmt.Size(r.ImageBytes) + " · при первом запуске пройдёт начальную настройку";
                            list.Add(r);
                        }
                    }
            }
            catch { }
            return list;
        }
    }

    // ============================================================================================ ДОБАВИТЬ ШАБЛОН
    /// <summary>Кладёт образ Windows (из ISO, WIM или ESD) в папку шаблонов на диске переноса.</summary>
    class TemplateImportFlow : Flow
    {
        public string SourceFile;

        public TemplateImportFlow(string root, string file) { Root = root; SourceFile = file; Title = "Добавление шаблона Windows"; }

        protected override void Execute()
        {
            Directory.CreateDirectory(Logs); Directory.CreateDirectory(ImageLib.TemplatesDir(Root));
            Log.FilePath = Path.Combine(Logs, "app-" + Stamp + ".log"); R.LogsDir = Logs; R.Stamp = Stamp;
            Log.Write("=== добавление шаблона " + SourceFile);
            TrackDevice();
            AddStep("open", "Открытие образа"); AddStep("copy", "Копирование на диск переноса"); AddStep("info", "Проверка шаблона");
            bool iso = SourceFile.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
            string src = SourceFile; bool mounted = false;
            try
            {
                Set("open", "run"); Activity = "Открываю образ"; ActivityDetail = SourceFile; StageFraction = -1;
                if (iso)
                {
                    string r = ImageLib.Ps("$i=Mount-DiskImage -ImagePath '" + SourceFile.Replace("'", "''") + "' -PassThru;'LETTER:'+($i|Get-Volume).DriveLetter");
                    var m = Regex.Match(r, @"LETTER:([A-Z])");
                    if (!m.Success) { Fail("Не удалось открыть ISO-файл", "Windows не смогла подключить этот образ: " + (r.Length > 200 ? r.Substring(r.Length - 200) : r) + ". Возможно, файл скачан не до конца."); return; }
                    mounted = true;
                    string l = m.Groups[1].Value + @":\sources\";
                    src = File.Exists(l + "install.wim") ? l + "install.wim" : File.Exists(l + "install.esd") ? l + "install.esd" : null;
                    if (src == null) { Fail("В этом ISO нет установочного образа Windows", "Внутри должен быть файл sources\\install.wim или install.esd. Это не установочный диск Windows."); return; }
                }
                if (!File.Exists(src)) { Fail("Файл не найден", SourceFile); return; }
                long len = new FileInfo(src).Length, free = 0;
                try { free = new DriveInfo(Root.Substring(0, 3)).AvailableFreeSpace; } catch { }
                if (len > free - (1L << 30)) { Fail("На диске переноса не хватает места", "Образ занимает " + Fmt.Size(len) + ", а свободно " + Fmt.Size(free) + "."); return; }
                Set("open", "ok", Path.GetFileName(src) + ", " + Fmt.Size(len));

                string name = Regex.Replace(Path.GetFileNameWithoutExtension(SourceFile), @"[^\w\-\. ]", "_") + Path.GetExtension(src).ToLowerInvariant();
                string dst = Path.Combine(ImageLib.TemplatesDir(Root), name), tmp = dst + ".part";
                Set("copy", "run"); Activity = "Копирую образ на диск переноса"; R.BytesTotal = len; R.BytesDone = 0; R.JobName = "Шаблон Windows · " + name; R.CountLabel = ""; R.FilesLabel = ""; R.JobIndex = 1; R.JobCount = 1;
                Copying = true; bool running = true;
                var pump = new Thread(() =>
                {
                    var samples = new Queue<KeyValuePair<DateTime, long>>();
                    while (running)
                    {
                        Thread.Sleep(500);
                        samples.Enqueue(new KeyValuePair<DateTime, long>(DateTime.Now, Interlocked.Read(ref R.BytesDone))); while (samples.Count > 20) samples.Dequeue();
                        if (samples.Count >= 4) { var f = samples.Peek(); var la = samples.Last(); double sec = (la.Key - f.Key).TotalSeconds; if (sec > 0.5) R.Speed = Math.Max(0, (la.Value - f.Value) / sec); }
                    }
                }) { IsBackground = true };
                pump.Start();
                // Копирование с докачкой: если диск переноса отвалился, ждём его и продолжаем с того же места (буква могла смениться).
                long pos = 0; int drops = 0;
                var buf = new byte[4 << 20];
                while (true)
                {
                    dst = Path.Combine(ImageLib.TemplatesDir(Root), name); tmp = dst + ".part";
                    try
                    {
                        using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 4 << 20, FileOptions.SequentialScan))
                        using (var o = new FileStream(tmp, pos == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, 4 << 20))
                        {
                            if (pos > 0) { pos = Math.Min(pos, o.Length); o.SetLength(pos); o.Seek(pos, SeekOrigin.Begin); i.Seek(pos, SeekOrigin.Begin); Interlocked.Exchange(ref R.BytesDone, pos); }
                            int n; R.CurrentFile = dst;
                            while ((n = i.Read(buf, 0, buf.Length)) > 0)
                            {
                                R.WaitWhilePaused();
                                if (R.Cancelled) break;
                                o.Write(buf, 0, n); pos += n; Interlocked.Add(ref R.BytesDone, n);
                            }
                        }
                        break;
                    }
                    catch (IOException ex)
                    {
                        if (!R.Cancelled) Thread.Sleep(2000);      // Windows не сразу замечает, что диск пропал
                        if (!R.DeviceOk() && drops < 5 && !R.Cancelled)
                        {
                            drops++; Log.Write("шаблон: диск переноса отключился на " + Fmt.Size(pos) + " — жду (" + ex.Message + ")");
                            Copying = false; R.WaitingDevice = true; Activity = "Жду диск переноса"; R.WaitDevice(); R.WaitingDevice = false; Copying = true;
                            Activity = "Копирую образ на диск переноса";
                            if (R.Cancelled) break;
                            try { pos = File.Exists(Path.Combine(ImageLib.TemplatesDir(Root), name) + ".part") ? Math.Min(pos, new FileInfo(Path.Combine(ImageLib.TemplatesDir(Root), name) + ".part").Length) : 0; } catch { pos = 0; }
                            pos -= pos % (4 << 20);      // последний блок мог записаться не целиком — повторяем его
                            continue;
                        }
                        running = false; Copying = false; try { File.Delete(tmp); } catch { }
                        Fail("Копирование прервалось", "Windows сообщила: " + ex.Message + " Если съёмный диск отключался — подключи его и добавь шаблон ещё раз.");
                        return;
                    }
                }
                running = false; pump.Join(); Copying = false; R.Speed = 0;
                if (R.Cancelled) { try { File.Delete(tmp); } catch { } return; }
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
                Set("copy", "ok", Fmt.Size(len));

                Set("info", "run"); Activity = "Проверяю шаблон"; ActivityDetail = dst; StageFraction = -1;
                var info = ImageLib.TemplateInfo(dst);
                if (info.Count == 0) { try { File.Delete(dst); } catch { } Fail("Это не образ Windows", "Файл скопирован, но Windows не распознала в нём установочный образ, поэтому шаблон не добавлен."); return; }
                Set("info", "ok", "вариантов Windows внутри: " + info.Count);
                ResultLevel = 0; ResultTitle = "Шаблон добавлен";
                ResultText = "Теперь его можно развернуть второй Windows на любом компьютере: кнопка «Развернуть образ» на главном экране.";
                Summary.Add("Файл: " + dst + " (" + Fmt.Size(len) + ")");
                foreach (var e in info) Summary.Add("   " + e["Index"] + ". " + e["Name"] + " — " + Fmt.Size(Convert.ToDouble(e["Bytes"])) + " после распаковки");
                Summary.Add("Заняло " + Fmt.Clock(DateTime.Now - Started));
            }
            finally
            {
                if (mounted) ImageLib.Ps("Dismount-DiskImage -ImagePath '" + SourceFile.Replace("'", "''") + "' | Out-Null;'OK'");
            }
        }
    }
}
