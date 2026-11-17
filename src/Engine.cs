using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Transfer
{
    /// <summary>Одно задание копирования: папка-источник -> папка-приёмник.</summary>
    class CopyJob
    {
        public string Name;
        public Func<string> Src;
        public Func<string> Dst;
        public string Orig = "";       // исходный путь на старом компьютере (для манифеста)
        public string Rel = "";        // путь внутри data\files
        public List<string> XD = new List<string>();
        public List<string> XF = new List<string>();
        public bool KeepNewer;         // /XO — не перезаписывать файлы, которые в приёмнике новее
        public long Planned;           // байт предстоит скопировать
        public long PlannedFiles;
        public long TotalBytes;        // всего байт в источнике
        public int ExitCode;
    }

    /// <summary>Запускает robocopy, считает прогресс, скорость и оставшееся время, собирает ошибки.</summary>
    class Runner
    {
        public volatile string JobName = "";
        public volatile string CurrentFile = "";
        public volatile bool Paused, WaitingDevice, Cancelled;
        public volatile string SkipLabel;          // не null — на текущем этапе показывается кнопка «пропустить» с этой подписью
        public volatile bool SkipRequested;        // пользователь нажал «пропустить» — этап сворачивается, перенос идёт дальше
        public long BytesDone, BytesTotal, FilesDone, FilesTotal;
        public long BytesAfter;                    // объём следующего этапа с той же скоростью — входит в оставшееся время
        public double Speed;                       // байт/с, сглаженная
        public int ErrorCount;
        public int JobIndex, JobCount;
        public volatile string CountLabel, FilesLabel;   // подписи вместо «папка N из M» и «N файлов» (для клонирования)
        public Action OnCancel; public Action<bool> OnPause;
        public readonly List<CopyError> Errors = new List<CopyError>();
        public Func<bool> DeviceOk = () => true;   // подключён ли диск переноса
        public Action WaitDevice = () => { };      // ждать, пока диск вернётся
        public string LogsDir, Stamp;
        public DateTime Started;
        public TimeSpan PausedTotal;

        Process current;
        readonly object gate = new object();
        static readonly Regex FileLine = new Regex(@"^\s+(\d+)\s+([A-Za-z]:\\.+|\\\\.+)$", RegexOptions.Compiled);
        static readonly Regex SumLine = new Regex(@"^\s*\S[^:]*:\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.Compiled);

        static string BuildArgs(CopyJob j, string src, string dst, bool listOnly, string log)
        {
            var sb = new StringBuilder();
            sb.Append(Sh.Quote(src)).Append(' ').Append(Sh.Quote(dst));
            sb.Append(" /E /XJ /B /XX /NP /NDL /NC /BYTES /NJH");
            if (listOnly) sb.Append(" /L /NFL /R:0 /W:0");
            else sb.Append(" /COPY:DAT /DCOPY:DAT /R:1 /W:1 ").Append(Environment.GetEnvironmentVariable("XFER_SLOW") == "1" ? "/IPG:15" : "/MT:16").Append(" /FP /TEE /UNILOG:").Append(Sh.Quote(log));
            if (j.KeepNewer) sb.Append(" /XO");
            if (j.XD.Count > 0) { sb.Append(" /XD"); foreach (var d in j.XD) sb.Append(' ').Append(Sh.Quote(d)); }
            if (j.XF.Count > 0) { sb.Append(" /XF"); foreach (var f in j.XF) sb.Append(' ').Append(Sh.Quote(f)); }
            return sb.ToString();
        }

        /// <summary>Пробный прогон: сколько байт и файлов предстоит скопировать (уже скопированное не считается).</summary>
        public void Measure(CopyJob j)
        {
            j.Planned = 0; j.PlannedFiles = 0; j.TotalBytes = 0;
            string src = j.Src();
            if (!Directory.Exists(src)) return;
            var rows = new List<long[]>();
            Sh.Run("robocopy.exe", BuildArgs(j, src, j.Dst(), true, null), line =>
            {
                var m = SumLine.Match(line);
                if (m.Success) lock (rows) rows.Add(new[] { long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value) });
            }, Sh.Oem, p => { lock (gate) current = p; });
            lock (gate) current = null;
            // строки итога идут в порядке: каталоги, файлы, байты
            if (rows.Count >= 3) { j.PlannedFiles = rows[1][1]; j.TotalBytes = rows[2][0]; j.Planned = rows[2][1]; }
        }

        static long FreeOn(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace; } catch { return -1; }
        }

        public void Pause(bool pause)
        {
            Paused = pause;
            lock (gate) Native.Suspend(current, pause);
            if (OnPause != null) OnPause(pause);
        }

        public void Cancel()
        {
            Cancelled = true;
            if (OnCancel != null) { try { OnCancel(); } catch { } }
            lock (gate) { try { if (current != null && !current.HasExited) { Native.Suspend(current, false); current.Kill(); } } catch { } }
        }

        void KillCurrent()
        {
            lock (gate) { try { if (current != null && !current.HasExited) current.Kill(); } catch { } }
        }

        public void Run(List<CopyJob> jobs)
        {
            BytesTotal = jobs.Sum(j => j.Planned); FilesTotal = jobs.Sum(j => j.PlannedFiles);
            BytesDone = 0; FilesDone = 0; Speed = 0; JobCount = jobs.Count; Started = DateTime.Now; PausedTotal = TimeSpan.Zero;
            long baseDone = 0;
            for (int i = 0; i < jobs.Count && !Cancelled; i++)
            {
                var j = jobs[i];
                JobIndex = i + 1; JobName = j.Name;
                if (!Directory.Exists(j.Src())) { j.ExitCode = 0; continue; }
                while (!Cancelled)
                {
                    var jobErrors = new List<CopyError>();
                    string dst = j.Dst();
                    try { Directory.CreateDirectory(dst); } catch { }
                    long free0 = FreeOn(dst), jobBase = baseDone, filesBase = FilesDone;
                    var samples = new Queue<KeyValuePair<DateTime, long>>();
                    bool lostDevice = false;
                    string log = Path.Combine(LogsDir, "robocopy-" + Stamp + "-" + Regex.Replace(j.Rel == "" ? j.Name : j.Rel, @"[\\/: ]", "_") + ".log");

                    // Раз в полсекунды: прогресс по убыли свободного места на приёмнике (плавный даже на огромных файлах), скорость, связь с диском.
                    using (var timer = new Timer(_ =>
                    {
                        if (Paused) { PausedTotal += TimeSpan.FromMilliseconds(500); lock (samples) samples.Clear(); Speed = 0; return; }
                        if (!DeviceOk()) { lostDevice = true; KillCurrent(); return; }
                        long free = FreeOn(dst);
                        if (free0 >= 0 && free >= 0)
                        {
                            long d = Math.Max(0, Math.Min(j.Planned, free0 - free));
                            long now = jobBase + d;
                            if (now > Interlocked.Read(ref BytesDone)) Interlocked.Exchange(ref BytesDone, now);
                        }
                        lock (samples)
                        {
                            samples.Enqueue(new KeyValuePair<DateTime, long>(DateTime.Now, Interlocked.Read(ref BytesDone)));
                            while (samples.Count > 60) samples.Dequeue();     // окно 30 секунд
                            if (samples.Count >= 4)
                            {
                                var first = samples.Peek(); var last = samples.Last();
                                double sec = (last.Key - first.Key).TotalSeconds;
                                if (sec > 0.5) Speed = Math.Max(0, (last.Value - first.Value) / sec);
                            }
                        }
                    }, null, 500, 500))
                    {
                        j.ExitCode = Sh.Run("robocopy.exe", BuildArgs(j, j.Src(), dst, false, log), line =>
                        {
                            var m = FileLine.Match(line);
                            if (m.Success) { CurrentFile = m.Groups[2].Value; Interlocked.Increment(ref FilesDone); return; }
                            CopyError e;
                            if (Errors_TryParse(line, j, out e)) { lock (jobErrors) jobErrors.Add(e); Interlocked.Increment(ref ErrorCount); }
                        }, Sh.Oem, p => { lock (gate) current = p; });
                    }
                    lock (gate) current = null;
                    if (Cancelled) return;

                    if (lostDevice || !DeviceOk())
                    {
                        // Диск переноса пропал: ошибки этой попытки не считаем, ждём диск и повторяем задание (уже скопированное пропустится).
                        Interlocked.Add(ref ErrorCount, -jobErrors.Count);
                        Interlocked.Exchange(ref FilesDone, filesBase);
                        Log.Write("диск переноса отключился во время «" + j.Name + "» — жду");
                        WaitingDevice = true; Speed = 0;
                        WaitDevice();
                        WaitingDevice = false;
                        // часть задания уже скопирована до обрыва — пересчитываем остаток, чтобы процент и время были точными
                        long was = j.Planned; Measure(j); if (j.Planned > was) j.Planned = was;
                        baseDone += was - j.Planned;
                        continue;
                    }
                    // точные пути ошибок берём из журнала в Юникоде (вывод в консоль — в старой кодировке, редкие символы там теряются)
                    var exact = Transfer.Errors.ParseLog(log, j.Name);
                    lock (Errors) Errors.AddRange(exact.Count > 0 ? exact : jobErrors);
                    Log.Write("задание «" + j.Name + "»: код " + j.ExitCode + ", файлов " + (Interlocked.Read(ref FilesDone) - filesBase) + ", ошибок " + (exact.Count > 0 ? exact.Count : jobErrors.Count));
                    break;
                }
                baseDone += j.Planned;
                if (baseDone > Interlocked.Read(ref BytesDone)) Interlocked.Exchange(ref BytesDone, baseDone);
            }
            CurrentFile = ""; Speed = 0;
        }

        static bool Errors_TryParse(string line, CopyJob j, out CopyError e)
        {
            return Transfer.Errors.TryParse(line, j.Name, out e);
        }

        /// <summary>Сколько секунд осталось при текущей скорости.</summary>
        public double EtaSeconds
        {
            get
            {
                double sp = Speed; long left = BytesTotal - Interlocked.Read(ref BytesDone) + Math.Max(0, Interlocked.Read(ref BytesAfter));
                if (left <= 0) return 0;
                return sp < 1024 ? double.NaN : left / sp;
            }
        }

        public TimeSpan Elapsed { get { return DateTime.Now - Started - PausedTotal; } }
    }
}
