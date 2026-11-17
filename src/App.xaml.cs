using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace Transfer
{
    /// <summary>Уборка следов, если прошлый запуск оборвался (выключили питание, сняли задачу).</summary>
    static class Maintenance
    {
        public static void CleanupLeftovers()
        {
            // временно подключённые кусты реестра других Windows
            try
            {
                foreach (var n in Microsoft.Win32.Registry.LocalMachine.GetSubKeyNames())
                    if (n == "XFERCLONE" || n.StartsWith("XFERW")) Sh.Run("reg.exe", "unload HKLM\\" + n, null, null);
            }
            catch { }
            // снимок диска и ссылка на него
            try { BackupFlow.CleanupSnapshot(); } catch { }
            // рабочая папка DISM
            try { string sc = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Perenos", "scratch"); if (System.IO.Directory.Exists(sc)) System.IO.Directory.Delete(sc, true); } catch { }
        }
    }

    public partial class App : Application
    {
        public static string Root;       // папка переноса на съёмном диске
        public static string ShotDir;    // служебное: снять картинки экранов и выйти
        public static string TestSrc;    // служебное: сохранять только одну папку
        public static string Auto; public static string LiveDir; public static string CloneSrc, ApplyDir, ApplyEsp, CloneCompress, TemplateFile; public static int Pick;       // служебное: сразу запустить «backup»
        public static bool ExitWhenDone;
        static System.Threading.Mutex single;

        /// <summary>Выполнить действие в потоке интерфейса.</summary>
        public static void Ui(Action a)
        {
            try
            {
                var d = Current == null ? null : Current.Dispatcher;
                if (d == null || d.CheckAccess()) a(); else d.Invoke(a);
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string pass = "";
            for (int i = 0; i < e.Args.Length; i++)
            {
                string a = e.Args[i]; string next = i + 1 < e.Args.Length ? e.Args[i + 1] : null;
                if (a == "--root" && next != null) { Root = next; i++; }
                else if (a == "--shot" && next != null) { ShotDir = next; pass += " --shot \"" + next + "\""; i++; }
                else if (a == "--testsrc" && next != null) { TestSrc = next; pass += " --testsrc \"" + next + "\""; i++; }
                else if (a == "--auto" && next != null) { Auto = next; pass += " --auto " + next; i++; }
                else if (a == "--live" && next != null) { LiveDir = next; pass += " --live \"" + next + "\""; i++; }
                else if (a == "--clonesrc" && next != null) { CloneSrc = next; pass += " --clonesrc " + next; i++; }
                else if (a == "--compress" && next != null) { CloneCompress = next; pass += " --compress " + next; i++; }
                else if (a == "--template" && next != null) { TemplateFile = next; pass += " --template \"" + next + "\""; i++; }
                else if (a == "--pick" && next != null) { int.TryParse(next, out Pick); pass += " --pick " + next; i++; }
                else if (a == "--applydir" && next != null) { ApplyDir = next; pass += " --applydir " + next; i++; }
                else if (a == "--esp" && next != null) { ApplyEsp = next; pass += " --esp " + next; i++; }
                else if (a == "--exit") { ExitWhenDone = true; pass += " --exit"; }
            }
            DispatcherUnhandledException += (s, ev) =>
            {
                Log.Write("сбой интерфейса: " + ev.Exception);
                MessageBox.Show("В программе произошла ошибка:\n\n" + ev.Exception.Message + "\n\nСкопированные файлы не пострадали. Запусти программу ещё раз — перенос продолжится с того же места.",
                    "Перенос", MessageBoxButton.OK, MessageBoxImage.Warning);
                ev.Handled = true;
            };

            if (Root == null)
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                Root = Path.GetDirectoryName(exe).TrimEnd('\\');
                // Программа лежит на съёмном диске. Если диск отключится, Windows закрыла бы и её, поэтому работаем с копии на этом компьютере.
                try
                {
                    string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Perenos");
                    Directory.CreateDirectory(local);
                    string copy = Path.Combine(local, "Perenos.exe");
                    if (!string.Equals(exe, copy, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(exe, copy, true);
                        Process.Start(new ProcessStartInfo(copy, "--root \"" + Root + "\"" + pass) { UseShellExecute = false });
                        Shutdown(); return;
                    }
                }
                catch { }
            }
            bool first;
            single = new System.Threading.Mutex(true, "Perenos.SingleInstance", out first);
            if (!first && ShotDir == null && Auto == null)
            {
                MessageBox.Show("«Перенос» уже запущен — посмотри на панели задач.", "Перенос", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(); return;
            }
            if (first) Maintenance.CleanupLeftovers();
            new MainWindow().Show();
        }
    }
}
