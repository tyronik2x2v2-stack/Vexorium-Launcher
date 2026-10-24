using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VexoriumLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        Log.Write("=== Лаунчер запущен ===");
        Application.Run(new MainForm());
    }
}

// ---------------------------------------------------------------- Настройки
internal static class Config
{
    public const string ServerHost = "ams-game-02.centnodes.net";
    public const int ServerPort = 40065;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vexorium");
    private static readonly string SettingsFile = Path.Combine(Dir, "settings.ini");

    public static string LogFile => Path.Combine(Dir, "launcher.log");
    public static string GamePath = "";
    public static string Nickname = "";

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            foreach (string line in File.ReadAllLines(SettingsFile))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string key = line[..i], value = line[(i + 1)..].Trim();
                if (key == "gamePath") GamePath = value;
                else if (key == "nickname") Nickname = value;
            }
        }
        catch (Exception ex) { Log.Write("Не удалось прочитать настройки: " + ex.Message); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(SettingsFile, new[] { $"gamePath={GamePath}", $"nickname={Nickname}" });
        }
        catch (Exception ex) { Log.Write("Не удалось сохранить настройки: " + ex.Message); }
    }
}

// ---------------------------------------------------------------- Лог
internal static class Log
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Config.LogFile)!);
            File.AppendAllText(Config.LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}

// ---------------------------------------------------------------- Проверка игры
internal sealed record CheckItem(string Name, bool Ok, bool Critical, string Details);

internal static class GameCheck
{
    // Размер gta_sa.exe версии 1.0 US (единственная версия, с которой работает SA-MP 0.3.7)
    private const long ExpectedExeSize = 14383616;

    public static bool IsGameFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "gta_sa.exe"));

    public static string? AutoDetect()
    {
        var candidates = new List<string?> { Config.GamePath };
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\SAMP");
            if (k?.GetValue("gta_sa_exe") is string exe) candidates.Add(Path.GetDirectoryName(exe));
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\GTA San Andreas\Installation");
            if (k?.GetValue("ExePath") is string exe) candidates.Add(Path.GetDirectoryName(exe.Trim('"')));
        }
        catch { }
        candidates.AddRange(new[]
        {
            @"C:\Games\GTA San Andreas", @"D:\Games\GTA San Andreas", @"C:\GTA San Andreas",
            @"D:\GTA San Andreas", @"C:\Program Files (x86)\Rockstar Games\GTA San Andreas",
            @"C:\Program Files\Rockstar Games\GTA San Andreas"
        });
        return candidates.FirstOrDefault(IsGameFolder);
    }

    public static List<CheckItem> Run(string? path)
    {
        var list = new List<CheckItem>();
        if (!IsGameFolder(path))
        {
            list.Add(new CheckItem("Папка GTA San Andreas", false, true, "не выбрана или в ней нет gta_sa.exe"));
            return list;
        }

        string exe = Path.Combine(path!, "gta_sa.exe");
        long size = new FileInfo(exe).Length;
        list.Add(new CheckItem("gta_sa.exe", true, true, $"найден ({size:N0} байт)"));
        list.Add(new CheckItem("Версия GTA", size == ExpectedExeSize, false,
            size == ExpectedExeSize
                ? "похоже на 1.0 US"
                : "размер не совпадает с версией 1.0 US, SA-MP 0.3.7 может не запуститься (Steam, Rockstar Launcher, 1.01, 2.0 и Definitive не подходят)"));

        foreach (string name in new[] { "samp.exe", "samp.dll" })
        {
            bool ok = File.Exists(Path.Combine(path!, name));
            list.Add(new CheckItem(name, ok, true, ok ? "найден" : "не найден (установите SA-MP 0.3.7 в папку игры или антивирус удалил файл)"));
        }
        return list;
    }

    public static string? ReadSampNick()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\SAMP");
            return k?.GetValue("PlayerName") as string;
        }
        catch { return null; }
    }

    public static void WriteSampNick(string nick)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\SAMP");
            k.SetValue("PlayerName", nick, RegistryValueKind.String);
        }
        catch (Exception ex) { Log.Write("Не удалось записать ник в реестр: " + ex.Message); }
    }
}

// ---------------------------------------------------------------- Запрос к серверу (SA-MP query, UDP)
internal sealed record ServerInfo(bool Online, int Players, int MaxPlayers, string Hostname, int PingMs, string Error);

internal static class ServerQuery
{
    public static async Task<ServerInfo> QueryAsync()
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync(Config.ServerHost);
            var ip = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip == null) return new ServerInfo(false, 0, 0, "", 0, "DNS: адрес сервера не найден");

            byte[] ipBytes = ip.GetAddressBytes();
            var packet = new byte[11];
            Encoding.ASCII.GetBytes("SAMP").CopyTo(packet, 0);
            ipBytes.CopyTo(packet, 4);
            packet[8] = (byte)(Config.ServerPort & 0xFF);
            packet[9] = (byte)((Config.ServerPort >> 8) & 0xFF);
            packet[10] = (byte)'i';

            using var udp = new UdpClient();
            udp.Connect(ip, Config.ServerPort);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(packet, packet.Length);
            var receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(2500)) != receive)
                return new ServerInfo(false, 0, 0, "", 0, "нет ответа: сервер выключен или закрыт UDP-порт");
            sw.Stop();

            byte[] d = receive.Result.Buffer;
            if (d.Length < 20) return new ServerInfo(false, 0, 0, "", 0, "странный ответ сервера");
            int p = 11;
            p += 1; // пароль
            int players = BitConverter.ToUInt16(d, p); p += 2;
            int max = BitConverter.ToUInt16(d, p); p += 2;
            int len = (int)BitConverter.ToUInt32(d, p); p += 4;
            string name = (len > 0 && p + len <= d.Length) ? Encoding.GetEncoding(1251).GetString(d, p, len) : "";
            return new ServerInfo(true, players, max, name, (int)sw.ElapsedMilliseconds, "");
        }
        catch (Exception ex)
        {
            return new ServerInfo(false, 0, 0, "", 0, ex.Message);
        }
    }
}

// ---------------------------------------------------------------- Окно
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; }
}

internal sealed class MainForm : Form
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private static readonly Color Bg = Color.FromArgb(13, 14, 19);
    private static readonly Color Card = Color.FromArgb(23, 24, 32);
    private static readonly Color Card2 = Color.FromArgb(34, 36, 48);
    private static readonly Color Red = Color.FromArgb(232, 41, 62);
    private static readonly Color RedHover = Color.FromArgb(255, 72, 92);
    private static readonly Color Txt = Color.FromArgb(244, 245, 248);
    private static readonly Color Muted = Color.FromArgb(150, 154, 168);
    private static readonly Color Green = Color.FromArgb(60, 200, 120);

    private readonly Label _srvName = new(), _srvState = new(), _srvPlayers = new(), _srvPing = new();
    private readonly Label _folderLbl = new(), _status = new();
    private readonly Panel _bar = new BufferedPanel();
    private readonly TextBox _nick = new();
    private readonly Button _play;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30000 };
    private double _fill;
    private bool _launching;
    private bool? _lastOnline;

    public MainForm()
    {
        Config.Load();
        Text = "VEXORIUM ROLEPLAY";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 600);
        BackColor = Bg;
        ForeColor = Txt;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;
        Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(60, 62, 76));
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };

        // --- верхняя панель
        var title = new Panel { Bounds = new Rectangle(1, 1, 958, 44), BackColor = Color.FromArgb(18, 19, 25) };
        Controls.Add(title);
        var logo = MakeLabel("VEXORIUM", new Point(18, 7), 15F, FontStyle.Bold, Red);
        var logo2 = MakeLabel("ROLEPLAY", new Point(130, 13), 9.5F, FontStyle.Bold, Txt);
        title.Controls.Add(logo); title.Controls.Add(logo2);
        foreach (Control c in new Control[] { title, logo, logo2 }) EnableDrag(c);

        var minBtn = MakeButton("—", new Rectangle(866, 0, 46, 44), Color.FromArgb(18, 19, 25), Card2, 11F);
        minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;
        var closeBtn = MakeButton("✕", new Rectangle(912, 0, 46, 44), Color.FromArgb(18, 19, 25), Red, 11F);
        closeBtn.Click += (_, _) => Close();
        title.Controls.Add(minBtn); title.Controls.Add(closeBtn);

        // --- большая картинка слева
        var hero = new BufferedPanel { Bounds = new Rectangle(30, 66, 590, 330) };
        hero.Paint += PaintHero;
        Controls.Add(hero);

        // --- карточка сервера справа
        var srv = new BufferedPanel { Bounds = new Rectangle(640, 66, 290, 330), BackColor = Card };
        Controls.Add(srv);
        srv.Controls.Add(MakeLabel("СЕРВЕР", new Point(20, 16), 8.5F, FontStyle.Bold, Muted));
        Setup(_srvName, new Rectangle(20, 38, 250, 26), 12F, FontStyle.Bold, Txt, "VEXORIUM ROLEPLAY");
        Setup(_srvState, new Rectangle(20, 72, 250, 22), 9.5F, FontStyle.Bold, Muted, "● ПРОВЕРКА…");
        Setup(_srvPlayers, new Rectangle(20, 108, 250, 52), 28F, FontStyle.Bold, Txt, "— / —");
        srv.Controls.Add(MakeLabel("игроков онлайн", new Point(22, 160), 9F, FontStyle.Regular, Muted));
        _bar.Bounds = new Rectangle(20, 192, 250, 10);
        _bar.BackColor = Card2;
        _bar.Paint += (_, e) =>
        {
            using var br = new SolidBrush(Red);
            e.Graphics.FillRectangle(br, 0, 0, (int)(_bar.Width * _fill), _bar.Height);
        };
        Setup(_srvPing, new Rectangle(20, 214, 250, 22), 9F, FontStyle.Regular, Muted, "");
        var addr = MakeLabel($"{Config.ServerHost}:{Config.ServerPort}", new Point(20, 244), 8.5F, FontStyle.Regular, Muted);
        var copy = MakeButton("КОПИРОВАТЬ АДРЕС", new Rectangle(20, 276, 250, 34), Card2, Color.FromArgb(48, 50, 66), 9F);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"{Config.ServerHost}:{Config.ServerPort}"); SetStatus("Адрес сервера скопирован."); }
            catch { }
        };
        foreach (Control c in new Control[] { _srvName, _srvState, _srvPlayers, _bar, _srvPing, addr, copy }) srv.Controls.Add(c);

        // --- нижняя панель
        var bottom = new BufferedPanel { Bounds = new Rectangle(30, 416, 900, 160), BackColor = Card };
        Controls.Add(bottom);

        bottom.Controls.Add(MakeLabel("НИКНЕЙМ", new Point(20, 14), 8.5F, FontStyle.Bold, Muted));
        var nickWrap = new Panel { Bounds = new Rectangle(20, 38, 270, 40), BackColor = Card2 };
        _nick.Bounds = new Rectangle(12, 9, 246, 24);
        _nick.BorderStyle = BorderStyle.None;
        _nick.BackColor = Card2;
        _nick.ForeColor = Txt;
        _nick.Font = new Font("Segoe UI", 11F);
        _nick.MaxLength = 20;
        _nick.PlaceholderText = "Name_Surname";
        _nick.Text = !string.IsNullOrWhiteSpace(Config.Nickname) ? Config.Nickname : (GameCheck.ReadSampNick() ?? "");
        nickWrap.Controls.Add(_nick);
        bottom.Controls.Add(nickWrap);
        bottom.Controls.Add(MakeLabel("Формат ника: Имя_Фамилия (латиницей)", new Point(20, 84), 8.5F, FontStyle.Regular, Muted));

        bottom.Controls.Add(MakeLabel("ПАПКА С ИГРОЙ", new Point(320, 14), 8.5F, FontStyle.Bold, Muted));
        Setup(_folderLbl, new Rectangle(320, 36, 330, 24), 9.5F, FontStyle.Regular, Txt, "");
        _folderLbl.AutoEllipsis = true;
        bottom.Controls.Add(_folderLbl);

        var chooseBtn = MakeButton("ВЫБРАТЬ ПАПКУ", new Rectangle(320, 70, 140, 36), Card2, Color.FromArgb(48, 50, 66), 8.5F);
        chooseBtn.Click += (_, _) => ChooseFolder();
        var diagBtn = MakeButton("ДИАГНОСТИКА", new Rectangle(468, 70, 120, 36), Card2, Color.FromArgb(48, 50, 66), 8.5F);
        diagBtn.Click += async (_, _) => await ShowDiagnosticsAsync();
        var logBtn = MakeButton("ЛОГ", new Rectangle(596, 70, 54, 36), Card2, Color.FromArgb(48, 50, 66), 8.5F);
        logBtn.Click += (_, _) => OpenLog();
        bottom.Controls.Add(chooseBtn); bottom.Controls.Add(diagBtn); bottom.Controls.Add(logBtn);

        _play = MakeButton("▶  ИГРАТЬ", new Rectangle(676, 20, 204, 92), Red, RedHover, 17F);
        _play.Click += async (_, _) => await LaunchAsync();
        bottom.Controls.Add(_play);

        Setup(_status, new Rectangle(20, 124, 860, 24), 9F, FontStyle.Regular, Muted, "Готов к запуску.");
        bottom.Controls.Add(_status);

        // --- запуск
        Shown += async (_, _) =>
        {
            if (!GameCheck.IsGameFolder(Config.GamePath))
            {
                string? found = GameCheck.AutoDetect();
                if (found != null)
                {
                    Config.GamePath = found;
                    Config.Save();
                    Log.Write("Папка игры найдена автоматически: " + found);
                }
            }
            RefreshFolder();
            await RefreshServerAsync();
        };
        _timer.Tick += async (_, _) => await RefreshServerAsync();
        _timer.Start();
    }

    // ------------------------------------------------------------ рисование
    private void PaintHero(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var r = ((Control)sender!).ClientRectangle;

        using (var bg = new LinearGradientBrush(r, Color.FromArgb(64, 10, 22), Color.FromArgb(14, 15, 22), 35f))
            g.FillRectangle(bg, r);
        using (var glow = new SolidBrush(Color.FromArgb(45, Red)))
            g.FillEllipse(glow, r.Width - 320, -140, 460, 460);
        using (var glow2 = new SolidBrush(Color.FromArgb(25, Red)))
            g.FillEllipse(glow2, r.Width - 200, 120, 300, 300);
        using (var stripe = new SolidBrush(Red))
            g.FillRectangle(stripe, 0, 0, 6, r.Height);

        using var big = new Font("Segoe UI", 44F, FontStyle.Bold);
        using var mid = new Font("Segoe UI", 20F, FontStyle.Bold);
        using var small = new Font("Segoe UI", 11F, FontStyle.Bold);
        using var tiny = new Font("Segoe UI", 10F);
        using var red = new SolidBrush(Red);
        using var white = new SolidBrush(Txt);
        using var muted = new SolidBrush(Muted);

        g.DrawString("VEXORIUM", big, red, 36, 70);
        g.DrawString("R O L E P L A Y", mid, white, 44, 146);
        g.DrawString("ТВОЯ ИСТОРИЯ НАЧИНАЕТСЯ ЗДЕСЬ", small, white, 46, 214);
        g.DrawString("GTA San Andreas  •  SA-MP 0.3.7  •  Russian RP", tiny, muted, 46, 244);
    }

    private static void Setup(Label l, Rectangle b, float size, FontStyle style, Color color, string text)
    {
        l.Bounds = b;
        l.Font = new Font("Segoe UI", size, style);
        l.ForeColor = color;
        l.BackColor = Color.Transparent;
        l.Text = text;
        l.AutoSize = false;
    }

    private static Label MakeLabel(string text, Point loc, float size, FontStyle style, Color color) => new()
    {
        Text = text, Location = loc, AutoSize = true, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, style), ForeColor = color
    };

    private static Button MakeButton(string text, Rectangle b, Color back, Color hover, float size)
    {
        var btn = new Button
        {
            Text = text, Bounds = b, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Txt,
            Font = new Font("Segoe UI", size, FontStyle.Bold), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = hover;
        btn.FlatAppearance.MouseDownBackColor = hover;
        return btn;
    }

    private void EnableDrag(Control c) => c.MouseDown += (_, e) =>
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    };

    private void SetStatus(string text) => _status.Text = text;

    // ------------------------------------------------------------ действия
    private void RefreshFolder()
    {
        bool ok = GameCheck.IsGameFolder(Config.GamePath);
        _folderLbl.Text = ok ? Config.GamePath : "не выбрана — нажмите «Выбрать папку»";
        _folderLbl.ForeColor = ok ? Txt : Red;
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите папку, в которой лежит gta_sa.exe",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return; // старый путь не стираем

        if (!GameCheck.IsGameFolder(dialog.SelectedPath))
        {
            MessageBox.Show("В выбранной папке нет gta_sa.exe. Выберите именно папку GTA San Andreas.",
                "Папка не подходит", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Config.GamePath = dialog.SelectedPath;
        Config.Save();
        Log.Write("Папка игры выбрана вручную: " + Config.GamePath);
        RefreshFolder();
    }

    private async Task RefreshServerAsync()
    {
        var info = await ServerQuery.QueryAsync();
        if (IsDisposed) return;

        if (_lastOnline != info.Online)
        {
            Log.Write(info.Online
                ? $"Сервер онлайн: {info.Players}/{info.MaxPlayers}, пинг {info.PingMs} мс"
                : "Сервер недоступен: " + info.Error);
            _lastOnline = info.Online;
        }

        _srvState.Text = info.Online ? "● ОНЛАЙН" : "● НЕДОСТУПЕН";
        _srvState.ForeColor = info.Online ? Green : Red;
        _srvName.Text = info.Online && info.Hostname.Length > 0 ? info.Hostname : "VEXORIUM ROLEPLAY";
        _srvPlayers.Text = info.Online ? $"{info.Players} / {info.MaxPlayers}" : "— / —";
        _srvPing.Text = info.Online ? $"Пинг: {info.PingMs} мс" : info.Error;
        _fill = info.Online && info.MaxPlayers > 0 ? Math.Min(1.0, (double)info.Players / info.MaxPlayers) : 0;
        _bar.Invalidate();
    }

    private async Task LaunchAsync()
    {
        if (_launching) return;

        string nick = _nick.Text.Trim();
        if (nick.Length > 20 || !Regex.IsMatch(nick, @"^[A-Za-z]{2,}_[A-Za-z]{2,}$"))
        {
            MessageBox.Show("Введите ник в формате Имя_Фамилия латиницей, например John_Miller.",
                "Неверный ник", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var checks = GameCheck.Run(Config.GamePath);
        var critical = checks.Where(c => !c.Ok && c.Critical).ToList();
        if (critical.Count > 0)
        {
            string text = string.Join("\n", critical.Select(c => $"✖ {c.Name}: {c.Details}"));
            Log.Write("Запуск отменён:\n" + text);
            MessageBox.Show("Игру запустить нельзя:\n\n" + text, "Проблема с файлами", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var warnings = checks.Where(c => !c.Ok && !c.Critical).ToList();
        if (warnings.Count > 0)
        {
            string text = string.Join("\n", warnings.Select(c => $"⚠ {c.Name}: {c.Details}"));
            Log.Write("Предупреждение:\n" + text);
            if (MessageBox.Show(text + "\n\nВсё равно попробовать запустить?", "Предупреждение",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }

        Config.Nickname = nick;
        Config.Save();
        GameCheck.WriteSampNick(nick);

        string samp = Path.Combine(Config.GamePath, "samp.exe");
        string args = $"{Config.ServerHost}:{Config.ServerPort}";
        Log.Write($"Запуск: \"{samp}\" {args} (ник {nick})");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = samp,
                Arguments = args,
                WorkingDirectory = Config.GamePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write("ОШИБКА запуска samp.exe: " + ex);
            MessageBox.Show("Не удалось запустить SA-MP:\n" + ex.Message, "Ошибка запуска",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _launching = true;
        _play.Enabled = false;
        SetStatus("Запускаем игру…");
        await Task.Delay(10000);

        bool started = Process.GetProcessesByName("gta_sa").Length > 0;
        Log.Write(started
            ? "gta_sa.exe запущен."
            : "ВНИМАНИЕ: через 10 секунд процесс gta_sa.exe не найден, игра не запустилась.");
        if (IsDisposed) return;
        _launching = false;
        _play.Enabled = true;
        SetStatus(started
            ? "Игра запущена. Приятной игры!"
            : "Игра не запустилась. Нажмите «Диагностика» или откройте лог.");
    }

    private async Task ShowDiagnosticsAsync()
    {
        SetStatus("Идёт диагностика…");
        var sb = new StringBuilder();
        foreach (var c in GameCheck.Run(Config.GamePath))
            sb.AppendLine($"{(c.Ok ? "✔" : "✖")} {c.Name} — {c.Details}");

        var info = await ServerQuery.QueryAsync();
        sb.AppendLine(info.Online
            ? $"✔ Сервер {Config.ServerHost}:{Config.ServerPort} отвечает ({info.Players}/{info.MaxPlayers}, {info.PingMs} мс)"
            : $"✖ Сервер не отвечает — {info.Error}");

        Log.Write("Диагностика:\n" + sb);
        SetStatus("Диагностика завершена.");
        MessageBox.Show(sb.ToString(), "Диагностика", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OpenLog()
    {
        try
        {
            if (!File.Exists(Config.LogFile)) { MessageBox.Show("Лог пока пуст."); return; }
            Process.Start(new ProcessStartInfo(Config.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("Не удалось открыть лог:\n" + ex.Message); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
