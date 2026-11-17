using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Transfer
{
    /// <summary>Пустой сценарий для показа готовых экранов (просмотр замечаний, снимки экранов).</summary>
    class StaticFlow : Flow { protected override void Execute() { } }

    public partial class MainWindow : Window
    {
        Flow flow;
        RestoreFlow pendingRestore;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly Dictionary<string, CheckBox> opts = new Dictionary<string, CheckBox>();
        bool indetOn, closing; int liveTick;
        HwProfile hw;                 // железо этого компьютера (читается в фоне при запуске)
        HwProfile Hw { get { return hw ?? (hw = HwProfile.Read()); } }
        string Root { get { return App.Root; } }

        public MainWindow()
        {
            InitializeComponent();
            timer.Tick += (s, e) => Tick();
            SourceInitialized += (s, e) => Native.RoundCorners(new WindowInteropHelper(this).Handle);
            Loaded += (s, e) =>
            {
                RefreshHome();
                System.Threading.Tasks.Task.Run(() => { var h = HwProfile.Read(); Dispatcher.BeginInvoke(new Action(() => { hw = h; HwLine.Text = "Этот компьютер: " + h.Short; })); });
                if (App.ShotDir != null) { Dispatcher.BeginInvoke(new Action(Shots), DispatcherPriority.ApplicationIdle); return; }
                if (App.Auto == "backup") Backup_Click(null, null);
                if (App.Auto == "clonecap") CloneCap_Click(null, null);
                if (App.Auto == "cloneapply") CloneApply_Click(null, null);
                if (App.Auto == "template" && App.TemplateFile != null) StartFlow(new TemplateImportFlow(Root, App.TemplateFile));
            };
            Closing += (s, e) =>
            {
                if (closing || flow == null || flow.Finished || flow is StaticFlow) return;
                if (MessageBox.Show("Перенос ещё идёт. Остановить и закрыть программу?\n\nУже скопированное останется на диске — в следующий раз продолжится с того же места.",
                        "Перенос", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
                closing = true; flow.R.Cancel();
            };
            Closed += (s, e) => { try { BackupFlow.CleanupSnapshot(); } catch { } };
        }

        void Show(FrameworkElement view)
        {
            foreach (var v in new FrameworkElement[] { HomeView, OptView, RunView, DoneView }) v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
        }

        // ============================================================ главный экран
        static Brush Res(string key) { return (Brush)Application.Current.Resources[key]; }

        List<Problem> SavedProblems()
        {
            try
            {
                string pj = Path.Combine(Root, @"data\info\problems.json");
                if (File.Exists(pj))
                {
                    var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    return js.Deserialize<List<Problem>>(File.ReadAllText(pj));
                }
                // копия сделана прошлой (скриптовой) версией — разбираем её журналы
                return Errors.Group(Errors.ParseLegacyLogs(Path.Combine(Root, @"data\logs")), Path.Combine(Root, @"data\files"), @"C:\__xfer_snap\", @"C:\");
            }
            catch { return new List<Problem>(); }
        }

        void RefreshHome()
        {
            Show(HomeView); Title = "Перенос программ и настроек"; TitleNote.Text = "";
            Task.ProgressState = TaskbarItemProgressState.None;
            try
            {
                var d = new DriveInfo(Root.Substring(0, 3));
                DriveTitle.Text = "Диск " + Root.Substring(0, 2) + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : " · " + d.VolumeLabel);
                DriveBar.Value = 1.0 - (double)d.AvailableFreeSpace / d.TotalSize;
                DriveNote.Text = "Свободно " + Fmt.Size(d.AvailableFreeSpace) + " из " + Fmt.Size(d.TotalSize) + " · папка " + Root;
            }
            catch { DriveTitle.Text = Root; DriveNote.Text = ""; }

            var m = Json.ReadFile(Path.Combine(Root, @"data\info\machine.json"));
            bool has = m != null, same = has && string.Equals(Json.Str(m, "Computer"), Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            CopyPill.Visibility = has ? Visibility.Visible : Visibility.Collapsed; CopyLink.Visibility = Visibility.Collapsed;
            if (!has)
            {
                CopyTitle.Text = "Пока ничего не сохранено"; CopyNote.Text = "Нажми «Сохранить» на старом компьютере.";
            }
            else
            {
                CopyTitle.Text = "Компьютер " + Json.Str(m, "Computer") + " · пользователь " + Json.Str(m, "User");
                string donePath = Path.Combine(Root, @"data\info\DONE.txt"); bool done = File.Exists(donePath);
                string when = "", size = "";
                if (done)
                {
                    try
                    {
                        string t = File.ReadAllText(donePath);
                        var mb = System.Text.RegularExpressions.Regex.Match(t, @"bytes=(\d+)"); if (mb.Success) size = " · " + Fmt.Size(double.Parse(mb.Groups[1].Value));
                        when = File.GetLastWriteTime(donePath).ToString("dd.MM.yyyy в HH:mm");
                    }
                    catch { }
                }
                CopyPillText.Text = done ? "ГОТОВА" : "НЕ ЗАВЕРШЕНА";
                CopyPillText.Foreground = Res(done ? "Ok" : "Warn");
                CopyPill.Background = new SolidColorBrush(done ? Color.FromArgb(0x1F, 0x3D, 0xD6, 0x8C) : Color.FromArgb(0x1F, 0xF5, 0xB9, 0x4A));
                CopyNote.Text = done ? "Сохранено " + when + size + "." : "Сохранение было прервано. Нажми «Досохранить» — продолжится с того же места.";
                var probs = SavedProblems(); int n = probs.Sum(p => p.Count);
                if (done)
                {
                    CopyLink.Visibility = Visibility.Visible;
                    int worst = n == 0 ? 0 : probs.Max(p => p.Severity);
                    CopyLink.Text = n == 0 ? "Замечаний нет — все файлы скопированы" :
                        "Не скопировалось " + Fmt.Count(n, "файл", "файла", "файлов") + (worst == 0 ? " — это нормально, посмотреть почему" : " — посмотреть, что это");
                }
            }
            BackupBtn.Content = same ? "Досохранить изменения" : "Сохранить с этого компьютера";
            BackupNote.Text = has && !same
                ? "На диске лежит копия ДРУГОГО компьютера (" + Json.Str(m, "Computer") + "). Сохранение с этого компьютера смешает их — обычно здесь нужно «Восстановить»."
                : "Документы, настройки и данные всех программ, папки программ, Wi-Fi и список установленного. Занятые файлы тоже копируются.";
            RefreshClone();
            RestoreBtn.IsEnabled = has;
            RestoreNote.Text = !has ? "Сначала сделай сохранение на старом компьютере."
                : same ? "Это тот же компьютер, с которого сделана копия. Восстанавливать нужно на новом."
                : "Сама поставит программы, вернёт файлы и настройки на место и покажет, что осталось доделать руками.";
        }

        // ============================================================ клон Windows
        void RefreshClone()
        {
            var imgs = ImageLib.List(Root);
            int clones = imgs.Count(i => !i.IsTemplate), templates = imgs.Count(i => i.IsTemplate);
            string fresh = CloneFinishFlow.FindFresh();
            CloneFinishBtn.Visibility = fresh != null ? Visibility.Visible : Visibility.Collapsed;
            CloneApplyBtn.IsEnabled = imgs.Count > 0;
            string have = imgs.Count == 0 ? "Образов на диске пока нет. "
                : "На диске: " + (clones > 0 ? Fmt.Count(clones, "клон", "клона", "клонов") : "") + (clones > 0 && templates > 0 ? " и " : "") + (templates > 0 ? Fmt.Count(templates, "шаблон", "шаблона", "шаблонов") : "") + ". ";
            CloneNote.Text = have + "Клон — любая из Windows этого компьютера целиком, со всеми программами. Шаблон — чистый образ Windows (ISO, WIM или ESD) для проверок. " +
                             "Любой образ разворачивается второй Windows в меню выбора при включении; основная не трогается." +
                             (fresh != null ? " Сейчас запущена развёрнутая Windows; вторая лежит на диске " + fresh + ":." : "");
        }

        /// <summary>Окно выбора: один список (и, если нужно, второй) с вариантами. Возвращает номер выбранного или -1.</summary>
        int PickDialog(string title, string sub, List<string[]> a, int selA, string capB, List<string[]> b, int selB, out int pickedB, string okText, Func<int, int, string> footer, string shotName = null)
        {
            int ra = selA, rb = selB, result = -1;
            var w = new Window { Owner = this, Title = title, Width = 680, SizeToContent = SizeToContent.Height, MaxHeight = 720, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                 WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Res("Bg"), ShowInTaskbar = false };
            var root = new Border { Background = Res("Bg"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(26, 24, 26, 22) };
            var dock = new DockPanel(); root.Child = dock; w.Content = root;
            var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
            head.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["UiBig"] });
            if (!string.IsNullOrEmpty(sub)) head.Children.Add(new TextBlock { Text = sub, Foreground = Res("Mute"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 6, 0, 0) });
            var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
            var foot = new TextBlock { Foreground = Res("Mute"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(2, 12, 0, 0) }; DockPanel.SetDock(foot, Dock.Bottom); dock.Children.Add(foot);
            var ok = new Button { Content = okText, Style = (Style)Application.Current.Resources["BtnPrimary"], IsDefault = true };
            var cancel = new Button { Content = "Отмена", Style = (Style)Application.Current.Resources["Btn"], IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(ok, Dock.Right); DockPanel.SetDock(cancel, Dock.Right); buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var list = new StackPanel { Margin = new Thickness(0, 16, 8, 0) };
            dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
            Action refresh = () => { foot.Text = footer != null ? footer(ra, rb) : ""; };
            Func<List<string[]>, bool, List<CheckBox>> group = (items, first) =>
            {
                var boxes = new List<CheckBox>();
                for (int i = 0; i < items.Count; i++)
                {
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = items[i][0], FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
                    sp.Children.Add(new TextBlock { Text = items[i][1], Foreground = Res("Mute"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 3, 0, 0) });
                    var cb = new CheckBox { Style = (Style)Application.Current.Resources["Opt"], Content = sp, IsChecked = i == (first ? ra : rb), Tag = i };
                    boxes.Add(cb); list.Children.Add(cb);
                }
                foreach (var cb in boxes)
                    cb.Click += (s2, e2) =>
                    {
                        int idx = (int)((CheckBox)s2).Tag; if (first) ra = idx; else rb = idx;
                        foreach (var o in boxes) o.IsChecked = (int)o.Tag == idx;      // ведёт себя как переключатель: выбран всегда ровно один
                        refresh();
                    };
                return boxes;
            };
            group(a, true);
            if (b != null)
            {
                list.Children.Add(new TextBlock { Text = capB, Style = (Style)Application.Current.Resources["Cap"], Margin = new Thickness(2, 10, 0, 10) });
                group(b, false);
            }
            refresh();
            ok.Click += (s2, e2) => { result = ra; w.DialogResult = true; };
            if (App.ShotDir != null && shotName != null)
            {
                w.Owner = null; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -4000; w.Show(); w.UpdateLayout();
                var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(root);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(App.ShotDir, shotName + ".png"))) enc.Save(fs);
                w.Close(); pickedB = rb; return -1;
            }
            bool done = w.ShowDialog() == true; pickedB = rb;
            return done ? result : -1;
        }

        /// <summary>Окно «Проверка перед запуском»: что с компьютером, что помешает и что программа подстроит сама.</summary>
        bool Preflight(string title, string machine, List<CheckItem> items, List<string> auto, string okText, string shotName = null)
        {
            bool blocked = items.Any(i => i.Level == 2), result = false;
            var w = new Window { Owner = this, Title = title, Width = 700, SizeToContent = SizeToContent.Height, MaxHeight = 760, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                 WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Res("Bg"), ShowInTaskbar = false };
            var root = new Border { Background = Res("Bg"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(26, 24, 26, 22) };
            var dock = new DockPanel(); root.Child = dock; w.Content = root;
            var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
            head.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["UiBig"] });
            head.Children.Add(new TextBlock { Text = machine, Foreground = Res("Mute"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 6, 0, 0) });
            var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
            var ok = new Button { Content = okText, Style = (Style)Application.Current.Resources["BtnPrimary"], IsDefault = true, IsEnabled = !blocked };
            var cancel = new Button { Content = blocked ? "Закрыть" : "Отмена", Style = (Style)Application.Current.Resources["Btn"], IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(ok, Dock.Right); DockPanel.SetDock(cancel, Dock.Right); buttons.Children.Add(ok); buttons.Children.Add(cancel);
            if (blocked) { var note = new TextBlock { Text = "Сначала нужно устранить отмеченное красным.", Foreground = Res("Bad"), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(note, Dock.Left); buttons.Children.Add(note); }
            var list = new StackPanel { Margin = new Thickness(0, 16, 8, 0) };
            dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
            foreach (var it in items.OrderByDescending(i => i.Level))
            {
                string key = it.Level == 0 ? "Ok" : it.Level == 1 ? "Warn" : "Bad";
                var c = ((SolidColorBrush)Res(key)).Color;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B)), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
                badge.Child = new TextBlock { Text = it.Level == 0 ? "" : it.Level == 1 ? "" : "", FontFamily = (FontFamily)Application.Current.Resources["Ico"], FontSize = 12, Foreground = Res(key), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var sp = new StackPanel(); Grid.SetColumn(sp, 1);
                sp.Children.Add(new TextBlock { Text = it.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
                sp.Children.Add(new TextBlock { Text = it.Text, Foreground = Res("Mute"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) });
                row.Children.Add(badge); row.Children.Add(sp); list.Children.Add(row);
            }
            if (auto != null && auto.Count > 0)
            {
                var box = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 4, 0, 0) };
                var sp = new StackPanel(); box.Child = sp;
                sp.Children.Add(new TextBlock { Text = "ПРОГРАММА ПОДСТРОИТ САМА", Style = (Style)Application.Current.Resources["Cap"], Margin = new Thickness(0, 0, 0, 6) });
                foreach (var a in auto) sp.Children.Add(new TextBlock { Text = "•  " + a, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 2) });
                list.Children.Add(box);
            }
            ok.Click += (s2, e2) => { result = true; w.DialogResult = true; };
            if (App.ShotDir != null && shotName != null)
            {
                w.Owner = null; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -4000; w.Show(); w.UpdateLayout();
                var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(root);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(App.ShotDir, shotName + ".png"))) enc.Save(fs);
                w.Close(); return false;
            }
            return w.ShowDialog() == true && result;
        }

        double MeasureDisk()
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try { return HwProfile.WriteSpeed(Root); } finally { Mouse.OverrideCursor = null; }
        }

        // примерная скорость снятия образа на съёмный диск, МБ/с (по замерам на этом компьютере)
        const double CapSpeedNone = 150, CapSpeedFast = 180;

        void CloneCap_Click(object sender, RoutedEventArgs e)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            List<WinInstall> wins; try { wins = WinInstall.Find(Root); } finally { Mouse.OverrideCursor = null; }
            if (wins.Count == 0) { MessageBox.Show("На дисках этого компьютера не нашлось установленной Windows.", "Клон Windows", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            long free = 0; try { free = new DriveInfo(Root.Substring(0, 3)).AvailableFreeSpace; } catch { }
            int wi = 0, mode;
            if (App.Auto != null || App.ShotDir != null)
            {
                if (App.CloneSrc != null) wi = Math.Max(0, wins.FindIndex(x => x.Root.StartsWith(App.CloneSrc.Substring(0, 2), StringComparison.OrdinalIgnoreCase)));
                mode = App.CloneCompress == "none" ? 0 : 1;
            }
            else mode = 1;   // со сжатием: замеры показали, что на съёмный диск так и быстрее, и файл вдвое меньше
            if (App.Auto == null)
            {
                var a = wins.Select(x => new[] { x.Name + (x.Current ? " — запущена сейчас" : ""),
                    (x.VersionLine != "" ? x.VersionLine + " · " : "") + (x.Custom != "" ? "переделанная сборка · " : "") + "диск " + x.Root.Substring(0, 2) + " · занято " + Fmt.Size(x.Used) + (x.Current ? " · снимается со снимка диска, работать можно" : "") }).ToList();
                var b = new List<string[]> {
                    new[] { "Быстрая запись, без сжатия", "Файл образа примерно равен занятому месту. Самый быстрый способ, если на диске переноса хватает места." },
                    new[] { "Со сжатием", "Файл выходит примерно вдвое меньше, но снимается дольше: процессор сжимает данные на ходу." } };
                Func<int, int, string> footer = (i, j) =>
                {
                    long need = CloneCaptureFlow.NeedFor(wins[i].Used, j == 0 ? "none" : "fast");
                    double min = wins[i].Used / 1048576.0 / (j == 0 ? CapSpeedNone : CapSpeedFast) / 60.0;
                    return "Нужно на диске переноса: " + Fmt.Size(need) + ", свободно " + Fmt.Size(free) + (need > free ? " — НЕ ХВАТИТ, освободи место на диске переноса" : "") +
                           " · займёт примерно " + (min < 1.5 ? "пару минут" : Math.Round(min) + " мин");
                };
                wi = PickDialog("Какую Windows клонировать?", "Клон попадёт в список образов на диске переноса; прежние образы не затираются.", a, wi, null, null, 1, out mode, "Снять клон", footer, "9-clone-source"); mode = 1;
                if (wi < 0) return;
            }
            if (App.Auto == null)
            {
                long need = CloneCaptureFlow.NeedFor(wins[wi].Used, "fast");
                if (!Preflight("Проверка перед снятием клона", "Клонируется: " + wins[wi].Name + " (диск " + wins[wi].Root.Substring(0, 2) + ") · этот компьютер: " + Hw.Short,
                        Compat.ForCapture(Hw, MeasureDisk(), need, free), null, "Снять клон")) return;
            }
            StartFlow(new CloneCaptureFlow(Root, wins[wi].Root, mode == 0 ? "none" : "fast", wins[wi].Name) { OsVersionLine = wins[wi].VersionLine, OsCustom = wins[wi].Custom });
        }

        void TemplateAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выбери образ Windows", Filter = "Образы Windows (*.iso; *.wim; *.esd)|*.iso;*.wim;*.esd", CheckFileExists = true };
            if (dlg.ShowDialog(this) != true) return;
            StartFlow(new TemplateImportFlow(Root, dlg.FileName));
        }

        void CloneApply_Click(object sender, RoutedEventArgs e)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            List<ImageRef> imgs; try { imgs = ImageLib.List(Root); } finally { Mouse.OverrideCursor = null; }
            if (imgs.Count == 0 && App.ShotDir != null) return;
            if (imgs.Count == 0) { MessageBox.Show("На диске переноса пока нет образов. Сними клон или добавь шаблон.", "Образы Windows", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            int pick = Math.Min(App.Pick, imgs.Count - 1), dummy;
            if (App.Auto == null)
            {
                pick = PickDialog("Какой образ развернуть?", "Он встанет второй Windows на этот компьютер и появится в меню выбора при включении.",
                    imgs.Select(i => new[] { i.Title, i.Sub }).ToList(), 0, null, null, 0, out dummy, "Дальше", null, "10-image-pick");
                if (pick < 0) return;
            }
            var img = imgs[pick];
            var f = new CloneApplyFlow(Root, img) { TestDir = App.ApplyDir, TestEsp = App.ApplyEsp };
            if (App.Auto == null)
            {
                var auto = new List<string>();
                var items = Compat.ForApply(Hw, img.SrcHw, img.OsName, img.IsTemplate, MeasureDisk(), auto);
                if (!Preflight("Подойдёт ли образ этому компьютеру", "Образ: " + img.Title + " · этот компьютер: " + Hw.Short, items, auto, "Дальше: сколько места выделить")) return;
            }
            if (f.TestDir == null)
            {
                // сколько места выделить, решает пользователь; программа только подсказывает границы
                Mouse.OverrideCursor = Cursors.Wait;
                long max; try { max = f.MaxBytes(); } finally { Mouse.OverrideCursor = null; }
                long min = f.NeedBytes();
                if (max < min)
                {
                    MessageBox.Show("На диске этого компьютера не хватает места под вторую Windows.\n\nЕй нужно минимум " + Fmt.Size(min) + " (образ " + Fmt.Size(f.ImageBytes) + " + запас), а освободить получается только " +
                                    Fmt.Size(Math.Max(0, max)) + ".\n\nОсвободи место на диске и попробуй снова.", "Образы Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                long chosen = AskCloneSize(min, max);
                if (chosen <= 0) return;
                f.CloneSize = chosen;
            }
            StartFlow(f);
        }

        /// <summary>Окно «сколько места выделить под клон». Возвращает байты или 0, если отменено.</summary>
        long AskCloneSize(long min, long max)
        {
            int minGb = (int)Math.Ceiling(min / 1073741824.0), maxGb = (int)Math.Floor(max / 1073741824.0);
            if (maxGb < minGb) maxGb = minGb;
            long result = 0;
            var w = new Window { Owner = this, Title = "Клон Windows", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                 WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Res("Card"), ShowInTaskbar = false };
            var root = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(26, 24, 26, 22) };
            var sp = new StackPanel(); root.Child = sp; w.Content = root;
            sp.Children.Add(new TextBlock { Text = "Сколько места выделить под вторую Windows?", FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["UiBig"] });
            sp.Children.Add(new TextBlock { Text = "(минимум " + minGb + " ГБ, максимум " + maxGb + " ГБ)", Foreground = Res("Mute"), Margin = new Thickness(0, 6, 0, 0) });
            sp.Children.Add(new TextBlock { Text = "Это место отрежется от диска основной Windows и станет диском второй. Чем больше выделишь, тем больше в ней будет свободного места под программы и файлы.",
                                            Foreground = Res("Mute"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 10, 0, 16) });
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var field = new Border { Background = Res("Bg"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9, 12, 9) };
            var fg = new Grid(); field.Child = fg;
            var hint = new TextBlock { Text = minGb.ToString(), Foreground = Res("Dim"), FontSize = 18, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Res("Text"), CaretBrush = Res("Text"), FontSize = 18, MaxLength = 6, VerticalAlignment = VerticalAlignment.Center };
            fg.Children.Add(hint); fg.Children.Add(box);
            row.Children.Add(field);
            var unit = new TextBlock { Text = "ГБ", FontSize = 18, Foreground = Res("Mute"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(unit, 1); row.Children.Add(unit);
            sp.Children.Add(row);
            var err = new TextBlock { Text = "Пустое поле — будет выделен минимум.", Foreground = Res("Dim"), FontSize = 12, Margin = new Thickness(2, 8, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
            sp.Children.Add(err);
            var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 20, 0, 0) };
            var ok = new Button { Content = "Продолжить", Style = (Style)Application.Current.Resources["BtnPrimary"], IsDefault = true };
            var cancel = new Button { Content = "Отмена", Style = (Style)Application.Current.Resources["Btn"], IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(ok, Dock.Right); DockPanel.SetDock(cancel, Dock.Right); buttons.Children.Add(ok); buttons.Children.Add(cancel);
            sp.Children.Add(buttons);
            box.TextChanged += (s2, e2) =>
            {
                hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                err.Foreground = Res("Dim"); err.Text = box.Text.Length == 0 ? "Пустое поле — будет выделен минимум." : "";
            };
            box.PreviewTextInput += (s2, e2) => { foreach (char c in e2.Text) if (!char.IsDigit(c)) e2.Handled = true; };
            ok.Click += (s2, e2) =>
            {
                string t = box.Text.Trim(); int gb;
                if (t.Length == 0) gb = minGb;
                else if (!int.TryParse(t, out gb)) { err.Foreground = Res("Bad"); err.Text = "Введи число — сколько гигабайт выделить."; return; }
                if (gb < minGb) { err.Foreground = Res("Bad"); err.Text = "Этого мало: нужно минимум " + minGb + " ГБ, иначе Windows не поместится."; return; }
                if (gb > maxGb) { err.Foreground = Res("Bad"); err.Text = "Столько нет: основной Windows тоже нужно место. Максимум " + maxGb + " ГБ."; return; }
                result = gb == maxGb ? max : Math.Max(min, (long)gb * 1073741824L);
                w.DialogResult = true;
            };
            w.Loaded += (s2, e2) => box.Focus();
            if (App.ShotDir != null)
            {
                // служебное: снимок окна для проверки вёрстки
                w.Owner = null; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -4000; w.Show(); w.UpdateLayout();
                var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(root);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(App.ShotDir, "8-clone-size.png"))) enc.Save(fs);
                w.Close(); return 0;
            }
            return w.ShowDialog() == true ? result : 0;
        }

        void CloneFinish_Click(object sender, RoutedEventArgs e)
        {
            var f = new CloneFinishFlow(Root);
            if (f.FreshLetter == null) return;
            if (MessageBox.Show("Вторая (чистая) Windows на диске " + f.FreshLetter + ": будет удалена, а диск очищен. Всё, что лежит на диске " + f.FreshLetter + ":, пропадёт без возможности вернуть.\n\n" +
                    "Это не обязательно: обе Windows могут жить рядом. Удаляй, только если чистая больше не нужна.\n\nУдалить её?", "Клон Windows", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            StartFlow(f);
        }

        void Home_Click(object sender, RoutedEventArgs e) { flow = null; RefreshHome(); }

        void CopyLink_Click(object sender, MouseButtonEventArgs e)
        {
            var m = Json.ReadFile(Path.Combine(Root, @"data\info\machine.json"));
            var probs = SavedProblems(); int n = probs.Sum(p => p.Count); int worst = n == 0 ? 0 : probs.Max(p => p.Severity);
            var f = new StaticFlow { Root = Root, Problems = probs, ResultLevel = worst == 0 ? 0 : 1 };
            f.ResultTitle = n == 0 ? "Копия полная, замечаний нет" : worst == 0 ? "Копия полная" : "В копии есть что проверить";
            f.ResultText = n == 0 ? "Все файлы скопированы." : worst == 0
                ? "Не скопировались только служебные файлы Windows, которые нельзя перенести в принципе. Твои данные и настройки на месте."
                : "Ниже написано, какие файлы не скопировались, почему и что с этим делать.";
            f.Summary.Add("Копия с компьютера " + Json.Str(m, "Computer") + ", пользователь " + Json.Str(m, "User"));
            f.Summary.Add("Не скопировалось файлов: " + n);
            string rep = Path.Combine(Root, "Отчёт о сохранении.txt"); if (File.Exists(rep)) f.ReportPath = rep;
            flow = f; ShowDone(true);
        }

        // ============================================================ запуск
        void Backup_Click(object sender, RoutedEventArgs e)
        {
            var m = Json.ReadFile(Path.Combine(Root, @"data\info\machine.json"));
            if (m != null && App.TestSrc == null && !string.Equals(Json.Str(m, "Computer"), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                if (MessageBox.Show("На диске уже лежит копия другого компьютера (" + Json.Str(m, "Computer") + ").\n\nЕсли сохранить сюда и этот компьютер, данные смешаются. Продолжить?",
                        "Перенос", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            if (App.Auto == null && App.TestSrc == null)
            {
                var items = Compat.ForCapture(Hw, MeasureDisk(), 0, 0);
                if (items.Any(i => i.Level > 0) && !Preflight("Проверка перед сохранением", "Этот компьютер: " + Hw.Short, items, null, "Всё равно сохранить")) return;
            }
            StartFlow(new BackupFlow(Root) { TestSource = App.TestSrc });
        }

        CheckBox AddOpt(string key, string title, string note, bool on, bool enabled = true)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = note, Foreground = Res("Mute"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 3, 0, 0) });
            var cb = new CheckBox { Style = (Style)Application.Current.Resources["Opt"], Content = sp, IsChecked = on && enabled, IsEnabled = enabled };
            OptList.Children.Add(cb); opts[key] = cb;
            return cb;
        }

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            var f = new RestoreFlow(Root); pendingRestore = f;
            OptList.Children.Clear(); opts.Clear();
            OptSub.Text = "Копия с компьютера " + f.OldComputer + " (пользователь " + f.OldUser + ")  →  этот компьютер, пользователь " + f.NewUser + ". Ничего не удаляется; файлы, которые здесь новее, не перезаписываются.";
            var warn = new List<string>();
            if (string.Equals(f.OldComputer, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) warn.Add("Это тот же компьютер, с которого делалась копия. Восстановление нужно запускать на новом компьютере.");
            if (!f.SameAccount) warn.Add("Программа запущена от имени другой учётной записи (" + Environment.UserName + "), а в Windows вошёл " + f.NewUser + ". Настройки из реестра для пользователя перенести не получится — остальное перенесётся.");
            if (!string.Equals(f.OldUser, f.NewUser, StringComparison.OrdinalIgnoreCase)) warn.Add("Имя пользователя другое (было «" + f.OldUser + "»). Чтобы программы нашли свои файлы, будет создана ссылка со старого пути профиля на новый.");
            OptWarn.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed; OptWarnText.Text = string.Join("\n", warn);

            int apps = f.WingetPackages().Count;
            Func<string, int> cnt = dir => { try { return Directory.GetFiles(Path.Combine(f.Info, dir), "*.xml").Length; } catch { return 0; } };
            AddOpt("apps", "Установить программы", apps > 0 ? Fmt.Count(apps, "программа поставится сама", "программы поставятся сами", "программ поставятся сами") + " из интернета. Занимает от получаса; нужен интернет." : "Список для автоустановки не сохранён — программы придётся ставить вручную.", true, apps > 0);
            AddOpt("profile", "Файлы и настройки пользователя", "Рабочий стол, документы, загрузки и настройки всех программ из профиля «" + f.OldUser + "». Самый большой шаг.", true, Directory.Exists(Path.Combine(f.Files, @"C\Users", f.OldUser)));
            AddOpt("public", "Общие файлы", "Папка «Общие» (C:\\Users\\Public).", true, Directory.Exists(Path.Combine(f.Files, @"C\Users\Public")));
            AddOpt("reg", "Настройки программ из реестра", "Параметры программ, которые хранятся не в файлах. Системные ветки Windows не трогаются; перед импортом делается копия текущих настроек.", true, f.SameAccount);
            AddOpt("env", "Переменные среды (PATH)", "Нужны для инструментов разработчика: Flutter, Java, Python и других.", true, f.SameAccount);
            AddOpt("other", "Остальные папки", "Папки из корня дисков (C:\\src и другие) — на те же места. Если такого диска здесь нет, они попадут в C:\\Перенесено.", true);
            AddOpt("wifi", "Сети Wi-Fi с паролями", "Сохранённых сетей: " + cnt("wifi") + ".", true, cnt("wifi") > 0);
            AddOpt("tasks", "Задания планировщика", "Автозапуск твоих скриптов и программ (" + cnt("tasks") + "). Включай, только если нужны: пути на новом компьютере могут отличаться.", false, cnt("tasks") > 0);
            AddOpt("regsys", "Системные настройки сторонних программ", "Нужно редко — для лицензий и старых программ. Обычно программы после установки записывают их сами.", false);
            Show(OptView);
        }

        void RestoreStart_Click(object sender, RoutedEventArgs e)
        {
            Func<string, bool> on = k => opts.ContainsKey(k) && opts[k].IsChecked == true;
            var o = pendingRestore.Opt;
            o.Programs = on("apps"); o.Profile = on("profile"); o.PublicFiles = on("public"); o.RegUser = on("reg"); o.Env = on("env");
            o.Other = on("other"); o.Wifi = on("wifi"); o.Tasks = on("tasks"); o.RegSystem = on("regsys");
            if (!(o.Programs || o.Profile || o.PublicFiles || o.RegUser || o.Other || o.Wifi || o.Tasks || o.RegSystem)) { MessageBox.Show("Выбери хотя бы один пункт.", "Перенос"); return; }
            StartFlow(pendingRestore);
        }

        void StartFlow(Flow f)
        {
            flow = f; StepsList.ItemsSource = f.Steps;
            RunTitle.Text = (f is BackupFlow ? "СОХРАНЕНИЕ · " : f is CloneFlowBase ? "ОБРАЗ WINDOWS · " : f is TemplateImportFlow ? "ШАБЛОН WINDOWS · " : "ВОССТАНОВЛЕНИЕ · ") + Environment.MachineName.ToUpper();
            RunHint.Text = f is CloneApplyFlow ? "Не выключай компьютер и не отключай диск, пока разворачивается клон." : "Компьютером можно пользоваться. Не отключай диск и не закрывай крышку ноутбука.";
            PauseBtn.Content = "Пауза"; PauseBtn.IsEnabled = true; CancelBtn.IsEnabled = true;
            DeviceBox.Visibility = Visibility.Collapsed; ErrBox.Visibility = Visibility.Collapsed;
            Show(RunView);
            f.Started = DateTime.Now; f.Start(); timer.Start(); Tick();
        }

        // ============================================================ прогресс
        void Indet(bool on)
        {
            if (on == indetOn) return; indetOn = on;
            IndetBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            RunBar.Visibility = on ? Visibility.Hidden : Visibility.Visible;
            if (on)
            {
                double w = Math.Max(300, IndetBox.ActualWidth > 0 ? IndetBox.ActualWidth : 500);
                IndetMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-160, w, TimeSpan.FromSeconds(1.5)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else IndetMove.BeginAnimation(TranslateTransform.XProperty, null);
        }

        static string Pretty(string path) { return (path ?? "").Replace(@"C:\__xfer_snap\", @"C:\"); }

        void Tick()
        {
            if (flow == null) return;
            var r = flow.R;
            RunActivity.Text = r.Cancelled ? "Останавливаю…" : r.WaitingDevice ? "Жду диск переноса" : r.Paused ? "Пауза" : flow.Activity;
            TileElapsed.Text = Fmt.Clock(DateTime.Now - flow.Started);
            DeviceBox.Visibility = r.WaitingDevice ? Visibility.Visible : Visibility.Collapsed;
            string skipLabel = r.SkipLabel;
            bool canSkip = skipLabel != null && !r.SkipRequested && !r.Cancelled;
            SkipBtn.Visibility = canSkip ? Visibility.Visible : Visibility.Collapsed;
            if (canSkip) { SkipBtn.Content = skipLabel; SkipBtn.IsEnabled = true; }
            int skipped = (r.ErrorCount + 1) / 2;       // каждый неудачный файл robocopy пробует дважды
            ErrBox.Visibility = skipped > 0 && !r.WaitingDevice ? Visibility.Visible : Visibility.Collapsed;
            ErrText.Text = "Пропущено " + Fmt.Count(skipped, "файл", "файла", "файлов") + " — копирование продолжается. В конце покажу, что это за файлы и нужно ли с ними что-то делать.";
            string pct;
            if (flow.Copying)
            {
                long total = r.BytesTotal, done = Interlocked.Read(ref r.BytesDone);
                double frac = total > 0 ? Math.Min(0.999, (double)done / total) : 0.999;
                Indet(false); RunBar.Value = frac; pct = ((int)Math.Floor(frac * 100)) + "%";
                RunStat.Text = Fmt.Size(done) + " из " + Fmt.Size(total);
                TileDone.Text = Fmt.Size(done); TileDoneSub.Text = "из " + Fmt.Size(total);
                TileSpeed.Text = r.Paused || r.WaitingDevice ? "—" : Fmt.Speed(r.Speed);
                TileFiles.Text = r.FilesLabel != null ? r.FilesLabel : Fmt.Count(Interlocked.Read(ref r.FilesDone), "файл", "файла", "файлов");
                double eta = r.EtaSeconds;
                TileEta.Text = r.Paused ? "на паузе" : r.WaitingDevice ? "жду диск" : Fmt.Eta(eta);
                TileEtaSub.Text = (!r.Paused && !r.WaitingDevice && !double.IsNaN(eta) && eta > 60) ? "закончится около " + DateTime.Now.AddSeconds(eta).ToString("HH:mm") : "";
                NowJob.Text = Pretty(r.JobName); NowCount.Text = r.CountLabel != null ? r.CountLabel : "папка " + r.JobIndex + " из " + r.JobCount;
                NowFile.Text = Fmt.ShortPath(Pretty(r.CurrentFile), 92);
                Task.ProgressState = r.WaitingDevice ? TaskbarItemProgressState.Error : r.Paused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;
                Task.ProgressValue = frac;
            }
            else
            {
                double sf = flow.StageFraction;
                if (sf < 0) { Indet(true); pct = ""; Task.ProgressState = TaskbarItemProgressState.Indeterminate; }
                else { Indet(false); RunBar.Value = sf; pct = ((int)Math.Floor(sf * 100)) + "%"; Task.ProgressState = TaskbarItemProgressState.Normal; Task.ProgressValue = sf; }
                RunStat.Text = flow.StageStat;
                TileDone.Text = "—"; TileDoneSub.Text = ""; TileSpeed.Text = "—"; TileFiles.Text = ""; TileEta.Text = "—"; TileEtaSub.Text = "";
                NowJob.Text = flow.Activity; NowCount.Text = ""; NowFile.Text = Fmt.ShortPath(Pretty(flow.ActivityDetail), 92);
            }
            RunPercent.Text = pct; RunPercent.Visibility = pct == "" ? Visibility.Collapsed : Visibility.Visible;
            Title = (pct == "" ? "" : pct + " · ") + "Перенос"; TitleNote.Text = pct == "" ? "" : "· " + pct;
            if (App.LiveDir != null && (++liveTick % 16) == 0) { try { Directory.CreateDirectory(App.LiveDir); App.ShotDir = App.LiveDir; Snap("live-" + (liveTick / 16).ToString("00")); } catch { } finally { App.ShotDir = null; } }
            if (flow.Finished) { timer.Stop(); OnFinished(); }
        }

        void OnFinished()
        {
            Indet(false); Task.ProgressState = TaskbarItemProgressState.None;
            if (closing) return;
            try { Native.FlashWindow(new WindowInteropHelper(this).Handle, true); } catch { }
            if (flow.R.Cancelled)
            {
                flow = null; RefreshHome();
                HomeHint.Text = "Перенос остановлен. Уже скопированное осталось на диске — в следующий раз продолжится с того же места.";
                return;
            }
            ShowDone(false);
            if (App.ExitWhenDone) Close();
        }

        void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (flow == null) return;
            bool p = !flow.R.Paused; flow.R.Pause(p);
            PauseBtn.Content = p ? "Продолжить" : "Пауза";
        }

        void Skip_Click(object sender, RoutedEventArgs e)
        {
            if (flow == null || flow.R.SkipLabel == null) return;
            if (MessageBox.Show("Пропустить эту проверку на вирусы?\n\nОстальные шаги выполнятся как обычно. В итоге будет отмечено, что проверка пропущена.",
                    "Перенос", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SkipBtn.IsEnabled = false;
            flow.R.SkipRequested = true;
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (flow == null) return;
            if (MessageBox.Show("Остановить перенос?\n\nУже скопированное останется на диске. Потом можно продолжить с того же места — заново копировать не придётся.",
                    "Перенос", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            PauseBtn.IsEnabled = false; CancelBtn.IsEnabled = false;
            flow.R.Cancel();
        }

        // ============================================================ итог
        void ShowDone(bool review)
        {
            var f = flow;
            string glyph = f.ResultLevel == 0 ? "\uE73E" : f.ResultLevel == 1 ? "\uE7BA" : "\uEA39";
            string key = f.ResultLevel == 0 ? "Ok" : f.ResultLevel == 1 ? "Warn" : "Bad";
            var c = ((SolidColorBrush)Res(key)).Color;
            DoneIcon.Text = glyph; DoneIcon.Foreground = Res(key); DoneIconBox.Background = new SolidColorBrush(Color.FromArgb(0x1F, c.R, c.G, c.B));
            DoneTitle.Text = f.ResultTitle; DoneText.Text = f.ResultText;
            DoneSummary.ItemsSource = f.Summary; ProblemsList.ItemsSource = f.Problems;
            ProblemsCap.Text = f.Problems.Count > 0 ? "ЧТО НЕ СКОПИРОВАЛОСЬ И ПОЧЕМУ" : (f.ResultLevel == 2 || f is CloneFlowBase || f is TemplateImportFlow) ? "" : "ЗАМЕЧАНИЙ НЕТ — ВСЕ ФАЙЛЫ СКОПИРОВАНЫ";
            AgainBtn.Visibility = (f is BackupFlow || f is StaticFlow) ? Visibility.Visible : Visibility.Collapsed;
            AgainBtn.Content = f.ResultLevel == 2 ? "Попробовать снова" : "Досохранить";
            ReportBtn.Visibility = f.ReportPath != null && File.Exists(f.ReportPath) ? Visibility.Visible : Visibility.Collapsed;
            var rf = f as RestoreFlow;
            ManualBtn.Visibility = rf != null && rf.ManualListPath != null && File.Exists(rf.ManualListPath) ? Visibility.Visible : Visibility.Collapsed;
            Title = "Перенос — " + f.ResultTitle; TitleNote.Text = "";
            Show(DoneView);
        }

        static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }
        void Report_Click(object sender, RoutedEventArgs e) { if (flow != null && flow.ReportPath != null) Open(flow.ReportPath); }
        void Manual_Click(object sender, RoutedEventArgs e) { var rf = flow as RestoreFlow; if (rf != null && rf.ManualListPath != null) Open(rf.ManualListPath); }
        void Min_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        // ============================================================ служебное: снимки экранов для проверки вёрстки
        void Snap(string name)
        {
            UpdateLayout();
            Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render);
            var root = (FrameworkElement)Content;
            var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(App.ShotDir, name + ".png"))) enc.Save(fs);
        }

        void Shots()
        {
            Directory.CreateDirectory(App.ShotDir);
            Snap("1-home");
            var f = new StaticFlow { Root = Root, Title = "demo", Copying = true };
            foreach (var s in new[] { new[] { "ok", "Список программ", "Программ: 142, приложений из Магазина: 79" }, new[] { "ok", "Настройки программ", "Сохранены настройки 170 программ" },
                new[] { "ok", "Wi-Fi, задания, переменные", "Сетей Wi-Fi: 52, заданий планировщика: 27" }, new[] { "ok", "Снимок диска", "файлы открытых программ тоже скопируются" },
                new[] { "ok", "Подсчёт объёма", "509.2 ГБ" }, new[] { "run", "Копирование файлов", "" }, new[] { "wait", "Проверка и отчёт", "" } })
                f.Steps.Add(new StepVm { State = s[0], Title = s[1], Detail = s[2] });
            f.Activity = "Копирую файлы"; f.R.BytesTotal = 546750000000; f.R.BytesDone = 344450000000; f.R.Speed = 118 * 1024 * 1024; f.R.FilesDone = 512340;
            f.R.JobName = "Профили пользователей: документы и настройки программ"; f.R.JobIndex = 1; f.R.JobCount = 45;
            f.R.CurrentFile = @"C:\__xfer_snap\Users\avito\AppData\Local\Google\Play Games\userdata\avd\user_data.img"; f.R.Started = DateTime.Now.AddMinutes(-48);
            flow = f; f.Started = DateTime.Now.AddMinutes(-48); StepsList.ItemsSource = f.Steps; RunTitle.Text = "СОХРАНЕНИЕ · " + Environment.MachineName.ToUpper();
            Show(RunView); Tick(); Snap("2-run");
            f.R.ErrorCount = 368; Tick(); Snap("3-run-skipped");
            f.R.WaitingDevice = true; Tick(); Snap("4-run-device-lost");
            f.R.WaitingDevice = false; f.Copying = false; f.StageFraction = -1; f.Activity = "Сохраняю настройки программ из реестра"; f.ActivityDetail = "JetBrains"; f.R.ErrorCount = 0;
            f.Steps[1].State = "run"; f.Steps[5].State = "wait"; Tick(); Snap("5-run-stage");
            CopyLink_Click(null, null); Snap("6-done-review");
            if (File.Exists(Path.Combine(Root, @"data\info\machine.json"))) { Restore_Click(null, null); Snap("7-restore-options"); }
            AskCloneSize(61L << 30, 880L << 30);
            CloneCap_Click(null, null);
            CloneApply_Click(null, null);
            {
                // пример: клон с этого компьютера разворачивается на ноутбук с Intel VMD
                var t = new HwProfile { Manufacturer = "MSI", Model = "Katana 17 HX B14WFK", Cpu = "Intel Core i7-14650HX", RamGb = 32, Arch = "x64", Firmware = "UEFI", DiskBus = "RAID", DiskStyle = "GPT", Tpm2 = true, SecureBoot = true, HasBattery = true };
                t.StorageServices.Add("iaStorVD");
                var auto = new List<string>();
                Preflight("Подойдёт ли образ этому компьютеру", "Образ: Клон: Windows 11 Home Single Language 24H2 · ReviOS 11 25.10 · этот компьютер: " + t.Short,
                    Compat.ForApply(t, Hw, "Windows 11 Home Single Language 24H2", false, 376, auto), auto, "Дальше: сколько места выделить", "11-preflight-apply");
                var bad = new HwProfile { Manufacturer = "ASUS", Model = "K53", Cpu = "Intel Core i3-2310M", RamGb = 4, Arch = "x64", Firmware = "BIOS", DiskBus = "SATA", DiskStyle = "MBR", HasBattery = true, OnBattery = true, BatteryPct = 22 };
                auto = new List<string>();
                Preflight("Подойдёт ли образ этому компьютеру", "Образ: Клон: Windows 11 Home · этот компьютер: " + bad.Short, Compat.ForApply(bad, Hw, "Windows 11 Home", false, 31, auto), null, "Дальше", "12-preflight-blocked");
                Preflight("Проверка перед снятием клона", "Этот компьютер: " + Hw.Short, Compat.ForCapture(Hw, HwProfile.WriteSpeed(Root), 294L << 30, 311L << 30), null, "Снять клон", "13-preflight-capture");
            }
            flow = null; closing = true; Close();
        }
    }
}
