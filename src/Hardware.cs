using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Transfer
{
    /// <summary>Сведения о железе компьютера, важные для переноса Windows.</summary>
    class HwProfile
    {
        public string Manufacturer = "", Model = "", Cpu = "", Arch = "", Firmware = "", DiskBus = "", DiskStyle = "";
        public double RamGb;
        public bool SecureBoot, Tpm2, BitLockerOn, HasBattery, OnBattery;
        public int BatteryPct = 100;
        /// <summary>Службы драйверов контроллеров дисков: «stornvme», «storahci», «iaStorVD»…</summary>
        public List<string> StorageServices = new List<string>();

        // драйверы, которые есть в любой Windows 10/11 — для них ничего добавлять не нужно
        static readonly string[] Inbox = { "stornvme", "storahci", "pciide", "intelide", "atapi", "storvsc", "sdbus", "uaspstor", "usbstor", "spaceport", "vhdmp", "storufs", "mshdc", "xvdd", "iscsiprt", "ehstorclass" };

        /// <summary>Контроллеры, которым нужен свой драйвер (Intel VMD/RST, AMD RAID и подобные).</summary>
        public List<string> SpecialStorage { get { return StorageServices.Where(s => !Inbox.Contains(s.ToLowerInvariant())).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); } }

        public string Short
        {
            get
            {
                string pc = (Manufacturer + " " + Model).Trim();
                string disk = DiskBus + (SpecialStorage.Count > 0 ? " через " + StorageName(SpecialStorage[0]) : "");
                return string.Join(" · ", new[] { pc, Cpu, RamGb > 0 ? Math.Round(RamGb) + " ГБ памяти" : "", Firmware, disk != "" ? "диск " + disk : "" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }

        public static string StorageName(string service)
        {
            string s = service.ToLowerInvariant();
            if (s.StartsWith("iastorvd") || s.StartsWith("iavroc")) return "Intel VMD";
            if (s.StartsWith("iastor")) return "Intel RST";
            if (s.StartsWith("rc")) return "AMD RAID";
            if (s.StartsWith("nv")) return "NVIDIA RAID";
            return service;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        static IEnumerable<ManagementObject> Query(string ns, string wql)
        {
            var list = new List<ManagementObject>();
            try { using (var s = new ManagementObjectSearcher(ns, wql)) foreach (ManagementObject o in s.Get()) list.Add(o); } catch { }
            return list;
        }

        static string S(ManagementObject o, string p) { try { return Convert.ToString(o[p] ?? "").Trim(); } catch { return ""; } }

        /// <summary>Снять сведения с этого компьютера. Занимает около секунды.</summary>
        public static HwProfile Read()
        {
            var h = new HwProfile();
            foreach (var o in Query(@"root\cimv2", "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                h.Manufacturer = S(o, "Manufacturer"); h.Model = S(o, "Model");
                double b; if (double.TryParse(S(o, "TotalPhysicalMemory"), out b)) h.RamGb = b / 1073741824.0;
            }
            if (h.Manufacturer.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase) || h.Manufacturer.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase))
                foreach (var o in Query(@"root\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard")) { h.Manufacturer = S(o, "Manufacturer"); h.Model = S(o, "Product"); }
            // короткие привычные названия производителей
            string ml = h.Manufacturer.ToLowerInvariant();
            if (ml.Contains("micro-star")) h.Manufacturer = "MSI"; else if (ml.Contains("asustek")) h.Manufacturer = "ASUS"; else if (ml.Contains("hewlett") || ml == "hp") h.Manufacturer = "HP";
            else if (ml.StartsWith("dell")) h.Manufacturer = "Dell"; else if (ml == "lenovo") h.Manufacturer = "Lenovo"; else if (ml.Contains("gigabyte")) h.Manufacturer = "Gigabyte";
            else if (ml.StartsWith("acer")) h.Manufacturer = "Acer"; else if (ml.Contains("samsung")) h.Manufacturer = "Samsung";
            foreach (var o in Query(@"root\cimv2", "SELECT Name FROM Win32_Processor")) { h.Cpu = System.Text.RegularExpressions.Regex.Replace(S(o, "Name"), @"\(R\)|\(TM\)|CPU|\s+@.*$|\d+th Gen", "").Replace("  ", " ").Trim(); break; }
            string pa = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
            h.Arch = pa == "AMD64" ? "x64" : pa == "ARM64" ? "arm64" : pa == "X86" ? "x86" : pa.ToLowerInvariant();
            h.Firmware = string.Equals(Environment.GetEnvironmentVariable("firmware_type"), "UEFI", StringComparison.OrdinalIgnoreCase) ? "UEFI" : "BIOS";
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State")) h.SecureBoot = k != null && Convert.ToInt32(k.GetValue("UEFISecureBootEnabled", 0)) == 1; } catch { }
            foreach (var o in Query(@"root\cimv2\Security\MicrosoftTpm", "SELECT SpecVersion FROM Win32_Tpm")) h.Tpm2 = S(o, "SpecVersion").StartsWith("2");
            foreach (var o in Query(@"root\cimv2", "SELECT Service, PNPClass FROM Win32_PnPEntity WHERE PNPClass='SCSIAdapter' OR PNPClass='HDC'"))
            { string svc = S(o, "Service"); if (svc != "") h.StorageServices.Add(svc); }
            foreach (var o in Query(@"root\Microsoft\Windows\Storage", "SELECT BusType, PartitionStyle, IsBoot FROM MSFT_Disk"))
            {
                if (!Convert.ToBoolean(o["IsBoot"])) continue;
                int bus = Convert.ToInt32(o["BusType"]), ps = Convert.ToInt32(o["PartitionStyle"]);
                h.DiskBus = bus == 17 ? "NVMe" : bus == 11 ? "SATA" : bus == 8 ? "RAID" : bus == 7 ? "USB" : bus == 10 ? "SAS" : bus == 3 ? "IDE" : "тип " + bus;
                h.DiskStyle = ps == 2 ? "GPT" : ps == 1 ? "MBR" : "";
            }
            string sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            foreach (var o in Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"))
                if (S(o, "DriveLetter").Equals(sys, StringComparison.OrdinalIgnoreCase)) h.BitLockerOn = S(o, "ProtectionStatus") == "1";
            SYSTEM_POWER_STATUS p;
            if (GetSystemPowerStatus(out p)) { h.HasBattery = (p.BatteryFlag & 128) == 0 && p.BatteryFlag != 255; h.OnBattery = h.HasBattery && p.ACLineStatus == 0; if (p.BatteryLifePercent <= 100) h.BatteryPct = p.BatteryLifePercent; }
            return h;
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> { { "Manufacturer", Manufacturer }, { "Model", Model }, { "Cpu", Cpu }, { "Arch", Arch }, { "Firmware", Firmware },
                { "DiskBus", DiskBus }, { "RamGb", Math.Round(RamGb, 1) }, { "StorageServices", string.Join(",", StorageServices.Distinct()) } };
        }

        public static HwProfile FromDict(object o)
        {
            var d = o as Dictionary<string, object>; if (d == null) return null;
            var h = new HwProfile { Manufacturer = Json.Str(d, "Manufacturer"), Model = Json.Str(d, "Model"), Cpu = Json.Str(d, "Cpu"), Arch = Json.Str(d, "Arch"), Firmware = Json.Str(d, "Firmware"), DiskBus = Json.Str(d, "DiskBus") };
            double.TryParse(Json.Str(d, "RamGb").Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out h.RamGb);
            h.StorageServices = Json.Str(d, "StorageServices").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return h;
        }

        /// <summary>Скорость записи на диск переноса, МБ/с (короткая проба 192 МБ мимо кэша Windows). -1 — не удалось измерить.</summary>
        public static double WriteSpeed(string folder)
        {
            string f = Path.Combine(folder, "speed-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                var buf = new byte[4 << 20]; new Random(7).NextBytes(buf);
                var sw = Stopwatch.StartNew(); const int mb = 192;
                using (var fs = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.None, 4 << 20, (FileOptions)0x20000000 | FileOptions.WriteThrough))
                    for (int i = 0; i < mb / 4 && sw.Elapsed.TotalSeconds < 12; i++) fs.Write(buf, 0, buf.Length);
                long written = new FileInfo(f).Length;
                return written / 1048576.0 / Math.Max(0.05, sw.Elapsed.TotalSeconds);
            }
            catch { return -1; }
            finally { try { File.Delete(f); } catch { } }
        }
    }

    /// <summary>Одна строка проверки перед запуском.</summary>
    class CheckItem
    {
        public int Level;            // 0 — в порядке, 1 — обрати внимание, 2 — так нельзя
        public string Title = "", Text = "";
        public CheckItem(int level, string title, string text) { Level = level; Title = title; Text = text; }
    }

    static class Compat
    {
        static void Power(HwProfile t, List<CheckItem> r)
        {
            if (!t.HasBattery) return;
            if (t.OnBattery && t.BatteryPct < 30) r.Add(new CheckItem(2, "Ноутбук работает от батареи, заряд " + t.BatteryPct + " %", "Операция долгая: если ноутбук выключится посередине, придётся начинать заново. Подключи зарядку и запусти снова."));
            else if (t.OnBattery) r.Add(new CheckItem(1, "Ноутбук работает от батареи (" + t.BatteryPct + " %)", "Лучше подключить зарядку: операция долгая и нагружает диск и процессор."));
            else r.Add(new CheckItem(0, "Питание от сети", "Зарядка подключена."));
        }

        static void Speed(double mbps, List<CheckItem> r)
        {
            if (mbps < 0) return;
            if (mbps < 60) r.Add(new CheckItem(1, "Диск переноса подключён медленно: " + mbps.ToString("0") + " МБ/с", "Так работает порт USB 2.0 или плохой кабель. Переставь диск в синий разъём USB 3 или в Type-C и возьми короткий кабель — будет в 5–10 раз быстрее."));
            else r.Add(new CheckItem(0, "Диск переноса работает быстро: " + mbps.ToString("0") + " МБ/с", mbps < 200 ? "Нормальная скорость для USB 3." : "Подключён на полной скорости."));
        }

        /// <summary>Проверка перед сохранением файлов или снятием клона.</summary>
        public static List<CheckItem> ForCapture(HwProfile t, double diskMbps, long need, long free)
        {
            var r = new List<CheckItem>();
            if (need > 0 && need > free) r.Add(new CheckItem(2, "На диске переноса не хватает места", "Нужно примерно " + Fmt.Size(need) + ", свободно " + Fmt.Size(free) + ". Освободи ещё " + Fmt.Size(need - free) + "."));
            else if (need > 0) r.Add(new CheckItem(0, "Места на диске переноса хватает", "Нужно примерно " + Fmt.Size(need) + ", свободно " + Fmt.Size(free) + "."));
            Speed(diskMbps, r); Power(t, r);
            return r;
        }

        /// <summary>Проверка перед разворачиванием образа: подходит ли он этому компьютеру и что программа подстроит сама.</summary>
        public static List<CheckItem> ForApply(HwProfile t, HwProfile src, string imageName, bool isTemplate, double diskMbps, List<string> auto)
        {
            var r = new List<CheckItem>();
            if (t.Firmware != "UEFI") r.Add(new CheckItem(2, "Компьютер загружается в старом режиме BIOS", "Вторая Windows добавляется только на компьютерах с загрузкой UEFI. Включи UEFI в настройках BIOS (и отключи CSM/Legacy)."));
            else if (t.DiskStyle == "MBR") r.Add(new CheckItem(2, "Системный диск размечен в старом формате MBR", "Нужна разметка GPT. Так бывает, если Windows ставили в режиме Legacy."));
            else r.Add(new CheckItem(0, "Режим загрузки подходит", "UEFI, разметка диска GPT."));

            if (src != null && src.Arch != "" && t.Arch != "" && src.Arch != t.Arch)
                r.Add(new CheckItem(2, "Образ для другого типа процессора", "Образ сделан для " + src.Arch + ", а здесь " + t.Arch + ". Такая Windows на этом компьютере не запустится."));

            var special = t.SpecialStorage;
            if (special.Count > 0)
            {
                r.Add(new CheckItem(1, "Диск работает через " + HwProfile.StorageName(special[0]), "В обычной Windows нет драйвера для такого контроллера — без него вторая Windows не увидела бы диск и упала бы в синий экран при загрузке."));
                auto.Add("возьмёт драйвер контроллера диска (" + string.Join(", ", special) + ") из этой Windows и встроит его в образ; если драйвер не найдётся — остановится, ничего не меняя");
            }
            else r.Add(new CheckItem(0, "Контроллер диска стандартный (" + (t.DiskBus != "" ? t.DiskBus : "обычный") + ")", "Драйвер для него есть в любой Windows."));
            auto.Add("встроит в образ драйверы этого компьютера (экран, Wi-Fi, звук, тачпад) — те, что уже стоят в этой Windows");
            auto.Add("включит в образе драйверы дисков SATA и NVMe при загрузке и сбросит старые буквы дисков");

            if (src != null && src.Manufacturer != "" && !src.Manufacturer.Equals(t.Manufacturer, StringComparison.OrdinalIgnoreCase))
                r.Add(new CheckItem(1, "Образ снят на другом компьютере: " + (src.Manufacturer + " " + src.Model).Trim(), "Это нормально, Windows при первом запуске перестроится под новое железо. Фирменные программы прежнего производителя (" + src.Manufacturer + ") здесь бесполезны — их лучше удалить после запуска."));
            if (src != null && src.DiskBus != "" && t.DiskBus != "" && src.DiskBus != t.DiskBus)
                r.Add(new CheckItem(0, "Тип диска другой: было " + src.DiskBus + ", здесь " + t.DiskBus, "Учтено: нужный драйвер диска будет включён при загрузке."));

            bool win11 = (imageName ?? "").Contains("Windows 11");
            if (win11 && (!t.Tpm2 || !t.SecureBoot))
                r.Add(new CheckItem(1, "Windows 11 и требования к компьютеру", (!t.Tpm2 ? "Нет модуля TPM 2.0. " : "") + (!t.SecureBoot ? "Secure Boot выключен. " : "") + "Windows 11 запустится, но может не получать крупные обновления."));

            if (t.BitLockerOn)
            {
                r.Add(new CheckItem(1, "Диск основной Windows зашифрован (BitLocker)", "После изменения меню загрузки Windows может спросить ключ восстановления. Убедись, что ключ у тебя есть (он сохраняется в учётной записи Microsoft)."));
                auto.Add("приостановит защиту BitLocker на одну перезагрузку, чтобы ключ не понадобился");
            }
            if (!isTemplate) r.Add(new CheckItem(1, "Активация Windows", "Лицензия привязана к компьютеру, с которого снят клон. Здесь Windows из клона попросит активацию."));
            Speed(diskMbps, r); Power(t, r);
            auto.Add("оставит основную Windows первой в меню и загружаемой по умолчанию");
            return r;
        }
    }
}
