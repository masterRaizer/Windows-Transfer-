using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Transfer
{
    /// <summary>Строка в списке этапов слева.</summary>
    class StepVm : INotifyPropertyChanged
    {
        string state = "wait", detail = "";
        public string Id { get; set; }
        public string Title { get; set; }
        /// <summary>wait | run | ok | warn | fail | skip</summary>
        public string State { get { return state; } set { state = value; Raise("State"); } }
        public string Detail { get { return detail; } set { detail = value; Raise("Detail"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
    }

    /// <summary>
    /// Оставшееся время этапа по доле выполненного: считается по скорости за последние минуты (окно сдвигается),
    /// паузы не учитываются. Пока данных мало — NaN («считаю…»).
    /// </summary>
    class StageClock
    {
        readonly Queue<KeyValuePair<DateTime, double>> samples = new Queue<KeyValuePair<DateTime, double>>();
        readonly Runner r;
        public StageClock(Runner runner) { r = runner; }

        public double Update(double fraction)
        {
            if (r != null && r.Paused) { samples.Clear(); return double.NaN; }
            var now = DateTime.Now;
            samples.Enqueue(new KeyValuePair<DateTime, double>(now, fraction));
            while (samples.Count > 2 && (now - samples.Peek().Key).TotalSeconds > 180) samples.Dequeue();
            var first = samples.Peek();
            double sec = (now - first.Key).TotalSeconds, df = fraction - first.Value;
            if (sec < 15 || df <= 0.002) return double.NaN;
            return Math.Max(0, (1 - fraction) * sec / df);
        }
    }

    /// <summary>Общая часть сценариев «сохранить» и «восстановить».</summary>
    abstract class Flow
    {
        public readonly ObservableCollection<StepVm> Steps = new ObservableCollection<StepVm>();
        public readonly Runner R = new Runner();
        public string Root;                       // папка переноса на съёмном диске
        public string Title = "";
        public volatile string Activity = "";     // что делаем прямо сейчас (крупная строка)
        public volatile string ActivityDetail = "";
        public volatile bool Copying;             // идёт этап копирования файлов — показывать байты, скорость, время
        public volatile bool Finished;
        public double StageFraction = -1;         // 0..1 для этапов без копирования; -1 — неизвестно
        public string StageStat = "";
        public double StageEta = double.NaN;      // сколько секунд осталось на этапе без копирования (NaN — неизвестно)
        public string ResultTitle = "", ResultText = "";
        public int ResultLevel;                   // 0 — хорошо, 1 — с замечаниями, 2 — не удалось
        public List<Problem> Problems = new List<Problem>();
        public List<string> Summary = new List<string>();
        public string ReportPath;
        public DateTime Started = DateTime.Now;
        protected string Stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string rootRel, markerId;

        public string Data { get { return Path.Combine(Root, "data"); } }
        public string Info { get { return Path.Combine(Data, "info"); } }
        public string RegDir { get { return Path.Combine(Data, "reg"); } }
        public string Files { get { return Path.Combine(Data, "files"); } }
        public string Logs { get { return Path.Combine(Data, "logs"); } }

        protected StepVm AddStep(string id, string title)
        {
            var s = new StepVm { Id = id, Title = title };
            App.Ui(() => Steps.Add(s));
            return s;
        }

        protected StepVm Step(string id) { return Steps.FirstOrDefault(s => s.Id == id); }

        protected void Set(string id, string state, string detail = null)
        {
            App.Ui(() => { var s = Step(id); if (s == null) return; s.State = state; if (detail != null) s.Detail = detail; });
        }

        /// <summary>Сбросить прогресс этапа без копирования: неизвестная доля, без подписи и без оставшегося времени.</summary>
        protected void ResetStage() { StageFraction = -1; StageStat = ""; StageEta = double.NaN; }

        protected void OkIfRunning(string id) { App.Ui(() => { var s = Step(id); if (s != null && s.State == "run") s.State = "ok"; }); }

        protected void Fail(string title, string text)
        {
            ResultLevel = 2; ResultTitle = title; ResultText = text;
            Log.Write("ОСТАНОВЛЕНО: " + title + " — " + text);
            App.Ui(() => { foreach (var s in Steps) if (s.State == "run") s.State = "fail"; });
        }

        /// <summary>Диск переноса может отвалиться и вернуться под другой буквой — находим его по метке в папке данных.</summary>
        protected void TrackDevice()
        {
            rootRel = Root.Substring(2);
            string marker = Path.Combine(Data, "xfer.id");
            try
            {
                Directory.CreateDirectory(Data);
                if (!File.Exists(marker)) File.WriteAllText(marker, Guid.NewGuid().ToString("N"));
                markerId = File.ReadAllText(marker).Trim();
            }
            catch { markerId = null; }
            R.DeviceOk = () => { try { return Directory.Exists(Root); } catch { return false; } };
            R.WaitDevice = () =>
            {
                while (!R.Cancelled)
                {
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (!d.IsReady) continue;
                            string cand = d.Name.TrimEnd('\\') + rootRel;
                            string m = Path.Combine(cand, "data", "xfer.id");
                            if (File.Exists(m) && (markerId == null || File.ReadAllText(m).Trim() == markerId))
                            {
                                if (!string.Equals(cand, Root, StringComparison.OrdinalIgnoreCase)) Log.Write("диск переноса вернулся под другой буквой: " + cand);
                                Root = cand; Log.FilePath = Path.Combine(Logs, "app-" + Stamp + ".log"); R.LogsDir = Logs;
                                return;
                            }
                        }
                        catch { }
                    }
                    Thread.Sleep(2000);
                }
            };
        }

        public void Start()
        {
            var t = new Thread(() =>
            {
                Native.KeepAwake(true);
                try { Execute(); }
                catch (Exception ex) { Log.Write("сбой: " + ex); Fail("Неожиданная ошибка программы", ex.Message); }
                finally { Native.KeepAwake(false); Copying = false; Finished = true; }
            });
            t.IsBackground = true; t.SetApartmentState(ApartmentState.STA); t.Start();
        }

        protected abstract void Execute();

        protected void RunCopy(string stepId, List<CopyJob> jobs)
        {
            Set(stepId, "run");
            Copying = true; Activity = "Копирую файлы";
            R.Run(jobs);
            Copying = false;
            int bad = jobs.Count(j => j.ExitCode >= 8);
            if (!R.Cancelled) Set(stepId, bad > 0 ? "warn" : "ok", Fmt.Size(R.BytesTotal) + (bad > 0 ? ", есть замечания" : ""));
        }

        protected void MeasureAll(string stepId, List<CopyJob> jobs)
        {
            Set(stepId, "run");
            long total = 0; int i = 0;
            foreach (var j in jobs)
            {
                if (R.Cancelled) return;
                i++; Activity = "Считаю, сколько нужно скопировать"; ActivityDetail = j.Name;
                StageFraction = (double)(i - 1) / jobs.Count; StageStat = "найдено " + Fmt.Size(total);
                R.Measure(j); total += j.Planned;
            }
            StageFraction = 1; StageStat = "к копированию " + Fmt.Size(total);
            Set(stepId, "ok", Fmt.Size(total));
        }
    }

    // ============================================================================================ СОХРАНЕНИЕ
    class BackupFlow : Flow
    {
        public string TestSource;                 // режим проверки: копировать только эту папку
        const string SnapLink = @"C:\__xfer_snap";
        static readonly string ShadowIdFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Perenos", "shadow.id");

        static readonly string[] RootSkip = { "Windows", "Windows.old", "System Volume Information", "$RECYCLE.BIN", "Recovery", "PerfLogs", "Config.Msi", "ESD",
            "WUDownloadCache", "WindowsApps", "WpSystem", "Documents and Settings", "msdownld.tmp", "Boot", "$Windows.~WS", "$WINDOWS.~BT", "$WinREAgent", "$SysReset", "OneDriveTemp" };
        static readonly string[] CacheNames = { "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Crashpad", "CrashDumps" };

        public BackupFlow(string root) { Root = root; Title = "Сохранение с компьютера " + Environment.MachineName; }

        static bool Skip(string name) { return RootSkip.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("__xfer", StringComparison.OrdinalIgnoreCase); }

        static bool IsUsb(string letter)
        {
            try
            {
                using (var s1 = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + letter + "'} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                    foreach (ManagementObject part in s1.Get())
                        using (var s2 = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + part["DeviceID"] + "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                            foreach (ManagementObject disk in s2.Get())
                                if (string.Equals(Convert.ToString(disk["InterfaceType"]), "USB", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        CopyJob Job(string name, string src, string orig, string rel, IEnumerable<string> xd = null)
        {
            var j = new CopyJob { Name = name, Orig = orig, Rel = rel, Src = () => src, Dst = () => Path.Combine(Files, rel) };
            if (xd != null) j.XD.AddRange(xd);
            return j;
        }

        List<CopyJob> BuildJobs(string cRoot)
        {
            var jobs = new List<CopyJob>();
            if (TestSource != null) { jobs.Add(Job(TestSource, TestSource, TestSource, "TEST")); return jobs; }
            string sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var xd = new List<string>();
            foreach (var u in Directory.GetDirectories(sys + @"\Users")) xd.Add(Path.Combine(cRoot, "Users", Path.GetFileName(u), @"AppData\Local\Temp"));
            xd.AddRange(CacheNames);
            jobs.Add(Job("Профили пользователей: документы и настройки программ", Path.Combine(cRoot, "Users"), sys + @"\Users", @"C\Users", xd));
            jobs.Add(Job("Папки программ (Program Files)", Path.Combine(cRoot, "Program Files"), sys + @"\Program Files", @"C\Program Files", new[] { Path.Combine(cRoot, @"Program Files\WindowsApps") }));
            jobs.Add(Job("Папки программ (Program Files x86)", Path.Combine(cRoot, "Program Files (x86)"), sys + @"\Program Files (x86)", @"C\Program Files (x86)"));
            string pd = Path.Combine(cRoot, "ProgramData");
            jobs.Add(Job("Общие данные программ (ProgramData)", pd, sys + @"\ProgramData", @"C\ProgramData", new[] {
                pd + @"\Microsoft\Windows Defender", pd + @"\Microsoft\Windows Defender Advanced Threat Protection", pd + @"\Microsoft\Search", pd + @"\Microsoft\Diagnosis",
                pd + @"\Microsoft\Windows\WER", pd + @"\Microsoft\Windows\Containers", pd + @"\Packages", pd + @"\USOShared", pd + @"\USOPrivate" }));
            var std = new[] { "Users", "Program Files", "Program Files (x86)", "ProgramData" };
            foreach (var d in new DirectoryInfo(sys + @"\").GetDirectories())
            {
                if (Skip(d.Name) || std.Contains(d.Name, StringComparer.OrdinalIgnoreCase) || (d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                jobs.Add(Job(sys + @"\" + d.Name, Path.Combine(cRoot, d.Name), d.FullName, @"C\" + d.Name));
            }
            string rootDrive = Root.Substring(0, 2);
            foreach (var drv in DriveInfo.GetDrives())
            {
                string l = drv.Name.Substring(0, 2);
                try { if (drv.DriveType != DriveType.Fixed || !drv.IsReady) continue; } catch { continue; }
                if (l.Equals(sys, StringComparison.OrdinalIgnoreCase) || l.Equals(rootDrive, StringComparison.OrdinalIgnoreCase) || IsUsb(l)) continue;
                DirectoryInfo[] dirs; try { dirs = new DirectoryInfo(l + @"\").GetDirectories(); } catch { continue; }
                foreach (var d in dirs)
                {
                    if (Skip(d.Name) || (d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var x = new List<string>();
                    if (d.Name.Equals("Program Files", StringComparison.OrdinalIgnoreCase)) x.Add(Path.Combine(d.FullName, "WindowsApps"));
                    if (d.Name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)) { x.Add(Path.Combine(d.FullName, @"Microsoft\Windows Defender")); x.Add(Path.Combine(d.FullName, @"Microsoft\Search")); x.Add(Path.Combine(d.FullName, "Packages")); }
                    if (d.Name.Equals("Users", StringComparison.OrdinalIgnoreCase)) x.AddRange(CacheNames);
                    jobs.Add(Job(l + @"\" + d.Name, d.FullName, d.FullName, l.Substring(0, 1) + @"\" + d.Name, x));
                }
            }
            return jobs;
        }

        /// <summary>Убрать снимок, оставшийся от прерванного запуска.</summary>
        public static void CleanupSnapshot()
        {
            try { if (Directory.Exists(SnapLink) && (new DirectoryInfo(SnapLink).Attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(SnapLink, false); } catch { }
            try
            {
                if (!File.Exists(ShadowIdFile)) return;
                string id = File.ReadAllText(ShadowIdFile).Trim();
                using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_ShadowCopy WHERE ID='" + id + "'"))
                    foreach (ManagementObject o in s.Get()) o.Delete();
                File.Delete(ShadowIdFile);
            }
            catch { }
        }

        /// <summary>Теневая копия системного диска: с неё читаются и файлы, занятые открытыми программами.</summary>
        public static bool CreateSnapshot()
        {
            try
            {
                CleanupSnapshot();
                var mc = new ManagementClass("Win32_ShadowCopy");
                var inp = mc.GetMethodParameters("Create");
                inp["Volume"] = @"C:\"; inp["Context"] = "ClientAccessible";
                var outp = mc.InvokeMethod("Create", inp, null);
                if (Convert.ToUInt32(outp["ReturnValue"]) != 0) return false;
                string id = Convert.ToString(outp["ShadowID"]);
                Directory.CreateDirectory(Path.GetDirectoryName(ShadowIdFile)); File.WriteAllText(ShadowIdFile, id);
                string dev = null;
                using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_ShadowCopy WHERE ID='" + id + "'"))
                    foreach (ManagementObject o in s.Get()) dev = Convert.ToString(o["DeviceObject"]);
                if (dev == null) return false;
                Sh.Run("cmd.exe", "/c mklink /d \"" + SnapLink + "\" \"" + dev + "\\\"", null, null);
                return Directory.Exists(Path.Combine(SnapLink, "Users"));
            }
            catch (Exception ex) { Log.Write("снимок: " + ex.Message); return false; }
        }

        protected override void Execute()
        {
            foreach (var d in new[] { Data, Info, RegDir, Logs, Files }) Directory.CreateDirectory(d);
            Log.FilePath = Path.Combine(Logs, "app-" + Stamp + ".log"); R.LogsDir = Logs; R.Stamp = Stamp;
            Log.Write("=== сохранение, компьютер " + Environment.MachineName);
            TrackDevice();
            bool test = TestSource != null;
            if (!test)
            {
                AddStep("apps", "Список программ"); AddStep("reg", "Настройки программ"); AddStep("misc", "Wi-Fi, задания, переменные"); AddStep("snap", "Снимок диска");
            }
            AddStep("scan", "Подсчёт объёма"); AddStep("copy", "Копирование файлов"); AddStep("fin", "Проверка и отчёт");
            try { File.Delete(Path.Combine(Info, "DONE.txt")); } catch { }

            string cRoot = @"C:\"; bool snap = false;
            if (!test)
            {
                // ---- сведения о программах, реестр, Wi-Fi
                string cur = null; var warns = new List<string>();
                Activity = "Составляю список программ и настроек"; ActivityDetail = "";
                Set("apps", "run");
                Sh.RunPs(Sh.ExtractScript("collect.ps1"), "-Info \"" + Info + "\" -Reg \"" + RegDir + "\"", line =>
                {
                    Log.Write("collect: " + line);
                    if (line.StartsWith("##STEP:")) { if (cur != null) OkIfRunning(cur); cur = line.Substring(7); Set(cur, "run");
                        Activity = cur == "apps" ? "Составляю список программ" : cur == "reg" ? "Сохраняю настройки программ из реестра" : "Сохраняю сети Wi-Fi и задания"; }
                    else if (line.StartsWith("##INFO:") && cur != null) Set(cur, "run", line.Substring(7));
                    else if (line.StartsWith("##WARN:") && cur != null) { warns.Add(line.Substring(7)); Set(cur, "warn", line.Substring(7)); }
                    else if (line == "##DONE" && cur != null) OkIfRunning(cur);
                });
                foreach (var id in new[] { "apps", "reg", "misc" }) { OkIfRunning(id); App.Ui(() => { var s = Step(id); if (s != null && s.State == "wait") { s.State = "warn"; s.Detail = "не выполнено"; } }); }
                if (R.Cancelled) return;

                // ---- снимок диска
                Set("snap", "run"); Activity = "Делаю снимок диска"; ActivityDetail = "чтобы скопировались и файлы, открытые в программах";
                snap = CreateSnapshot();
                if (snap) { cRoot = SnapLink + @"\"; Set("snap", "ok", "файлы открытых программ тоже скопируются"); }
                else Set("snap", "warn", "не получилось — закрой браузеры и мессенджеры, иначе часть их файлов не скопируется");
            }

            try
            {
                var jobs = BuildJobs(cRoot);
                MeasureAll("scan", jobs);
                if (R.Cancelled) return;
                long need = jobs.Sum(j => j.Planned), free = 0;
                try { free = new DriveInfo(Root.Substring(0, 3)).AvailableFreeSpace; } catch { }
                if (need > free - (2L << 30))
                {
                    Fail("На диске переноса не хватает места", "Нужно скопировать ещё " + Fmt.Size(need) + ", а свободно " + Fmt.Size(free) + ". Освободи на диске хотя бы " + Fmt.Size(need - free + (3L << 30)) +
                        " и запусти сохранение снова — уже скопированное повторно не копируется.");
                    return;
                }
                RunCopy("copy", jobs);
                if (R.Cancelled) return;

                // ---- итог
                Set("fin", "run"); Activity = "Проверяю результат"; ActivityDetail = ""; StageFraction = -1; StageStat = "";
                var man = jobs.Select(j => new Dictionary<string, object> { { "Name", j.Name }, { "Orig", j.Orig }, { "Rel", j.Rel }, { "ExitCode", j.ExitCode } }).ToList();
                if (!test) Json.WriteFile(Path.Combine(Info, "manifest.json"), man);
                List<CopyError> errs; lock (R.Errors) errs = R.Errors.ToList();
                Problems = Errors.Group(errs, Files, SnapLink + @"\", @"C:\");
                long totalAll = jobs.Sum(j => j.TotalBytes);
                Summary.Add("Компьютер: " + Environment.MachineName + ", пользователь " + Environment.UserName);
                Summary.Add("Дата: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ", заняло " + Fmt.Clock(DateTime.Now - Started));
                Summary.Add("В копии сейчас: " + Fmt.Size(totalAll) + " (" + Fmt.Count(jobs.Count, "папка", "папки", "папок") + "). За этот запуск скопировано " + Fmt.Size(R.BytesTotal) + ".");
                Summary.Add("Не скопировалось файлов: " + Problems.Sum(p => p.Count));
                Finish(totalAll, test);
                Set("fin", ResultLevel == 0 ? "ok" : "warn");
            }
            finally { if (snap) CleanupSnapshot(); }
        }

        void Finish(long totalAll, bool test)
        {
            int worst = Problems.Count == 0 ? 0 : Problems.Max(p => p.Severity);
            int n = Problems.Sum(p => p.Count);
            if (worst == 2) { ResultLevel = 1; ResultTitle = "Сохранено, но есть важные замечания"; ResultText = "Основная часть скопирована (" + Fmt.Size(totalAll) + "), но часть файлов записать не удалось. Посмотри список ниже и нажми «Досохранить»."; }
            else if (worst == 1) { ResultLevel = 1; ResultTitle = "Сохранено, " + Fmt.Count(n, "файл", "файла", "файлов") + " стоит проверить"; ResultText = "Скопировано " + Fmt.Size(totalAll) + ". Ниже написано, что не скопировалось и нужно ли с этим что-то делать."; }
            else { ResultLevel = 0; ResultTitle = "Всё сохранено"; ResultText = "Скопировано " + Fmt.Size(totalAll) + "." + (n > 0 ? " " + Fmt.Count(n, "служебный файл", "служебных файла", "служебных файлов") + " Windows не копируются в принципе — это нормально." : "") + " Теперь диск можно подключать к новому компьютеру."; }
            try
            {
                if (!test) File.WriteAllText(Path.Combine(Info, "DONE.txt"), DateTime.Now.ToString("s") + " failed=" + n + " bytes=" + totalAll, Encoding.UTF8);
                Json.WriteFile(Path.Combine(Info, "problems.json"), Problems);
                ReportPath = Path.Combine(Root, "Отчёт о сохранении.txt");
                File.WriteAllText(ReportPath, Errors.Report("ОТЧЁТ О СОХРАНЕНИИ", new[] { ResultTitle }.Concat(Summary), Problems), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Write("отчёт: " + ex.Message); }
        }
    }

    // ============================================================================================ ВОССТАНОВЛЕНИЕ
    class RestoreOptions
    {
        public bool Programs = true, Profile = true, PublicFiles = true, RegUser = true, RegSystem = false, Env = true, Other = true, Wifi = true, Tasks = false;
    }

    class AppResult
    {
        public string Id { get; set; }
        public string Status { get; set; }   // ok | had | fail
        public string Note { get; set; }
    }

    class RestoreFlow : Flow
    {
        public RestoreOptions Opt = new RestoreOptions();
        public string OldUser = "", OldProfile = "", OldComputer = "", NewUser = "", NewProfile = "";
        public bool SameAccount = true;           // программа запущена под тем же пользователем, который вошёл в Windows
        public List<AppResult> Apps = new List<AppResult>();
        public string ManualListPath;

        public RestoreFlow(string root)
        {
            Root = root;
            var m = Json.ReadFile(Path.Combine(Info, "machine.json"));
            OldUser = Json.Str(m, "User"); OldProfile = Json.Str(m, "Profile"); OldComputer = Json.Str(m, "Computer");
            NewUser = Environment.UserName;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem"))
                    foreach (ManagementObject o in s.Get()) { string u = Convert.ToString(o["UserName"]); if (!string.IsNullOrEmpty(u)) NewUser = u.Split('\\').Last(); }
            }
            catch { }
            SameAccount = string.Equals(NewUser, Environment.UserName, StringComparison.OrdinalIgnoreCase);
            string sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            NewProfile = Path.Combine(sys + @"\Users", NewUser);
            if (!Directory.Exists(NewProfile)) { NewProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); NewUser = Path.GetFileName(NewProfile); }
            Title = "Восстановление на компьютер " + Environment.MachineName;
        }

        public List<KeyValuePair<string, string>> WingetPackages()
        {
            var list = new List<KeyValuePair<string, string>>();
            var j = Json.ReadFile(Path.Combine(Info, "winget.json"));
            object srcs; if (j == null || !j.TryGetValue("Sources", out srcs)) return list;
            var skip = new[] { "Microsoft.UI.Xaml", "Microsoft.VCLibs", "Microsoft.WindowsAppRuntime", "Microsoft.DotNet.Native", "Microsoft.AppInstaller" };
            foreach (Dictionary<string, object> s in (IEnumerable)srcs)
            {
                string src = "";
                try { src = Json.Str((Dictionary<string, object>)s["SourceDetails"], "Name"); } catch { }
                foreach (Dictionary<string, object> p in (IEnumerable)s["Packages"])
                {
                    string id = Json.Str(p, "PackageIdentifier");
                    if (id == "" || skip.Any(x => id.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new KeyValuePair<string, string>(id, src));
                }
            }
            return list;
        }

        static string WingetPath()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
            return File.Exists(p) ? p : null;
        }

        static string WingetWhy(int code, string lastLine)
        {
            switch (unchecked((uint)code))
            {
                case 0x8A150014: return "такой программы больше нет в каталоге установщика";
                case 0x8A150008: case 0x80072EE7: case 0x80072EFD: case 0x80072EE2: return "не удалось скачать — проверь интернет";
                case 0x8A150011: return "скачанный установщик не прошёл проверку (файл на сайте изменился)";
                case 0x8A150010: return "эта программа не подходит для этой версии Windows";
                case 0x8A15010C: case 0x8A15010D: return "установщик программы не запустился или был отменён";
                case 0x8A150109: return "нужна перезагрузка компьютера, после неё повтори";
                default: return "установщик сообщил об ошибке (" + (string.IsNullOrWhiteSpace(lastLine) ? "код " + code : lastLine.Trim()) + ")";
            }
        }

        void InstallPrograms()
        {
            Set("apps", "run");
            var pkgs = WingetPackages(); string wg = WingetPath();
            if (wg == null) { Set("apps", "warn", "установщик winget не найден: обнови «Установщик приложения» в Microsoft Store и повтори"); return; }
            int i = 0, ok = 0, had = 0, bad = 0;
            foreach (var p in pkgs)
            {
                if (R.Cancelled) return;
                i++; Activity = "Устанавливаю программы"; ActivityDetail = p.Key;
                StageFraction = (double)(i - 1) / pkgs.Count; StageStat = i + " из " + pkgs.Count + " · установлено " + ok + (bad > 0 ? ", не удалось " + bad : "");
                string last = "";
                string args = "install --id \"" + p.Key + "\" -e " + (p.Value != "" ? "--source " + p.Value + " " : "") + "--silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
                int rc = Sh.Run(wg, args, l => { if (!string.IsNullOrWhiteSpace(l) && l.Trim().Length > 3) last = l; }, Encoding.UTF8);
                uint u = unchecked((uint)rc);
                var r = new AppResult { Id = p.Key };
                if (rc == 0) { r.Status = "ok"; r.Note = "установлено"; ok++; }
                else if (u == 0x8A15002B || u == 0x8A150061 || u == 0x8A15004F) { r.Status = "had"; r.Note = "уже было установлено"; had++; }
                else { r.Status = "fail"; r.Note = WingetWhy(rc, last); bad++; }
                Log.Write("winget " + p.Key + ": " + rc + " " + r.Note);
                lock (Apps) Apps.Add(r);
            }
            StageFraction = 1;
            Set("apps", bad > 0 ? "warn" : "ok", "установлено " + ok + ", уже было " + had + (bad > 0 ? ", не удалось " + bad : ""));
        }

        CopyJob ProfileJob()
        {
            string src = Path.Combine(Files, @"C\Users", OldUser);
            var j = new CopyJob { Name = "Файлы и настройки пользователя " + OldUser, Src = () => Path.Combine(Files, @"C\Users", OldUser), Dst = () => NewProfile, KeepNewer = true, Rel = "restore-profile" };
            // Не переносим куст реестра пользователя, ключи шифрования Windows и то, что привязано к старой системе.
            j.XF.AddRange(new[] { "NTUSER.DAT*", "ntuser.ini", "ntuser.pol", "UsrClass.dat*", "desktop.ini" });
            foreach (var rel in new[] { @"AppData\Local\Temp", @"AppData\Local\Packages", @"AppData\Local\Microsoft\Windows", @"AppData\Local\Microsoft\WindowsApps", @"AppData\Local\Microsoft\Credentials",
                @"AppData\Local\Microsoft\Vault", @"AppData\Local\Microsoft\TokenBroker", @"AppData\Local\Microsoft\OneDrive", @"AppData\Local\ConnectedDevicesPlatform", @"AppData\Local\D3DSCache",
                @"AppData\Roaming\Microsoft\Protect", @"AppData\Roaming\Microsoft\Credentials", @"AppData\Roaming\Microsoft\Crypto", @"AppData\Roaming\Microsoft\SystemCertificates",
                @"AppData\Roaming\Microsoft\Vault", @"AppData\Roaming\Microsoft\Windows\Recent", @"AppData\Roaming\Microsoft\Windows\Themes", @"AppData\Roaming\Microsoft\Windows\AccountPictures" })
                j.XD.Add(Path.Combine(src, rel));
            return j;
        }

        int ImportRegDir(string dir)
        {
            int n = 0;
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.GetFiles(dir, "*.reg")) { if (R.Cancelled) break; ActivityDetail = Path.GetFileNameWithoutExtension(f); if (Sh.Run("reg.exe", "import \"" + f + "\"", null, null) == 0) n++; }
            return n;
        }

        List<Dictionary<string, object>> OtherFolders()
        {
            var res = new List<Dictionary<string, object>>();
            try
            {
                var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var all = js.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Path.Combine(Info, "manifest.json"), Encoding.UTF8));
                var rx = new System.Text.RegularExpressions.Regex(@"^[A-Z]\\(Program Files|Program Files \(x86\)|ProgramData|Users)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var m in all) if (!rx.IsMatch(Json.Str(m, "Rel")) && Directory.Exists(Path.Combine(Files, Json.Str(m, "Rel")))) res.Add(m);
            }
            catch { }
            return res;
        }

        static List<string[]> ReadCsv(string path)
        {
            var rows = new List<string[]>();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var cells = new List<string>(); var sb = new StringBuilder(); bool q = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
                    else if (c == '"') q = true;
                    else if (c == ';') { cells.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
                cells.Add(sb.ToString()); rows.Add(cells.ToArray());
            }
            return rows;
        }

        static HashSet<string> InstalledNow()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<RegistryKey, string> scan = (root, path) =>
            {
                try
                {
                    using (var k = root.OpenSubKey(path))
                        if (k != null) foreach (var n in k.GetSubKeyNames()) using (var s = k.OpenSubKey(n)) { var d = s == null ? null : s.GetValue("DisplayName") as string; if (!string.IsNullOrEmpty(d)) set.Add(d); }
                }
                catch { }
            };
            const string u = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            using (var h = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) scan(h, u);
            using (var h = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)) scan(h, u);
            using (var h = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)) scan(h, u);
            return set;
        }

        /// <summary>Список программ со старого компьютера, которых здесь всё ещё нет.</summary>
        int WriteManualList()
        {
            string csv = Path.Combine(Info, "programs.csv");
            if (!File.Exists(csv)) return 0;
            var rows = ReadCsv(csv); if (rows.Count < 2) return 0;
            var head = rows[0].ToList(); int iN = head.IndexOf("DisplayName"), iV = head.IndexOf("DisplayVersion"), iP = head.IndexOf("Publisher"), iL = head.IndexOf("InstallLocation");
            var now = InstalledNow(); var sb = new StringBuilder(); int miss = 0, total = rows.Count - 1;
            var body = new StringBuilder();
            foreach (var r in rows.Skip(1))
            {
                if (iN < 0 || iN >= r.Length || now.Contains(r[iN])) continue;
                miss++;
                body.AppendLine(r[iN] + (iV >= 0 && iV < r.Length ? "  " + r[iV] : "") + (iP >= 0 && iP < r.Length && r[iP] != "" ? "  [" + r[iP] + "]" : ""));
                if (iL >= 0 && iL < r.Length && r[iL].Length > 3 && r[iL][1] == ':')
                {
                    string saved = Path.Combine(Files, r[iL].Substring(0, 1).ToUpper(), r[iL].Substring(3).TrimEnd('\\'));
                    if (Directory.Exists(saved)) body.AppendLine("      сохранённая папка программы: " + saved);
                }
            }
            sb.AppendLine("Программы со старого компьютера (" + OldComputer + "), которых здесь пока нет: " + miss + " из " + total);
            sb.AppendLine("Составлено: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm")); sb.AppendLine();
            sb.AppendLine("Их нужно поставить установщиком с сайта программы. Настройки этих программ уже перенесены."); sb.AppendLine();
            sb.Append(body);
            ManualListPath = Path.Combine(Root, "Осталось установить вручную.txt");
            File.WriteAllText(ManualListPath, sb.ToString(), new UTF8Encoding(true));
            return miss;
        }

        protected override void Execute()
        {
            Directory.CreateDirectory(Logs);
            Log.FilePath = Path.Combine(Logs, "app-" + Stamp + ".log"); R.LogsDir = Logs; R.Stamp = Stamp;
            Log.Write("=== восстановление на " + Environment.MachineName + ", пользователь " + NewUser + " (было: " + OldComputer + "\\" + OldUser + ")");
            TrackDevice();
            if (Opt.Programs) AddStep("apps", "Установка программ");
            bool files = Opt.Profile || Opt.PublicFiles || Opt.Other;
            if (files) { AddStep("scan", "Подсчёт объёма"); AddStep("copy", "Копирование файлов и настроек"); }
            if (Opt.RegUser || Opt.RegSystem) AddStep("reg", "Настройки программ (реестр)");
            if (Opt.Wifi || Opt.Tasks) AddStep("net", "Wi-Fi и задания");
            AddStep("fin", "Проверка и отчёт");

            if (Opt.Programs) InstallPrograms();
            if (R.Cancelled) return;

            if (files)
            {
                var jobs = new List<CopyJob>();
                if (Opt.Profile && Directory.Exists(Path.Combine(Files, @"C\Users", OldUser)))
                {
                    // Имя пользователя другое: старый путь профиля делаем ссылкой на новый — программы хранят в настройках полные пути.
                    if (!string.Equals(OldUser, NewUser, StringComparison.OrdinalIgnoreCase) && OldProfile.Length > 3 && !Directory.Exists(OldProfile))
                    { Sh.Run("cmd.exe", "/c mklink /J \"" + OldProfile + "\" \"" + NewProfile + "\"", null, null); Log.Write("ссылка " + OldProfile + " -> " + NewProfile); }
                    jobs.Add(ProfileJob());
                }
                if (Opt.PublicFiles && Directory.Exists(Path.Combine(Files, @"C\Users\Public")))
                {
                    var pj = new CopyJob { Name = "Общие файлы (Public)", Src = () => Path.Combine(Files, @"C\Users\Public"), Dst = () => Path.Combine(Path.GetDirectoryName(NewProfile), "Public"), KeepNewer = true, Rel = "restore-public" };
                    pj.XF.Add("desktop.ini"); jobs.Add(pj);
                }
                if (Opt.Other)
                    foreach (var m in OtherFolders())
                    {
                        string orig = Json.Str(m, "Orig"), rel = Json.Str(m, "Rel");
                        string dst = Directory.Exists(orig.Substring(0, 3)) ? orig : Path.Combine(@"C:\Перенесено", orig.Substring(0, 1), orig.Substring(3));
                        jobs.Add(new CopyJob { Name = orig, Src = () => Path.Combine(Files, rel), Dst = () => dst, KeepNewer = true, Rel = "restore-" + rel });
                    }
                MeasureAll("scan", jobs);
                if (R.Cancelled) return;
                long need = jobs.Sum(j => j.Planned), free = 0;
                try { free = new DriveInfo(NewProfile.Substring(0, 3)).AvailableFreeSpace; } catch { }
                if (need > free - (5L << 30))
                {
                    Fail("На этом компьютере не хватает места", "Нужно скопировать " + Fmt.Size(need) + ", а на диске " + NewProfile.Substring(0, 2) + " свободно " + Fmt.Size(free) + ". Освободи место или сними галочки с части пунктов и запусти снова.");
                    return;
                }
                RunCopy("copy", jobs);
                if (R.Cancelled) return;
            }

            if (Opt.RegUser || Opt.RegSystem)
            {
                Set("reg", "run"); Activity = "Переношу настройки программ"; StageFraction = -1; StageStat = "";
                int n = 0; string note = "";
                if (Opt.RegUser && SameAccount)
                {
                    Sh.Run("reg.exe", "export HKCU\\Software \"" + Path.Combine(Logs, "hkcu-before-restore-" + Stamp + ".reg") + "\" /y", null, null);
                    n += ImportRegDir(Path.Combine(RegDir, "hkcu")) + ImportRegDir(Path.Combine(RegDir, "hkcu_ms"));
                    if (Opt.Env) Sh.Run("reg.exe", "import \"" + Path.Combine(RegDir, "user-environment.reg") + "\"", null, null);
                }
                else if (Opt.RegUser) note = "настройки пользователя пропущены: программа запущена под другой учётной записью";
                if (Opt.RegSystem) n += ImportRegDir(Path.Combine(RegDir, "hklm")) + ImportRegDir(Path.Combine(RegDir, "hklm32"));
                Set("reg", note == "" ? "ok" : "warn", note == "" ? "перенесены настройки " + n + " программ" : note);
            }
            if (R.Cancelled) return;

            if (Opt.Wifi || Opt.Tasks)
            {
                Set("net", "run"); Activity = "Добавляю сети Wi-Fi и задания"; int w = 0, t = 0;
                if (Opt.Wifi && Directory.Exists(Path.Combine(Info, "wifi")))
                    foreach (var f in Directory.GetFiles(Path.Combine(Info, "wifi"), "*.xml")) { ActivityDetail = Path.GetFileNameWithoutExtension(f); if (Sh.Run("netsh.exe", "wlan add profile filename=\"" + f + "\" user=all", null, null) == 0) w++; }
                if (Opt.Tasks && Directory.Exists(Path.Combine(Info, "tasks")))
                    foreach (var f in Directory.GetFiles(Path.Combine(Info, "tasks"), "*.xml")) { ActivityDetail = Path.GetFileNameWithoutExtension(f); if (Sh.Run("schtasks.exe", "/create /tn \"" + Path.GetFileNameWithoutExtension(f) + "\" /xml \"" + f + "\" /f", null, null) == 0) t++; }
                Set("net", "ok", "сетей Wi-Fi: " + w + (Opt.Tasks ? ", заданий: " + t : ""));
            }

            // ---- итог
            Set("fin", "run"); Activity = "Проверяю результат"; ActivityDetail = "";
            int miss = 0; try { miss = WriteManualList(); } catch (Exception ex) { Log.Write("список: " + ex.Message); }
            List<CopyError> errs; lock (R.Errors) errs = R.Errors.ToList();
            Problems = Errors.Group(errs, NewProfile.Substring(0, 3));
            int failedApps; lock (Apps) failedApps = Apps.Count(a => a.Status == "fail");
            Summary.Add("Со старого компьютера: " + OldComputer + ", пользователь " + OldUser);
            Summary.Add("На этот компьютер: " + Environment.MachineName + ", пользователь " + NewUser);
            Summary.Add("Дата: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ", заняло " + Fmt.Clock(DateTime.Now - Started));
            if (Opt.Programs) lock (Apps) Summary.Add("Программы: установлено " + Apps.Count(a => a.Status == "ok") + ", уже было " + Apps.Count(a => a.Status == "had") + ", не удалось " + failedApps);
            if (files) Summary.Add("Скопировано файлов и настроек: " + Fmt.Size(R.BytesTotal));
            Summary.Add("Осталось поставить вручную: " + miss + " программ (список — в файле «Осталось установить вручную.txt»)");
            lock (Apps) foreach (var a in Apps.Where(a => a.Status == "fail")) Summary.Add("   не установилась " + a.Id + ": " + a.Note);
            int worst = Problems.Count == 0 ? 0 : Problems.Max(p => p.Severity);
            if (worst == 2) { ResultLevel = 1; ResultTitle = "Перенесено, но есть важные замечания"; }
            else if (worst == 1 || failedApps > 0) { ResultLevel = 1; ResultTitle = "Перенесено, есть что проверить"; }
            else { ResultLevel = 0; ResultTitle = "Перенос завершён"; }
            ResultText = "Перезагрузи компьютер, чтобы настройки вступили в силу. " + (miss > 0 ? "Ещё " + Fmt.Count(miss, "программу", "программы", "программ") + " нужно поставить вручную — список открыт кнопкой ниже. " : "") +
                         "Пароли из браузеров файлами не переносятся: войди в аккаунт браузера, и они подтянутся синхронизацией.";
            try
            {
                ReportPath = Path.Combine(Root, "Отчёт о восстановлении.txt");
                File.WriteAllText(ReportPath, Errors.Report("ОТЧЁТ О ВОССТАНОВЛЕНИИ", new[] { ResultTitle }.Concat(Summary), Problems), new UTF8Encoding(true));
            }
            catch { }
            Set("fin", ResultLevel == 0 ? "ok" : "warn");
        }
    }
}
