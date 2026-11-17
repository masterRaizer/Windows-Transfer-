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

        public static int Run(string exe, string args, Action<string> onLine, Encoding enc, Action<Process> started = null)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = enc ?? Oem, StandardErrorEncoding = enc ?? Oem
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
                p.Start();
                if (started != null) started(p);
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
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
