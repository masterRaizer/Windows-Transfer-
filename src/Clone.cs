using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Transfer
{
    /// <summary>Общие вещи для клонирования Windows: запуск DISM с процентами, сведения об образе.</summary>
    abstract class CloneFlowBase : Flow
    {
        public string ImageId = "clone";     // папка клона внутри images\
        public string TemplateRel;            // для шаблона: путь к файлу образа относительно папки переноса
        public int Index = 1;                 // номер варианта Windows внутри файла образа
        public string CloneDir { get { return TemplateRel != null ? Path.Combine(Root, "templates") : Path.Combine(Root, "images", ImageId); } }
        public string Wim { get { return TemplateRel != null ? Path.Combine(Root, TemplateRel) : Path.Combine(CloneDir, "windows.wim"); } }
        public string Meta { get { return Path.Combine(CloneDir, "clone.json"); } }
        protected Process dism;
        protected readonly object dgate = new object();
        static readonly Regex Pct = new Regex(@"(\d{1,3}\.\d)%", RegexOptions.Compiled);
        protected string lastDismLines = "";
        protected volatile string DismLine = "";    // последняя строка DISM (не проценты)
        static readonly Regex OfN = new Regex(@"(\d+)\s+of\s+(\d+)", RegexOptions.Compiled);

        /// <summary>Сколько байт запущенный DISM прочитал и записал (-1 — DISM не запущен).</summary>
        protected void DismIo(out long read, out long written)
        {
            Process p; lock (dgate) p = dism;
            Native.Io(p, out read, out written);
        }
        protected volatile bool resetSpeed;         // сбросить замер скорости (сменились единицы прогресса)

        protected CloneFlowBase()
        {
            R.OnCancel = KillDism;
            R.OnPause = p => { lock (dgate) Native.Suspend(dism, p); };
        }

        /// <summary>Запуск DISM. Проценты приходят в onPercent (0..100).</summary>
        protected int Dism(string args, Action<double> onPercent)
        {
            var tail = new Queue<string>();
            string scratch = Path.Combine(Path.GetTempPath(), "Perenos", "scratch");
            Directory.CreateDirectory(scratch);
            DismLine = ""; int loggedPct = -1;
            Log.Write("dism " + args);
            int rc = Sh.Run("dism.exe", "/English " + args + " /ScratchDir:\"" + scratch + "\"", line =>
            {
                var m = Pct.Match(line);
                if (m.Success)
                {
                    double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if ((int)v / 5 != loggedPct) { loggedPct = (int)v / 5; Log.Write("dism " + v.ToString("0.0", CultureInfo.InvariantCulture) + "%"); }
                    if (onPercent != null) onPercent(v);
                    return;
                }
                if (string.IsNullOrWhiteSpace(line)) return;
                string t = line.Trim();
                lock (tail) { tail.Enqueue(t); while (tail.Count > 6) tail.Dequeue(); }
                // Строку DISM видно на экране: «Exporting 12 of 597 …», «Applying image» и т. п.
                DismLine = t;
                if (!Copying)
                {
                    ActivityDetail = t.Length > 140 ? t.Substring(0, 140) + "…" : t;
                    var nm = OfN.Match(t); long a, b;
                    if (nm.Success && long.TryParse(nm.Groups[1].Value, out a) && long.TryParse(nm.Groups[2].Value, out b) && b > 0) { StageFraction = Math.Min(1, (double)a / b); StageStat = a + " из " + b; }
                }
            }, Sh.Oem, p => { lock (dgate) dism = p; });
            lock (dgate) dism = null;
            lock (tail) lastDismLines = string.Join(" ", tail);
            Log.Write("dism " + args.Split(' ')[0] + " -> " + rc + " | " + lastDismLines);
            return rc;
        }

        protected void KillDism() { lock (dgate) { try { if (dism != null && !dism.HasExited) dism.Kill(); } catch { } } }

        /// <summary>Понятное объяснение кода ошибки DISM.</summary>
        protected string DismWhy(int rc)
        {
            switch (unchecked((uint)rc))
            {
                case 112: case 0x80070070: return "на диске закончилось место";
                case 5: case 0x80070005: return "Windows не дала доступ к файлам (запусти программу от имени администратора, на время отключи антивирус)";
                case 0x8007045D: case 1117: case 0x80070017: case 23: return "диск вернул ошибку чтения или записи — проверь кабель съёмного диска";
                case 0x80070570: case 0xC1420117: return "файл образа повреждён — сними клон заново";
                case 2: case 3: case 0x80070002: case 0x80070003: return "файл или папка не найдены (диск отключался?)";
                case 0xC1420127: return "образ уже открыт другой программой — перезагрузи компьютер и повтори";
                case 1223: case 0x800704C7: return "операция отменена";
                default: return "DISM сообщил: " + (lastDismLines.Length > 220 ? lastDismLines.Substring(lastDismLines.Length - 220) : lastDismLines) + " (код " + rc + ")";
            }
        }

        /// <summary>Плавный прогресс и скорость для экрана: доля берётся из процентов DISM.</summary>
        protected void Pump(Func<bool> running, Func<string> detail)
        {
            var samples = new Queue<KeyValuePair<DateTime, long>>();
            while (running())
            {
                Thread.Sleep(500);
                if (resetSpeed) { resetSpeed = false; samples.Clear(); }
                if (R.Paused) { samples.Clear(); R.Speed = 0; continue; }
                long done = Interlocked.Read(ref R.BytesDone);
                samples.Enqueue(new KeyValuePair<DateTime, long>(DateTime.Now, done));
                while (samples.Count > 120) samples.Dequeue();     // окно 60 секунд: DISM отдаёт проценты редко
                if (samples.Count >= 6)
                {
                    var f = samples.Peek(); var l = samples.Last(); double sec = (l.Key - f.Key).TotalSeconds;
                    if (sec > 1) R.Speed = Math.Max(0, (l.Value - f.Value) / sec);
                }
                if (detail != null) R.CurrentFile = detail();
            }
            R.Speed = 0;
        }

        // ---------------------------------------------------------------------------------------- проверка на вирусы
        protected SecurityReport Sec;
        protected string SecReportPath = "";

        /// <summary>Вопрос «да/нет» поверх окна программы; по умолчанию — «Нет».</summary>
        protected bool Ask(string title, string text)
        {
            bool yes = false;
            App.Ui(() => yes = System.Windows.MessageBox.Show(text, "Перенос — " + title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes);
            Log.Write("вопрос «" + title + "»: " + (yes ? "да" : "нет"));
            return yes;
        }

        /// <summary>Этап пропущен кнопкой «Пропустить проверку».</summary>
        protected void SkipNote(string stepId, string what)
        {
            Log.Write(what + ": пропущено пользователем");
            Set(stepId, "warn", "пропущено вручную");
            Summary.Add(what + ": пропущено вручную.");
        }

        protected void RunSecurityScan(string stepId, string root, bool live, string reportTitle, bool skippable = false)
        {
            Set(stepId, "run"); StageFraction = -1; StageStat = "";
            Sec = Security.Scan(root, live, s => ActivityDetail = s, () => R.Cancelled || (skippable && R.SkipRequested));
            if (skippable && R.SkipRequested) return;   // неполный результат — отчёт и проблемы не пишем
            string text = Sec.ToText(reportTitle);
            foreach (var dir in new[] { CloneDir, Logs })
                try { Directory.CreateDirectory(dir); string p = Path.Combine(dir, "security-" + Stamp + ".txt"); File.WriteAllText(p, text, new UTF8Encoding(true)); if (SecReportPath == "") SecReportPath = p; } catch { }
            Log.Write("проверка на вирусы: " + Sec.Verdict + ", опасного " + Sec.Dangerous + ", подозрительного " + Sec.Suspicious + ", отчёт " + SecReportPath);
            if (Sec.Dangerous > 0) Problems.Add(SecProblem(2, "Найдены вирусы или майнеры", "Это опасно: такие программы тайно тратят компьютер на чужую выгоду, а часто ещё и крадут пароли.", "Удали найденное и проверь компьютер антивирусом. Потом смени пароли (почта, Steam, банки, криптокошельки)."));
            if (Sec.Suspicious > 0) Problems.Add(SecProblem(1, "Подозрительное при проверке на вирусы", "Само по себе не обязательно вирус: например, отключённый Защитник или неподписанная программа в автозапуске.", "Посмотри список: если программа тебе знакома — всё в порядке."));
        }

        Problem SecProblem(int level, string title, string explain, string action)
        {
            var items = Sec.Items.Where(t => level == 2 ? t.Level >= 2 : t.Level == level).ToList();
            var p = new Problem { Key = "security-" + level, Title = title, Explain = explain, Action = action + (SecReportPath != "" ? " Полный отчёт: " + SecReportPath : ""), Severity = level, Count = items.Count };
            foreach (var t in items.Take(20)) p.Samples.Add(t.ToString());
            return p;
        }

        protected string SecList(int max)
        {
            var top = Sec.Items.Where(t => t.Level >= 2).Take(max).Select(t => "• " + t.What + (t.Path != "" ? ": " + Fmt.ShortPath(t.Path, 90) : "")).ToList();
            int more = Sec.Dangerous - top.Count;
            return string.Join("\n", top) + (more > 0 ? "\n…и ещё " + more : "");
        }

        /// <summary>Удалить из развёрнутой копии файлы, найденные проверкой (только внутри root). Возвращает, сколько удалено.</summary>
        protected int Disarm(string root)
        {
            int n = 0; string r = root.TrimEnd('\\') + "\\";
            var files = Sec.Items.Where(t => t.Level >= 2 && t.File != "").Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in files)
            {
                if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!File.Exists(f)) continue;
                    File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); n++;
                    Log.Write("обезврежено в клоне: удалён " + f);
                    // папка майнера в ProgramData, где кроме него ничего нет, — тоже лишняя
                    string d = Path.GetDirectoryName(f);
                    if (d.StartsWith(Path.Combine(r, "ProgramData") + "\\", StringComparison.OrdinalIgnoreCase) && Directory.Exists(d) &&
                        !Directory.EnumerateFiles(d, "*.exe", SearchOption.AllDirectories).Any())
                    { Directory.Delete(d, true); Log.Write("обезврежено в клоне: удалена папка " + d); }
                }
                catch (Exception e) { Log.Write("не удалось удалить " + f + ": " + e.Message); }
            }
            return n;
        }
    }

    // ============================================================================================ СНЯТЬ КЛОН
    class CloneCaptureFlow : CloneFlowBase
    {
        public string Source;                      // «C:\» — что клонируем
        const string SnapLink = @"C:\__xfer_snap";

        public string Compress = "fast";            // «none» — без сжатия (быстрее, файл больше), «fast» — со сжатием
        public string OsName = "";                  // название клонируемой Windows для списка образов
        public string OsVersionLine = "", OsCustom = "";

        public CloneCaptureFlow(string root, string source, string compress = "fast", string osName = "")
        {
            Root = root; Source = source ?? ((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + @"\");
            Compress = compress == "none" ? "none" : "fast"; OsName = osName ?? "";
            ImageId = Environment.MachineName + "-" + Source.Substring(0, 1) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
            Title = "Клон Windows с компьютера " + Environment.MachineName;
        }

        public long UsedBytes()
        {
            try { var d = new DriveInfo(Source.Substring(0, 3)); return d.TotalSize - d.AvailableFreeSpace; } catch { return 0; }
        }

        /// <summary>Сколько места нужно на диске переноса: образ сжимается, но насколько — заранее неизвестно, берём с запасом.</summary>
        public long NeedBytes() { return NeedFor(UsedBytes(), Compress); }
        public static long NeedFor(long used, string compress) { return compress == "none" ? (long)(used * 1.02) + (2L << 30) : (long)(used * 0.65) + (4L << 30); }

        void WriteConfig(string path, bool snapshot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ExclusionList]");
            foreach (var x in new[] { @"\$ntfs.log", @"\hiberfil.sys", @"\pagefile.sys", @"\swapfile.sys", @"\System Volume Information", @"\RECYCLER", @"\$Recycle.Bin",
                @"\Windows\CSC", @"\Windows\Temp\*", @"\Windows\SoftwareDistribution\Download\*", @"\Users\*\AppData\Local\Temp\*", @"\__xfer_snap", @"\xfer-fresh.marker" })
                sb.AppendLine(x);
            // сам диск переноса, если он вдруг лежит на клонируемом томе
            if (Root.StartsWith(Source.Substring(0, 2), StringComparison.OrdinalIgnoreCase)) sb.AppendLine(Root.Substring(2));
            sb.AppendLine(); sb.AppendLine("[CompressionExclusionList]");
            foreach (var x in new[] { "*.mp3", "*.mp4", "*.mkv", "*.avi", "*.zip", "*.7z", "*.rar", "*.cab", "*.jpg", "*.jpeg", "*.png", "*.wim", "*.esd", "*.iso", "*.vhd", "*.vhdx", "*.img", @"\WINDOWS\inf\*.pnf" })
                sb.AppendLine(x);
            File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        }

        protected override void Execute()
        {
            Directory.CreateDirectory(CloneDir); Directory.CreateDirectory(Logs);
            Log.FilePath = Path.Combine(Logs, "app-" + Stamp + ".log"); R.LogsDir = Logs; R.Stamp = Stamp;
            Log.Write("=== снятие клона " + Source + ", компьютер " + Environment.MachineName);
            TrackDevice();
            AddStep("check", "Проверка места"); AddStep("scan", "Проверка на вирусы"); AddStep("snap", "Снимок диска"); AddStep("cap", "Снятие образа Windows"); AddStep("verify", "Проверка образа");
            bool isSystem = Source.StartsWith(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", StringComparison.OrdinalIgnoreCase);

            // ---- место
            Set("check", "run"); Activity = "Проверяю место"; StageFraction = -1;
            if (!File.Exists(Path.Combine(Source, @"Windows\System32\ntoskrnl.exe"))) { Fail("На диске " + Source.Substring(0, 2) + " нет Windows", "Клонировать можно только диск, на котором установлена Windows."); return; }
            long used = UsedBytes(), need = NeedBytes(), free = 0, old = 0;
            try { free = new DriveInfo(Root.Substring(0, 3)).AvailableFreeSpace; } catch { }
            try { if (File.Exists(Wim)) old = new FileInfo(Wim).Length; } catch { }
            if (need > free + old)
            {
                Fail("На диске переноса не хватает места для клона",
                    "Windows занимает " + Fmt.Size(used) + ". Для образа нужно примерно " + Fmt.Size(need) + ", а свободно " + Fmt.Size(free + old) + ". Освободи ещё " + Fmt.Size(need - free - old) +
                    ". " + (Compress == "none" ? "Можно выбрать запись со сжатием — файл выйдет примерно вдвое меньше. " : "") + "Ещё вариант — удалить папку «data\\files» на диске переноса: это копия файлов, при клонировании она не нужна.");
                return;
            }
            Set("check", "ok", "Windows занимает " + Fmt.Size(used) + ", свободно " + Fmt.Size(free + old));

            // ---- проверка на вирусы: заражённую Windows клонировать нельзя — вирус переедет вместе с ней
            Activity = "Проверяю Windows на вирусы и майнеры"; ActivityDetail = "заражённую систему клонировать нельзя — вирус переедет вместе с ней";
            RunSecurityScan("scan", Source, isSystem, "Проверка перед снятием клона: " + Environment.MachineName + ", диск " + Source.Substring(0, 2));
            if (R.Cancelled) return;
            if (Sec.Dangerous > 0)
            {
                if (!Ask("найдены вирусы", "В этой Windows найдено опасное:\n\n" + SecList(8) + "\n\nЕсли снять клон сейчас, вирусы переедут вместе с ним на другой компьютер.\n\n" +
                         "Всё равно снять клон?\n\n«Нет» (рекомендуется) — остановиться: сначала удали найденное, потом сними клон заново."))
                {
                    Fail("В этой Windows найдены вирусы — клон не снят",
                        "Найдено опасного: " + Sec.Dangerous + ". Клон не снимался, на диске переноса ничего не изменилось. Удали найденное (список — в отчёте ниже и в файле " + SecReportPath + "), " +
                        "проверь компьютер антивирусом и запусти снятие клона снова. После чистки смени пароли: вместе с майнерами часто приходят программы, которые крадут пароли.");
                    return;
                }
                Set("scan", "warn", "найдено опасного: " + Sec.Dangerous + " — клон снят по твоему решению, вирусы будут и в нём");
            }
            else Set("scan", Sec.Suspicious > 0 ? "warn" : "ok", (Sec.Suspicious > 0 ? "опасного нет, подозрительного: " + Sec.Suspicious + " (см. отчёт)" : "вирусов и майнеров не найдено") +
                                                                (Sec.DefenderUsed ? " · проверили Защитник и программа" : " · Защитник не работает, проверено правилами программы"));

            // ---- снимок (для работающей системы)
            string capDir = Source; bool snap = false;
            if (isSystem)
            {
                Set("snap", "run"); Activity = "Делаю снимок диска"; ActivityDetail = "чтобы образ получился цельным, пока Windows работает";
                snap = BackupFlow.CreateSnapshot();
                if (!snap) { Fail("Не удалось сделать снимок диска", "Без снимка работающую Windows клонировать нельзя: системные файлы заняты. Перезагрузи компьютер и попробуй снова. Если не поможет — проверь, что служба «Теневое копирование тома» не отключена."); return; }
                capDir = SnapLink; Set("snap", "ok", "образ снимается с неизменного снимка");
            }
            else Set("snap", "ok", "не нужен: эта Windows сейчас не запущена");

            try
            {
                int attempt = 0;
                while (true)
                {
                    attempt++;
                    // пути считаем заново на каждой попытке: после обрыва диск переноса мог вернуться под другой буквой
                    Directory.CreateDirectory(CloneDir);
                    string cfg = Path.Combine(CloneDir, "exclude.ini");
                    WriteConfig(cfg, snap);
                    try { if (File.Exists(Wim)) File.Delete(Wim); } catch { }
                    try { File.Delete(Meta); } catch { }
                    Set("cap", "run", attempt > 1 ? "попытка " + attempt + ": после обрыва образ снимается заново" : "");
                    Activity = "Сканирую файлы Windows"; ActivityDetail = "скоро пойдут проценты"; StageFraction = -1; StageStat = "";
                    R.BytesTotal = used; R.BytesDone = 0; R.JobName = "Образ Windows · диск " + Source.Substring(0, 2); R.JobIndex = 1; R.JobCount = 1;
                    R.CountLabel = Compress == "none" ? "без сжатия" : "со сжатием"; R.FilesLabel = "";
                    bool running = true, lost = false;
                    var pump = new Thread(() => Pump(() => running, () =>
                    {
                        if (!R.DeviceOk()) { lost = true; KillDism(); }
                        try { long w = new FileInfo(Wim).Length; R.FilesLabel = "файл образа " + Fmt.Size(w); } catch { }
                        return Wim;
                    })) { IsBackground = true };
                    pump.Start();
                    int rc = Dism("/Capture-Image /ImageFile:\"" + Wim + "\" /CaptureDir:" + (capDir.Contains(" ") ? "\"" + capDir.TrimEnd('\\') + "\"" : capDir) +
                                  " /Name:\"Clone " + Environment.MachineName + "\" /Description:\"" + DateTime.Now.ToString("s") + "\" /Compress:" + Compress + " /CheckIntegrity /ConfigFile:\"" + cfg + "\"",
                        pct => { if (!Copying) { Copying = true; Activity = "Снимаю образ Windows"; } Interlocked.Exchange(ref R.BytesDone, (long)(used * pct / 100.0)); });
                    running = false; pump.Join(); Copying = false;
                    if (R.Cancelled) { try { File.Delete(Wim); } catch { } return; }
                    if (lost || !R.DeviceOk())
                    {
                        if (attempt >= 3) { Fail("Диск переноса отключается снова и снова", "Образ не удалось записать с трёх попыток: съёмный диск пропадает во время записи. Замени кабель или подключи диск в другой разъём на корпусе и запусти клонирование заново."); return; }
                        R.WaitingDevice = true; Activity = "Жду диск переноса"; R.WaitDevice(); R.WaitingDevice = false;
                        continue;      // файл образа нельзя дописать с середины — начинаем заново
                    }
                    if (rc != 0) { Fail("Не удалось снять образ Windows", "Причина: " + DismWhy(rc) + "."); try { File.Delete(Wim); } catch { } return; }
                    break;
                }
                long wim = new FileInfo(Wim).Length;
                Set("cap", "ok", "образ " + Fmt.Size(wim) + " (было " + Fmt.Size(used) + ")");

                // ---- проверка образа
                Set("verify", "run"); Activity = "Проверяю образ"; ActivityDetail = Wim; StageFraction = -1;
                string info = ""; long imgBytes = 0; string ver = "", edition = "";
                int irc = Sh.Run("dism.exe", "/English /Get-ImageInfo /ImageFile:\"" + Wim + "\" /Index:1", l => { info += l + "\n"; }, Sh.Oem);
                var ms = Regex.Match(info, @"Size\s*:\s*([\d,\.\s]+)\s*bytes"); if (ms.Success) long.TryParse(Regex.Replace(ms.Groups[1].Value, @"[^\d]", ""), out imgBytes);
                var mv = Regex.Match(info, @"(?m)^Version\s+:\s*([\d\.]+)"); if (mv.Success) ver = mv.Groups[1].Value;
                var me = Regex.Match(info, @"Edition\s*:\s*(\S+)"); if (me.Success) edition = me.Groups[1].Value;
                if (irc != 0 || imgBytes == 0) { Fail("Образ записан, но не читается", "Проверка образа не прошла: " + DismWhy(irc) + ". Скорее всего, запись на съёмный диск прошла с ошибками — сними клон заново, лучше с другим кабелем."); return; }
                Json.WriteFile(Meta, new Dictionary<string, object> {
                    { "Computer", Environment.MachineName }, { "User", Environment.UserName }, { "Date", DateTime.Now.ToString("s") }, { "Source", Source },
                    { "UsedBytes", used }, { "ImageBytes", imgBytes }, { "WimBytes", wim }, { "Version", ver }, { "Edition", edition }, { "Name", OsName }, { "VersionLine", OsVersionLine }, { "Custom", OsCustom }, { "Compress", Compress }, { "Hw", HwProfile.Read().ToDict() },
                    { "Security", Sec != null ? Sec.Verdict : "" }, { "SecurityItems", Sec != null ? Sec.Items.Where(t => t.Level >= 2).Select(t => t.ToString()).Take(30).ToList() : new List<string>() } });
                Set("verify", "ok", "Windows " + ver + " " + edition + ", " + Fmt.Size(imgBytes) + " после распаковки");

                ResultLevel = 0; ResultTitle = "Клон Windows снят";
                ResultText = "Образ лежит на диске переноса и появился в списке образов. Чтобы развернуть его, запусти «Перенос» на нужном компьютере и нажми «Развернуть образ».";
                Summary.Add("Система: " + (OsName != "" ? OsName : "Windows " + ver + " " + edition) + (OsVersionLine != "" ? " (" + OsVersionLine + ")" : ""));
                Summary.Add("Компьютер: " + Environment.MachineName + ", диск " + Source.Substring(0, 2));
                Summary.Add("Проверка на вирусы: " + (Sec == null ? "не выполнялась" : Sec.Dangerous > 0 ? "НАЙДЕНО ОПАСНОГО " + Sec.Dangerous + " — клон заражён" : Sec.Suspicious > 0 ? "опасного нет, подозрительного " + Sec.Suspicious : "чисто"));
                if (Sec != null && Sec.Dangerous > 0) { ResultLevel = 1; ResultTitle = "Клон снят, но в нём вирусы"; }
                Summary.Add("Занято на диске: " + Fmt.Size(used) + " → файл образа " + Fmt.Size(wim) + " (после распаковки " + Fmt.Size(imgBytes) + ")");
                Summary.Add("Заняло " + Fmt.Clock(DateTime.Now - Started) + ". Файл: " + Wim);
                Summary.Add("На новом компьютере понадобится свой ключ Windows: лицензия привязана к этому компьютеру и с клоном не переезжает.");
            }
            finally { if (snap) BackupFlow.CleanupSnapshot(); }
        }
    }

    // ============================================================================================ РАЗВЕРНУТЬ КЛОН
    class CloneApplyFlow : CloneFlowBase
    {
        public string TestDir, TestEsp;            // служебное: развернуть в готовый раздел и записать загрузчик на указанный том
        public long ImageBytes, WimBytes; public string OldComputer = "", Version = "";
        public const string Marker = "xfer-fresh.marker";
        /// <summary>Сколько места пользователь выделил под клон (0 — взять минимум).</summary>
        public long CloneSize;

        /// <summary>Сколько места можно отдать клону: весь диск минус то, что нужно оставить основной Windows.</summary>
        public long MaxBytes()
        {
            string l = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").Substring(0, 1);
            string r = Ps("$l='" + l + "';$p=Get-Partition -DriveLetter $l;$v=Get-Volume -DriveLetter $l;$used=$v.Size-$v.SizeRemaining;" +
                          "$sup=Get-PartitionSupportedSize -DriveLetter $l;$keep=[math]::Max($sup.SizeMin+2GB,$used+20GB);'MAX:'+($p.Size-$keep)");
            var m = Regex.Match(r, @"MAX:(-?\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value) : -1;
        }

        public string Label = "клон";               // «клон с PCNAME» или «шаблон Windows 10 Pro» — так пункт называется в меню загрузки

        public CloneApplyFlow(string root, ImageRef img)
        {
            Root = root; Title = "Вторая Windows на компьютер " + Environment.MachineName;
            if (img == null) return;
            ImageId = img.Id ?? "clone"; TemplateRel = img.TemplateRel; Index = img.Index;
            ImageBytes = img.ImageBytes; WimBytes = img.WimBytes; OldComputer = img.Computer; Version = img.Version; Label = img.Label;
        }

        /// <summary>Размер раздела под клон: распакованный образ + 7 % + место под файл подкачки и обновления.</summary>
        public long NeedBytes() { return (long)(ImageBytes * 1.07) + (12L << 30); }

        string Ps(string script)
        {
            var sb = new StringBuilder();
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8;try{" + script + "}catch{'ERR:'+$_.Exception.Message}"));
            Sh.Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, l => { lock (sb) sb.AppendLine(l); }, Encoding.UTF8);
            string s = sb.ToString().Trim(); Log.Write("ps: " + (s.Length > 300 ? s.Substring(0, 300) : s));
            return s;
        }

        protected override void Execute()
        {
            Directory.CreateDirectory(Logs);
            Log.FilePath = Path.Combine(Logs, "app-" + Stamp + ".log"); R.LogsDir = Logs; R.Stamp = Stamp;
            Log.Write("=== разворачивание клона на " + Environment.MachineName + (TestDir != null ? " (проверочный режим: " + TestDir + ")" : ""));
            TrackDevice();
            bool test = TestDir != null;
            AddStep("check", "Проверка"); if (!test) { AddStep("drv", "Драйверы этого компьютера"); AddStep("part", "Раздел под клон"); }
            AddStep("apply", "Разворачивание Windows"); AddStep("kvrt", "Kaspersky: поиск и лечение вирусов"); AddStep("scan", "Проверка на майнеры и следы вирусов");
            AddStep("sfc", "Сверка системных файлов"); AddStep("fix", "Драйверы и настройка"); AddStep("boot", "Загрузчик");
            string sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

            // ---- проверки
            Set("check", "run"); Activity = "Проверяю образ и компьютер"; StageFraction = -1;
            if (!File.Exists(Wim) || ImageBytes == 0) { Fail("Образ не найден на диске переноса", "Файл образа пропал или повреждён: " + Wim + ". Сними клон заново или добавь шаблон ещё раз."); return; }
            if (!test)
            {
                if (!string.Equals(Environment.GetEnvironmentVariable("firmware_type"), "UEFI", StringComparison.OrdinalIgnoreCase))
                { Fail("Этот компьютер загружается в старом режиме BIOS", "Клон разворачивается только на компьютерах с загрузкой UEFI (все ноутбуки последних лет). Включи режим UEFI в настройках BIOS."); return; }
            }
            // клон, снятый с заражённой Windows, — спросить до того, как что-то менять на этом компьютере
            string secMark = ""; try { if (TemplateRel == null && File.Exists(Meta)) secMark = Json.Str(Json.ReadFile(Meta), "Security") ?? ""; } catch { }
            if (secMark == "infected") Log.Write("клон помечен как заражённый при снятии — после разворачивания его вылечат Kaspersky и проверка программы");
            Set("check", "ok", Label + ", " + Fmt.Size(ImageBytes) + (secMark == "infected" ? " · клон был заражён — вылечу после разворачивания" : ""));

            string target = TestDir, drivers = Path.Combine(Root, "images", "drivers-" + Environment.MachineName);
            HwProfile hw = null;
            if (!test)
            {
                // ---- драйверы этого компьютера (в первую очередь контроллер диска): без них клон не увидит диск и не загрузится
                Set("drv", "run"); Activity = "Сохраняю драйверы этого компьютера"; ActivityDetail = "их получит клон, чтобы увидеть диск, Wi-Fi и экран";
                Directory.CreateDirectory(drivers);
                int drc = Dism("/Online /Export-Driver /Destination:\"" + drivers + "\"", null);
                int ndrv = 0; try { ndrv = Directory.GetDirectories(drivers).Length; } catch { }
                Set("drv", ndrv > 0 ? "ok" : "warn", ndrv > 0 ? "сохранено драйверов: " + ndrv : "драйверы сохранить не удалось — клон может не увидеть диск");
                if (R.Cancelled) return;
                hw = HwProfile.Read();
                foreach (var svc in hw.SpecialStorage)
                {
                    bool found = false;
                    try { found = Directory.GetFiles(drivers, "*.inf", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).IndexOf(svc, StringComparison.OrdinalIgnoreCase) >= 0); } catch { }
                    Log.Write("драйвер контроллера диска " + svc + ": " + (found ? "найден" : "НЕ НАЙДЕН"));
                    if (!found)
                    {
                        Fail("Не найден драйвер контроллера диска (" + HwProfile.StorageName(svc) + ")",
                            "Диск этого компьютера работает через " + HwProfile.StorageName(svc) + ", а его драйвер не удалось взять из этой Windows. Без него вторая Windows не увидит диск и не загрузится, поэтому программа остановилась, ничего не изменив. " +
                            "Поставь драйвер Intel RST с сайта производителя ноутбука и запусти снова — либо отключи VMD в настройках BIOS.");
                        return;
                    }
                }
                Set("drv", "ok", "сохранено драйверов: " + ndrv + (hw.SpecialStorage.Count > 0 ? ", драйвер диска " + HwProfile.StorageName(hw.SpecialStorage[0]) + " найден" : ""));

                // ---- раздел: ужимаем текущую Windows и отдаём освободившееся место клону
                Set("part", "run"); Activity = "Готовлю раздел под клон"; ActivityDetail = "основная Windows ужимается, её файлы не трогаются";
                string letter = sys.Substring(0, 1);
                string r = Ps("$l='" + letter + "';$need=" + NeedBytes() + ";$p=Get-Partition -DriveLetter $l;$d=Get-Disk -Number $p.DiskNumber;" +
                    "if($d.PartitionStyle -ne 'GPT'){'ERR:MBR';return};" +
                    "$v=Get-Volume -DriveLetter $l;$used=$v.Size-$v.SizeRemaining;$sup=Get-PartitionSupportedSize -DriveLetter $l;" +
                    "$keep=[math]::Max($sup.SizeMin+2GB,$used+20GB);$max=$p.Size-$keep;$give=" + (CloneSize > 0 ? CloneSize : NeedBytes()) + ";" +
                    "if($max -lt $need -or $give -gt $max){'ERR:NOSPACE:'+$max;return};" +
                    "if($give -lt $need){$give=$need};$give=[math]::Floor($give/1MB)*1MB;" +
                    "Set-Content -LiteralPath ($l+':\\" + Marker + "') -Value (Get-Date -Format s);" +
                    "Resize-Partition -DriveLetter $l -Size ($p.Size-$give);" +
                    "$np=New-Partition -DiskNumber $p.DiskNumber -UseMaximumSize -AssignDriveLetter;" +
                    "Format-Volume -DriveLetter $np.DriveLetter -FileSystem NTFS -NewFileSystemLabel 'Windows 2' -Confirm:$false | Out-Null;" +
                    "'OK:'+$np.DriveLetter+':'+$np.Size");
                var mo = Regex.Match(r, @"OK:([A-Z]):(\d+)");
                if (!mo.Success)
                {
                    var mn = Regex.Match(r, @"ERR:NOSPACE:(-?\d+)");
                    if (mn.Success) Fail("На диске этого компьютера не хватает места под клон", "Клону нужно " + Fmt.Size(NeedBytes()) + " (образ " + Fmt.Size(ImageBytes) + " + запас), а освободить получается только " + Fmt.Size(Math.Max(0, long.Parse(mn.Groups[1].Value))) + ". Диск этого компьютера меньше, чем нужно старой Windows.");
                    else if (r.Contains("ERR:MBR")) Fail("Диск размечен в старом формате MBR", "Клон разворачивается только на диск с разметкой GPT.");
                    else Fail("Не удалось подготовить раздел", "Windows сообщила: " + (r.Length > 250 ? r.Substring(r.Length - 250) : r) + ". Ничего не удалено; текущая Windows работает как раньше.");
                    return;
                }
                target = mo.Groups[1].Value + @":\";
                Set("part", "ok", "раздел " + target.Substring(0, 2) + " · " + Fmt.Size(double.Parse(mo.Groups[2].Value)));
            }
            if (R.Cancelled) return;

            if (!Directory.Exists(target)) { Fail("Раздел под вторую Windows недоступен", "Раздел " + target.Substring(0, 2) + " не найден в системе. Ничего не изменено. Перезагрузи компьютер и запусти снова."); return; }
            // ---- разворачивание
            Set("apply", "run"); Activity = "Разворачиваю Windows"; ActivityDetail = "";
            R.JobName = "Windows (" + Label + ") → раздел " + target.Substring(0, 2); R.JobIndex = 1; R.JobCount = 1; R.FilesLabel = "";
            long wimLen = WimBytes; try { wimLen = new FileInfo(Wim).Length; } catch { }
            int attempt = 0;
            while (true)
            {
                attempt++;
                bool running = true, lost = false, applying = false; Copying = true;
                // Этап 1: DISM с /CheckIntegrity сначала целиком читает файл образа и ничего не пишет на диск — без этого экран «молчал» по полчаса.
                // Прогресс берём из счётчика прочитанных DISM байт. Распаковка снова читает весь образ примерно с той же скоростью — она входит в оставшееся время.
                Activity = "Проверяю целостность образа"; ActivityDetail = "";
                R.BytesTotal = Math.Max(1, wimLen); Interlocked.Exchange(ref R.BytesDone, 0); Interlocked.Exchange(ref R.BytesAfter, wimLen); resetSpeed = true;
                R.CountLabel = "этап 1 из 2 · проверка целостности образа, на диск пока ничего не пишется";
                long freeStart = 0; try { freeStart = new DriveInfo(target.Substring(0, 3)).AvailableFreeSpace; } catch { }
                DateTime nextLog = DateTime.Now.AddMinutes(1);
                var pump = new Thread(() => Pump(() => running, () =>
                {
                    if (!R.DeviceOk()) { lost = true; KillDism(); }
                    long rd, wr; DismIo(out rd, out wr);
                    long written = 0; try { written = Math.Max(0, freeStart - new DriveInfo(target.Substring(0, 3)).AvailableFreeSpace); } catch { }
                    string line = DismLine;
                    string s;
                    if (!applying)
                    {
                        if (rd >= 0) Interlocked.Exchange(ref R.BytesDone, Math.Min(rd, wimLen));
                        s = "проверено " + Fmt.Size(Math.Max(0, rd)) + " из " + Fmt.Size(wimLen) + " · потом распаковка на " + target.Substring(0, 2);
                    }
                    else s = "записано на " + target.Substring(0, 2) + " " + Fmt.Size(written) + " · прочитано из образа " + Fmt.Size(Math.Max(0, rd)) + " из " + Fmt.Size(wimLen);
                    if (!string.IsNullOrEmpty(line)) s += " · DISM: " + line;
                    if (DateTime.Now >= nextLog)
                    {
                        nextLog = DateTime.Now.AddMinutes(1);
                        double eta = R.EtaSeconds;
                        Log.Write((applying ? "распаковка" : "проверка образа") + ": прочитано " + Fmt.Size(Math.Max(0, rd)) + " из " + Fmt.Size(wimLen) + ", записано " + Fmt.Size(written) +
                                  ", скорость " + Fmt.Speed(R.Speed) + ", осталось " + Fmt.Eta(eta));
                    }
                    return s;
                })) { IsBackground = true };
                pump.Start();
                int rc = Dism("/Apply-Image /ImageFile:\"" + Wim + "\" /Index:" + Index + " /ApplyDir:" + target + " /CheckIntegrity",
                    pct =>
                    {
                        if (!applying)
                        {
                            // Этап 2: пошли проценты — DISM распаковывает файлы
                            applying = true; Activity = "Разворачиваю Windows";
                            R.BytesTotal = ImageBytes; Interlocked.Exchange(ref R.BytesAfter, 0); resetSpeed = true;
                            R.CountLabel = "этап 2 из 2 · распаковка файлов на раздел " + target.Substring(0, 2);
                            Log.Write("проверка образа завершена, началась распаковка");
                        }
                        Interlocked.Exchange(ref R.BytesDone, (long)(ImageBytes * pct / 100.0));
                    });
                running = false; pump.Join(); Copying = false;
                if (R.Cancelled) return;
                if (lost || !R.DeviceOk())
                {
                    if (attempt >= 3) { Fail("Диск переноса отключается снова и снова", "Развернуть образ не удалось с трёх попыток. Замени кабель или разъём и запусти снова. Текущая Windows не тронута и работает."); return; }
                    R.WaitingDevice = true; Activity = "Жду диск переноса"; R.WaitDevice(); R.WaitingDevice = false;
                    Activity = "Разворачиваю Windows заново"; Ps("Format-Volume -DriveLetter " + target.Substring(0, 1) + " -FileSystem NTFS -NewFileSystemLabel 'Windows 2' -Confirm:$false | Out-Null;'OK'");
                    continue;
                }
                if (rc != 0) { Fail("Не удалось развернуть Windows", "Причина: " + DismWhy(rc) + ". Текущая Windows не тронута и работает как раньше."); return; }
                break;
            }
            if (!File.Exists(Path.Combine(target, @"Windows\System32\ntoskrnl.exe"))) { Fail("Образ развёрнут не полностью", "В развёрнутой копии нет системных файлов Windows. Сними клон заново."); return; }
            Set("apply", "ok", Fmt.Size(ImageBytes));

            // ---- Kaspersky Virus Removal Tool: лечит развёрнутую копию, пока она ни разу не запускалась (вирус в ней ещё ничего не сделал)
            Set("kvrt", "run"); Activity = "Kaspersky ищет и вычищает вирусы в клоне"; ActivityDetail = "свежая утилита с сайта Kaspersky"; StageFraction = -1;
            string knote;
            R.SkipRequested = false; R.SkipLabel = "Пропустить проверку";
            Func<bool> stopK = () => R.Cancelled || R.SkipRequested;
            string kexe = Kvrt.Get(Path.Combine(Root, "tools"), s => ActivityDetail = s, stopK, out knote);
            if (R.Cancelled) { R.SkipLabel = null; return; }
            if (R.SkipRequested) SkipNote("kvrt", "Kaspersky");
            else if (kexe == null) Set("kvrt", "warn", "Kaspersky недоступен: " + (knote != "" ? knote : "нет интернета") + " — клон проверит только сама программа");
            else
            {
                var kr = Kvrt.Scan(kexe, new[] { target }, Path.Combine(Logs, "kaspersky-" + Stamp), 3, s => ActivityDetail = s, stopK);
                if (R.Cancelled) { R.SkipLabel = null; return; }
                if (R.SkipRequested) SkipNote("kvrt", "Kaspersky");
                else if (!kr.Ran) Set("kvrt", "warn", "проверка не выполнена: " + kr.Note);
                else
                {
                    var finds = kr.Detections.Where(d => !Regex.IsMatch(d, @"\b0\s+(detected|обнаружено|threats)", RegexOptions.IgnoreCase)).Distinct().ToList();
                    Set("kvrt", kr.Quarantined > 0 || finds.Count > 0 ? "warn" : "ok",
                        kr.Quarantined > 0 ? "вычищено объектов: " + kr.Quarantined + " (копии — в карантине на диске переноса)" : finds.Count > 0 ? "есть находки — смотри отчёт" : "вирусов не найдено" + (kr.Note != "" ? " · " + kr.Note : ""));
                    if (kr.Quarantined > 0 || finds.Count > 0)
                    {
                        var p = new Problem { Key = "kvrt", Title = "Kaspersky нашёл и вычистил вирусы в клоне", Severity = 2, Count = Math.Max(kr.Quarantined, finds.Count),
                            Explain = "Найденное вылечено или удалено до первого запуска клона.", Action = "Отчёт и карантин: " + kr.ReportDir + ". Исходный компьютер, с которого снимался клон, тоже заражён — вылечи его и смени пароли." };
                        foreach (var d in finds.Take(20)) p.Samples.Add(d);
                        Problems.Add(p);
                    }
                    Summary.Add("Kaspersky: " + (kr.Quarantined > 0 ? "вычищено объектов " + kr.Quarantined : finds.Count > 0 ? "есть находки" : "чисто") + ". Отчёт: " + kr.ReportDir);
                }
            }

            // ---- своя проверка: майнеры и следы, которые антивирусы пропускают (исключения Защитника, поддельные имена, задачи с кодом из интернета)
            Activity = "Проверяю клон на майнеры и следы вирусов"; ActivityDetail = "до первого запуска клона";
            R.SkipRequested = false; R.SkipLabel = "Пропустить проверку";
            RunSecurityScan("scan", target, false, "Проверка развёрнутого клона (" + Label + ") на " + Environment.MachineName, true);
            R.SkipLabel = null;
            if (R.Cancelled) return;
            if (R.SkipRequested) { R.SkipRequested = false; SkipNote("scan", "Проверка на майнеры"); }
            else if (Sec.Dangerous > 0)
            {
                int before = Sec.Dangerous, removable = Sec.Items.Count(t => t.Level >= 2 && t.File != "");
                if (removable == 0)
                {
                    Set("scan", "warn", "опасных файлов нет, остались следы: " + before + " (исключения Защитника и т. п. — см. отчёт)");
                    Summary.Add("Проверка на майнеры: файлов-вирусов нет, следы — в отчёте " + SecReportPath);
                }
                else
                {
                    Activity = "Удаляю найденное из клона"; ActivityDetail = "";
                    int n = Disarm(target);
                    Activity = "Проверяю клон ещё раз";
                    RunSecurityScan("scan", target, false, "Повторная проверка клона после удаления найденного");
                    if (R.Cancelled) return;
                    // следы без файлов (исключения Защитника, правило брандмауэра на удалённую программу) сами ничего не запускают — опасны только оставшиеся файлы
                    var still = Sec.Items.Where(t => t.Level >= 2 && t.File != "" && File.Exists(t.File)).ToList();
                    if (still.Count > 0)
                    {
                        Fail("В клоне остались вирусы", "Удалено файлов: " + n + ", но опасное осталось: " + string.Join("; ", still.Take(3).Select(t => t.What + " " + t.Path)) +
                             ". Загрузчик не записан — клон не запустится. Основная Windows не тронута. Отчёт: " + SecReportPath);
                        return;
                    }
                    Set("scan", "warn", "найдено опасного: " + before + ", удалено файлов: " + n + (Sec.Dangerous > 0 ? "; в клоне остались исключения Защитника — убери их после первого запуска" : ""));
                    Summary.Add("Проверка на майнеры: в клоне было опасного " + before + ", удалено файлов " + n + ". Отчёт: " + SecReportPath);
                }
            }
            else
            {
                Set("scan", Sec.Suspicious > 0 ? "warn" : "ok", Sec.Suspicious > 0 ? "опасного нет, подозрительного: " + Sec.Suspicious + " (см. отчёт)" : "вирусов и майнеров не найдено");
                Summary.Add("Проверка на вирусы: " + (Sec.Suspicious > 0 ? "опасного нет, подозрительного " + Sec.Suspicious : "чисто") + (Sec.DefenderUsed ? " (Защитник + правила программы)" : " (правила программы)"));
            }

            // ---- сверка системных файлов клона: подменённые или повреждённые файлы Windows восстанавливаются из хранилища самого клона
            Set("sfc", "run"); Activity = "Сверяю системные файлы клона с оригиналами Windows"; ActivityDetail = "подменённые файлы будут восстановлены"; StageFraction = 0;
            {
                var sb = new StringBuilder(); Process sfcProc = null;
                var sfcPct = new Regex(@"(\d{1,3})\s*%");
                bool sfcDone = false;
                var sfcStop = new Thread(() => { while (!sfcDone) { Thread.Sleep(1000); if (R.Cancelled) { try { if (sfcProc != null) sfcProc.Kill(); } catch { } return; } } }) { IsBackground = true };
                sfcStop.Start();
                int src = Sh.Run("sfc.exe", "/scannow /offbootdir=" + target.Substring(0, 3) + " /offwindir=" + Path.Combine(target, "Windows"), l =>
                {
                    if (string.IsNullOrWhiteSpace(l)) return;
                    string t = l.Replace("\0", "").Trim(); if (t == "") return;
                    var m = sfcPct.Match(t); if (m.Success) { StageFraction = Math.Min(1, int.Parse(m.Groups[1].Value) / 100.0); StageStat = m.Groups[1].Value + "%"; return; }
                    lock (sb) sb.AppendLine(t); ActivityDetail = t.Length > 140 ? t.Substring(0, 140) + "…" : t;
                }, Encoding.Unicode, p => sfcProc = p);
                sfcDone = true;
                if (R.Cancelled) return;
                string so = sb.ToString(); Log.Write("sfc -> " + src + " | " + (so.Length > 600 ? so.Substring(so.Length - 600) : so));
                StageFraction = -1; StageStat = "";
                if (Regex.IsMatch(so, "не обнаружила нарушений|did not find any integrity violations", RegexOptions.IgnoreCase)) Set("sfc", "ok", "системные файлы совпадают с оригиналами");
                else if (Regex.IsMatch(so, "успешно их восстановила|successfully repaired", RegexOptions.IgnoreCase))
                {
                    Set("sfc", "warn", "найдены подменённые или повреждённые системные файлы — восстановлены");
                    Summary.Add("Сверка системных файлов: найдены изменённые файлы Windows, восстановлены из оригиналов. Подробности — в C:\\Windows\\Logs\\CBS\\CBS.log этого компьютера.");
                }
                else if (Regex.IsMatch(so, "не может восстановить|unable to fix", RegexOptions.IgnoreCase))
                {
                    Set("sfc", "warn", "часть системных файлов изменена и не восстановилась — см. отчёт");
                    Problems.Add(new Problem { Key = "sfc", Title = "В клоне изменены системные файлы Windows", Severity = 2, Count = 1, Explain = "Проверка нашла файлы Windows, которые отличаются от оригинала, и не смогла их восстановить.",
                        Action = "После первого запуска клона выполни от администратора: DISM /Online /Cleanup-Image /RestoreHealth, затем sfc /scannow. Подробности — в C:\\Windows\\Logs\\CBS\\CBS.log." });
                }
                else Set("sfc", "warn", "сверка не выполнена: " + (so.Trim().Length > 0 ? so.Trim().Split('\n').Last().Trim() : "код " + src));
            }

            // ---- драйверы и настройка
            drivers = Path.Combine(Root, "images", "drivers-" + Environment.MachineName);   // диск переноса мог сменить букву
            Set("fix", "run"); Activity = "Добавляю драйверы и настраиваю клон"; StageFraction = -1; string fixNote = "";
            if (!test && Directory.Exists(drivers) && Directory.GetDirectories(drivers).Length > 0)
            {
                ActivityDetail = "драйверы этого компьютера";
                int arc = Dism("/Image:" + target + " /Add-Driver /Driver:\"" + drivers + "\" /Recurse", null);
                var mi = Regex.Match(lastDismLines, @"(\d+)\s+of\s+(\d+)");
                fixNote = arc == 0 ? "драйверы добавлены" : "часть драйверов не добавилась (" + DismWhy(arc) + ")";
            }
            // Буквы дисков в клоне записаны под старый компьютер: без очистки новая Windows может получить не «C:» и не загрузиться.
            ActivityDetail = "буквы дисков";
            string hive = Path.Combine(target, @"Windows\System32\config\SYSTEM");
            bool regOk = false;
            if (Sh.Run("reg.exe", "load HKLM\\XFERCLONE \"" + hive + "\"", null, null) == 0)
            {
                Sh.Run("reg.exe", "delete HKLM\\XFERCLONE\\MountedDevices /va /f", null, null);
                // Встроенные драйверы дисков (SATA и NVMe) включаем при загрузке: на старом компьютере нужный мог быть выключен, и на новом железе Windows не нашла бы диск.
                foreach (var svc in new[] { "storahci", "stornvme" })
                    foreach (var cs in new[] { "ControlSet001", "ControlSet002" })
                    {
                        string key = "HKLM\\XFERCLONE\\" + cs + "\\Services\\" + svc;
                        if (Sh.Run("reg.exe", "query " + key, null, null) != 0) continue;
                        Sh.Run("reg.exe", "add " + key + " /v Start /t REG_DWORD /d 0 /f", null, null);
                        Sh.Run("reg.exe", "delete " + key + "\\StartOverride /f", null, null);
                    }
                GC.Collect();
                regOk = Sh.Run("reg.exe", "unload HKLM\\XFERCLONE", null, null) == 0;
            }
            Set("fix", regOk && !fixNote.Contains("не добав") ? "ok" : "warn", (fixNote + (regOk ? "" : "; буквы дисков сбросить не удалось")).Trim(' ', ';'));

            // ---- загрузчик
            Set("boot", "run"); Activity = "Записываю загрузчик"; ActivityDetail = "";
            string bout = "";
            if (!test && hw != null && hw.BitLockerOn)
            {
                string sysl = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                int brcl = Sh.Run("manage-bde.exe", "-protectors -disable " + sysl + " -RebootCount 1", null, null);
                Log.Write("BitLocker приостановлен на одну перезагрузку: код " + brcl);
            }
            // Основная Windows — та, что запущена сейчас. Запоминаем её пункт меню: bcdboot всегда ставит новую систему первой и по умолчанию.
            string mainId = null;
            if (!test)
            {
                string cur = ""; Sh.Run("bcdedit.exe", "/enum {current} /v", l => cur += l + "\n", Sh.Oem);
                var mg = Regex.Match(cur, @"\{[0-9a-fA-F\-]{36}\}"); if (mg.Success) mainId = mg.Value;
                Log.Write("основная Windows в меню загрузки: " + mainId);
            }
            string bargs = test ? target + "Windows /s " + TestEsp + " /f UEFI" : target + "Windows /l ru-ru";
            int brc = Sh.Run("bcdboot.exe", bargs, l => bout += l + " ", Sh.Oem);
            Log.Write("bcdboot " + bargs + " -> " + brc + " " + bout);
            if (brc != 0) { Fail("Не удалось записать загрузчик", "Клон развёрнут на раздел " + target.Substring(0, 2) + ", но добавить его в меню загрузки не получилось: " + bout.Trim() + ". Текущая Windows не тронута."); return; }
            bool mainKept = true;
            if (!test)
            {
                // находим только что добавленный пункт по букве раздела, даём понятное имя и возвращаем основную Windows на первое место
                string all = ""; Sh.Run("bcdedit.exe", "/enum osloader /v", l => all += l + "\n", Sh.Oem);
                string cloneId = null;
                foreach (var block in Regex.Split(all, @"\r?\n\s*\r?\n"))
                {
                    if (block.IndexOf("partition=" + target.Substring(0, 2), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var g = Regex.Match(block, @"\{[0-9a-fA-F\-]{36}\}");
                    if (g.Success) cloneId = g.Value;
                }
                if (cloneId != null) Sh.Run("bcdedit.exe", "/set " + cloneId + " description \"Windows — " + Label + "\"", null, null);
                if (mainId != null && cloneId != null && !string.Equals(mainId, cloneId, StringComparison.OrdinalIgnoreCase))
                {
                    int d1 = Sh.Run("bcdedit.exe", "/default " + mainId, null, null);
                    int d2 = Sh.Run("bcdedit.exe", "/displayorder " + cloneId + " /addlast", null, null);
                    mainKept = d1 == 0 && d2 == 0;
                }
                else mainKept = false;
                Sh.Run("bcdedit.exe", "/timeout 15", null, null);
                Log.Write("клон в меню загрузки: " + cloneId + ", основная осталась первой: " + mainKept);
            }
            Set("boot", mainKept ? "ok" : "warn", test ? "записан на " + TestEsp : mainKept ? "клон добавлен в меню выбора Windows; основная осталась первой" : "клон добавлен в меню, но вернуть основную Windows на первое место не удалось — выбирай её в меню вручную");

            ResultLevel = 0; ResultTitle = "Вторая Windows добавлена — она появится в меню при включении";
            ResultText = "При включении компьютера теперь будет меню выбора: основная Windows (загрузится сама через 15 секунд) и «Windows — " + Label + "». " +
                         "Чтобы попасть в неё, выбери этот пункт в меню. Первая загрузка идёт дольше обычного — Windows знакомится с новым железом" + (TemplateRel != null ? " и проходит начальную настройку" : "") + ". Основная Windows не тронута.";
            Summary.Add("Образ «" + Label + "» развёрнут на раздел " + target.Substring(0, 2) + " (" + Fmt.Size(ImageBytes) + ")");
            Summary.Add("Заняло " + Fmt.Clock(DateTime.Now - Started));
            Summary.Add("Windows попросит активацию: лицензия старого компьютера на этот не переносится, нужен свой ключ.");
            Summary.Add("Обе Windows остаются. Если когда-нибудь решишь оставить только клон — запусти в нём «Перенос»: там появится кнопка «Убрать вторую Windows».");
            Summary.Add("Если что-то работает странно, удали в клоне фирменные программы старого компьютера и поставь драйверы с сайта производителя этого.");
        }
    }

    // ============================================================================================ УБРАТЬ ЗАПАСНУЮ WINDOWS
    class CloneFinishFlow : CloneFlowBase
    {
        public string FreshLetter;

        /// <summary>Запасная Windows — раздел, который программа сама пометила перед разворачиванием клона.</summary>
        public static string FindFresh()
        {
            string sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").Substring(0, 1);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string l = d.Name.Substring(0, 1);
                    if (l.Equals(sys, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(d.Name + CloneApplyFlow.Marker) && File.Exists(d.Name + @"Windows\System32\ntoskrnl.exe")) return l;
                }
                catch { }
            }
            return null;
        }

        public CloneFinishFlow(string root) { Root = root; FreshLetter = FindFresh(); Title = "Удаление запасной Windows"; }

        protected override void Execute()
        {
            Directory.CreateDirectory(Logs);
            Log.FilePath = Path.Combine(Logs, "app-" + Stamp + ".log"); Log.Write("=== удаление запасной Windows " + FreshLetter);
            AddStep("boot", "Меню загрузки"); AddStep("part", "Освобождение места");
            if (FreshLetter == null) { Fail("Запасная Windows не найдена", "На этом компьютере нет раздела, помеченного программой как запасная Windows."); return; }
            Set("boot", "run"); Activity = "Убираю запасную Windows из меню загрузки"; StageFraction = -1;
            string all = ""; Sh.Run("bcdedit.exe", "/enum osloader /v", l => all += l + "\n", Sh.Oem);
            int removed = 0;
            foreach (var block in Regex.Split(all, @"\r?\n\s*\r?\n"))
            {
                if (block.IndexOf("partition=" + FreshLetter + ":", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var g = Regex.Match(block, @"\{[0-9a-fA-F\-]{36}\}");
                if (g.Success && Sh.Run("bcdedit.exe", "/delete " + g.Value + " /f", null, null) == 0) removed++;
            }
            Sh.Run("bcdedit.exe", "/timeout 3", null, null);
            Set("boot", "ok", "удалено пунктов: " + removed);
            Set("part", "run"); Activity = "Превращаю раздел в диск для данных";
            int frc = Sh.Run("powershell.exe", "-NoProfile -NonInteractive -Command \"Format-Volume -DriveLetter " + FreshLetter + " -FileSystem NTFS -NewFileSystemLabel 'Data' -Confirm:$false | Out-Null\"", null, null);
            if (frc != 0) { Fail("Не удалось очистить раздел", "Пункт меню загрузки убран, но раздел " + FreshLetter + ": очистить не получилось. Отформатируй его вручную в «Управлении дисками»."); return; }
            Set("part", "ok", "диск " + FreshLetter + ": теперь пустой, под данные");
            ResultLevel = 0; ResultTitle = "Запасная Windows убрана"; ResultText = "Её место стало пустым диском " + FreshLetter + ": — используй его под файлы.";
            Summary.Add("Меню загрузки больше не показывается, клон загружается сразу.");
        }
    }
}
