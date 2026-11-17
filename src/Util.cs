using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Transfer
{
    /// <summary>Форматирование размеров, времени и путей для показа пользователю.</summary>
    static class Fmt
    {
        public static string Size(double bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes >= 1024.0 * 1024 * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024 * 1024)).ToString("0.00") + " ТБ";
            if (bytes >= 1024.0 * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " ГБ";
            if (bytes >= 1024.0 * 1024) return (bytes / (1024.0 * 1024)).ToString("0") + " МБ";
            if (bytes >= 1024.0) return (bytes / 1024.0).ToString("0") + " КБ";
            return bytes.ToString("0") + " Б";
        }

        public static string Speed(double bytesPerSec)
        {
            return bytesPerSec < 1 ? "—" : (bytesPerSec / (1024.0 * 1024)).ToString("0") + " МБ/с";
        }

        public static string Clock(TimeSpan t)
        {
            return ((int)t.TotalHours).ToString("0") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        /// <summary>«≈ 1 ч 12 мин», «≈ 4 мин», «меньше минуты».</summary>
        public static string Eta(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "считаю…";
            if (seconds < 50) return "< 1 мин";
            int min = (int)Math.Round(seconds / 60.0);
            if (min < 60) return "≈ " + min + " мин";
            return "≈ " + (min / 60) + " ч " + (min % 60) + " мин";
        }

        public static string Count(long n, string one, string few, string many)
        {
            long a = Math.Abs(n) % 100, b = a % 10;
            string w = (a > 10 && a < 20) ? many : b == 1 ? one : (b >= 2 && b <= 4) ? few : many;
            return n.ToString("N0") + " " + w;
        }

        /// <summary>Длинный путь сокращается в середине: C:\Users\…\folder\file.txt</summary>
        public static string ShortPath(string p, int max)
        {
            if (string.IsNullOrEmpty(p) || p.Length <= max) return p ?? "";
            int tail = max * 2 / 3, head = max - tail - 1;
            return p.Substring(0, head) + "…" + p.Substring(p.Length - tail);
        }
    }

    static class Native
    {
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll")] public static extern int GetOEMCP();
        [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool FlashWindow(IntPtr hwnd, bool invert);

        /// <summary>Не давать компьютеру уснуть, пока идёт перенос.</summary>
        public static void KeepAwake(bool on)
        {
            const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
            SetThreadExecutionState(on ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED) : ES_CONTINUOUS);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);

        /// <summary>Сколько байт процесс прочитал и записал с начала работы (-1, если узнать не удалось).</summary>
        public static void Io(Process p, out long read, out long written)
        {
            read = -1; written = -1;
            try { IoCounters c; if (p != null && !p.HasExited && GetProcessIoCounters(p.Handle, out c)) { read = (long)c.ReadBytes; written = (long)c.WriteBytes; } } catch { }
        }

        public static void Suspend(Process p, bool suspend)
        {
            try { if (p != null && !p.HasExited) { if (suspend) NtSuspendProcess(p.Handle); else NtResumeProcess(p.Handle); } } catch { }
        }

        // ---- дерево процессов: DISM работает через дочерний DismHost.exe, Sentinel и MpCmdRun тоже могут запускать дочерние процессы
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry32
        {
            public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>Все потомки процесса (дети, внуки…), без него самого.</summary>
        public static List<int> Descendants(int root)
        {
            var parent = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return new List<int>();
            try
            {
                var e = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
                if (Process32FirstW(snap, ref e))
                    do { parent[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; } while (Process32NextW(snap, ref e));
            }
            catch { }
            finally { CloseHandle(snap); }
            var result = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(root);
            while (queue.Count > 0 && result.Count < 500)
            {
                int cur = queue.Dequeue();
                foreach (var kv in parent)
                    if (kv.Value == cur && kv.Key != cur && kv.Key != root && !result.Contains(kv.Key)) { result.Add(kv.Key); queue.Enqueue(kv.Key); }
            }
            return result;
        }

        /// <summary>Заморозить или разморозить процесс вместе со всеми его потомками.</summary>
        public static void SuspendTree(Process p, bool suspend)
        {
            if (p == null) return;
            List<int> kids; try { if (p.HasExited) return; kids = Descendants(p.Id); } catch { return; }
            Suspend(p, suspend);
            foreach (int id in kids) { try { using (var c = Process.GetProcessById(id)) Suspend(c, suspend); } catch { } }
        }

        /// <summary>Завершить процесс вместе со всеми его потомками (копии, которые он запустил, иначе продолжили бы работу).</summary>
        public static void KillTree(Process p)
        {
            if (p == null) return;
            List<int> kids; try { if (p.HasExited) return; kids = Descendants(p.Id); } catch { return; }
            try { NtResumeProcess(p.Handle); p.Kill(); } catch { }
            foreach (int id in kids) { try { using (var c = Process.GetProcessById(id)) { NtResumeProcess(c.Handle); c.Kill(); } } catch { } }
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            try { int v = 2; DwmSetWindowAttribute(hwnd, 33, ref v, 4); int dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, 4); } catch { }
        }
    }

    /// <summary>Запуск внешних программ без окна с построчным чтением вывода.</summary>
    static class Sh
    {
        public static Encoding Oem
        {
            get { try { return Encoding.GetEncoding(Native.GetOEMCP()); } catch { return Encoding.Default; } }
        }

        // Долгие утилиты, которые замораживаются кнопкой «Пауза». Короткие служебные (reg, powershell с разметкой диска,
        // bcdedit) не трогаем: замороженная посреди изменения раздела команда хуже, чем несколько секунд работы.
        static readonly HashSet<string> Pausable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "robocopy.exe", "dism.exe", "sfc.exe", "MpCmdRun.exe", "Sentinel.Cli.exe" };
        static readonly List<Process> Active = new List<Process>();
        static volatile bool paused;

        /// <summary>Пауза всех долгих утилит. Пока пауза, новые долгие утилиты не запускаются.</summary>
        public static void SetPaused(bool pause)
        {
            paused = pause;
            Process[] list; lock (Active) list = Active.ToArray();
            foreach (var p in list) Native.SuspendTree(p, pause);
        }

        /// <summary>
        /// Запуск программы без окна с построчным чтением вывода. stop — когда вернёт true, программа завершается вместе
        /// со всеми процессами, которые она запустила (отмена, «Пропустить проверку»). Возвращает код выхода.
        /// </summary>
        public static int Run(string exe, string args, Action<string> onLine, Encoding enc, Action<Process> started = null, Func<bool> stop = null)
        {
            bool pausable = Pausable.Contains(Path.GetFileName(exe));
            while (pausable && paused && (stop == null || !stop())) System.Threading.Thread.Sleep(200);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = enc ?? Oem, StandardErrorEncoding = enc ?? Oem
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) { try { onLine(e.Data); } catch { } } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && onLine != null) { try { onLine(e.Data); } catch { } } };
                p.Start();
                if (pausable) lock (Active) Active.Add(p);
                try
                {
                    if (started != null) started(p);
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    bool killed = false;
                    while (!p.WaitForExit(500))
                        if (!killed && stop != null && stop()) { killed = true; Log.Write("останавливаю " + Path.GetFileName(exe) + " вместе с дочерними процессами"); Native.KillTree(p); }
                    p.WaitForExit();     // дочитать вывод до конца
                    return p.ExitCode;
                }
                finally { if (pausable) lock (Active) Active.Remove(p); }
            }
        }

        public static string Quote(string s)
        {
            // Путь, заканчивающийся на «\», внутри кавычек съедает закрывающую кавычку — убираем хвостовой слэш.
            s = s.TrimEnd('\\');
            if (s.Length == 2 && s[1] == ':') s += "\\\\";
            return "\"" + s + "\"";
        }

        /// <summary>Достаёт встроенный PowerShell-скрипт во временную папку (с BOM, чтобы кириллица читалась).</summary>
        public static string ExtractScript(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "Perenos");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            using (var s = typeof(Sh).Assembly.GetManifestResourceStream("Transfer.ps." + name))
            using (var r = new StreamReader(s, Encoding.UTF8))
                File.WriteAllText(path, r.ReadToEnd(), new UTF8Encoding(true));
            return path;
        }

        public static int RunPs(string script, string args, Action<string> onLine)
        {
            string a = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" " + args;
            return Run("powershell.exe", a, onLine, Encoding.UTF8);
        }
    }

    /// <summary>Простой журнал приложения рядом с данными переноса.</summary>
    static class Log
    {
        static readonly object Gate = new object();
        public static string FilePath;

        public static void Write(string line)
        {
            if (FilePath == null) return;
            lock (Gate)
            {
                try { File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }
    }

    static class Json
    {
        public static Dictionary<string, object> ReadFile(string path)
        {
            try
            {
                var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { return null; }
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v; return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        public static void WriteFile(string path, object value)
        {
            var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            File.WriteAllText(path, js.Serialize(value), new UTF8Encoding(false));
        }
    }
}
