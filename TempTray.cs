// Tray icons with history graphs, like Process Explorer's: CPU load, RAM use, and the temperatures and fan speeds read from
// the Dell BIOS "data vault" WMI interface (DDVWmiMethodFunction). Dell decoding follows the Linux dell-wmi-ddv driver.
// Compiled by build.cmd with the .NET Framework csc (C# 5), no dependencies. Keep this file ASCII: csc reads it in the
// system code page.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

static class Program
{
    // Usage: TempTray.exe [--config <path>]; without it, settings.txt sits next to the exe.
    [STAThread]
    static void Main(string[] args)
    {
        string config = null;
        int at = Array.FindIndex(args, a => a.Equals("--config", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < args.Length)
            config = System.IO.Path.GetFullPath(args[at + 1]);

        bool created;
        using (var single = new System.Threading.Mutex(true, "TempTray-single-instance", out created))
        {
            if (!created)
                return;
            Application.EnableVisualStyles();
            Application.Run(new TrayApp(config));
        }
    }
}

enum Kind { Load, Temperature, Fan }

class Reading
{
    public string Key;
    public string Name;
    public Kind Kind;
    public double Value;
    // CPU only: the kernel-mode share of Value, drawn in red like Process Explorer does.
    public double Kernel;
    // Extra tooltip text, such as "11.2 of 15.7 GB".
    public string Detail;
}

// CPU load and RAM use from kernel32. Performance counters would need localized counter names on non-English Windows.
class SystemUsage
{
    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

    long _idle, _kernel, _user;

    public SystemUsage() { GetSystemTimes(out _idle, out _kernel, out _user); }

    public IEnumerable<Reading> Read()
    {
        long idle, kernel, user;
        GetSystemTimes(out idle, out kernel, out user);
        // Kernel time includes idle time.
        double total = (kernel - _kernel) + (user - _user);
        double idleDelta = idle - _idle;
        double kernelBusy = (kernel - _kernel) - idleDelta;
        _idle = idle;
        _kernel = kernel;
        _user = user;
        double load = total > 0 ? 100 * (total - idleDelta) / total : 0;
        double kernelLoad = total > 0 ? 100 * kernelBusy / total : 0;
        yield return new Reading { Key = "cpu-load", Name = "CPU load", Kind = Kind.Load, Value = load, Kernel = kernelLoad };

        var memory = new MemoryStatus();
        GlobalMemoryStatusEx(memory);
        const double GB = 1024.0 * 1024 * 1024;
        string detail = string.Format("{0:0.0} of {1:0.0} GB", (memory.TotalPhys - memory.AvailPhys) / GB, memory.TotalPhys / GB);
        yield return new Reading { Key = "ram", Name = "RAM", Kind = Kind.Load, Value = memory.MemoryLoad, Detail = detail };
    }
}

// Talks to the BIOS. Every call is read-only.
class DellSensors
{
    readonly ManagementObject _ddv;

    public DellSensors()
    {
        using (var cls = new ManagementClass(@"root\wmi", "DDVWmiMethodFunction", null))
            _ddv = cls.GetInstances().Cast<ManagementObject>().First();
    }

    public List<Reading> Read()
    {
        var result = new List<Reading>();
        var seen = new Dictionary<string, int>();

        // Thermal entries are 5 bytes: type, now (signed degrees C), min, max, unknown; 0xFF ends the list.
        byte[] raw = Raw("ThermalSensorInformation");
        for (int i = 0; i + 4 < raw.Length && raw[i] != 0xFF; i += 5)
            result.Add(Make(seen, Kind.Temperature, raw[i], (sbyte)raw[i + 1]));

        // Fan entries are 3 bytes: type, rpm little-endian; 0xFF ends the list.
        raw = Raw("FanSensorInformation");
        for (int i = 0; i + 2 < raw.Length && raw[i] != 0xFF; i += 3)
            result.Add(Make(seen, Kind.Fan, raw[i], raw[i + 1] | (raw[i + 2] << 8)));

        // The battery index is 1-based; the value is in tenths of a kelvin.
        uint battery = (uint)Invoke("BatteryTemperature", 1)["argr"];
        if (battery > 0)
            result.Add(new Reading { Key = "battery", Name = "Battery", Kind = Kind.Temperature, Value = Math.Round(battery / 10.0 - 273.15) });

        return result;
    }

    byte[] Raw(string method)
    {
        ManagementBaseObject output = Invoke(method, 0);
        byte[] data = (byte[])output["RawData"] ?? new byte[0];
        int size = (int)Math.Min((uint)output["RawSize"], (uint)data.Length);
        return data.Take(size).ToArray();
    }

    ManagementBaseObject Invoke(string method, uint arg)
    {
        ManagementBaseObject input = _ddv.GetMethodParameters(method);
        input["arg2"] = arg;
        return _ddv.InvokeMethod(method, input, null);
    }

    // Type names from the Linux dell-wmi-ddv driver; repeated types get a number so each has a stable key.
    static Reading Make(Dictionary<string, int> seen, Kind kind, byte type, double value)
    {
        bool isFan = kind == Kind.Fan;
        string baseName = isFan ? FanName(type) : ThermalName(type);
        string baseKey = (isFan ? "fan:" : "temp:") + type.ToString("X2");
        int n;
        seen.TryGetValue(baseKey, out n);
        seen[baseKey] = ++n;
        string name = n == 1 ? baseName : baseName + " " + n;
        return new Reading { Key = baseKey + "#" + n, Name = name, Kind = kind, Value = value };
    }

    static string ThermalName(byte type)
    {
        switch (type)
        {
            case 0x00: return "CPU temperature";
            case 0x11: return "GPU temperature";
            case 0x22: return "Memory";
            case 0x44: return "Ambient";
            case 0x52: return "RAM modules";
            default: return "Sensor " + type.ToString("X2");
        }
    }

    static string FanName(byte type)
    {
        switch (type)
        {
            case 0x00: return "CPU fan";
            case 0x02: return "GPU fan";
            default: return "Fan " + type.ToString("X2");
        }
    }
}

class TrayApp : ApplicationContext
{
    const int HistoryLength = 60;
    const string TaskName = "TempTray";
    static readonly Color Green = Color.FromArgb(40, 160, 60);
    static readonly Color Orange = Color.FromArgb(230, 150, 20);
    static readonly Color Red = Color.FromArgb(220, 50, 47);
    static readonly Color Salmon = Color.FromArgb(240, 120, 110);
    // The degree sign as a char code: csc reads this file in the system code page, which garbles a literal one.
    const char Degree = (char)176;

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    readonly SystemUsage _usage = new SystemUsage();
    readonly DellSensors _dell;
    readonly Timer _timer = new Timer { Interval = 2000 };
    readonly Dictionary<string, Queue<Reading>> _history = new Dictionary<string, Queue<Reading>>();
    readonly Dictionary<string, NotifyIcon> _icons = new Dictionary<string, NotifyIcon>();
    readonly Dictionary<string, IntPtr> _iconHandles = new Dictionary<string, IntPtr>();
    readonly ContextMenuStrip _menu = new ContextMenuStrip { ShowCheckMargin = false, ShowImageMargin = true };
    readonly Settings _settings;
    // The --config path as given, or null: the logon task passes it on, so the app started at logon reads the same file.
    readonly string _configArg;
    List<Reading> _last = new List<Reading>();
    string _dellError;
    bool _keepMenuOpen;

    public TrayApp(string configArg)
    {
        _configArg = configArg;
        _settings = Settings.Load(configArg ?? System.IO.Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.txt"));
        try
        {
            _dell = new DellSensors();
        }
        catch (Exception e)
        {
            _dellError = "Dell sensors unavailable: " + e.Message;
        }
        _menu.Opening += (s, e) => BuildMenu();
        // Ticking a sensor keeps the menu open, so several can be chosen in one go.
        _menu.Closing += (s, e) =>
        {
            e.Cancel = e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && _keepMenuOpen;
            _keepMenuOpen = false;
        };
        _timer.Tick += (s, e) => Refresh();
        Refresh();
        _timer.Start();
    }

    void Refresh()
    {
        var readings = _usage.Read().ToList();
        if (_dell != null)
        {
            try
            {
                readings.AddRange(_dell.Read());
                _dellError = null;
            }
            catch (Exception e)
            {
                _dellError = e.Message;
            }
        }
        _last = readings;

        foreach (Reading r in _last)
        {
            Queue<Reading> h;
            if (!_history.TryGetValue(r.Key, out h))
                _history[r.Key] = h = new Queue<Reading>();
            h.Enqueue(r);
            while (h.Count > HistoryLength)
                h.Dequeue();
        }

        SyncIcons();
    }

    bool IsShown(string key) { return !_settings.Hidden.Contains(key); }

    // Adds and removes tray icons to match the settings, then redraws each.
    void SyncIcons()
    {
        foreach (string key in _icons.Keys.Where(k => !IsShown(k)).ToList())
            RemoveIcon(key);

        foreach (Reading r in _last.Where(x => IsShown(x.Key)))
        {
            NotifyIcon icon;
            if (!_icons.TryGetValue(r.Key, out icon))
            {
                icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
                _icons[r.Key] = icon;
            }
            SetIcon(r.Key, icon, Render(r));
            icon.Text = Clip(Tooltip(r), 63);
        }
    }

    string Tooltip(Reading r)
    {
        Queue<Reading> h = _history[r.Key];
        int minutes = (int)Math.Ceiling(h.Count * _timer.Interval / 60000.0);
        string text = string.Format("{0}: {1}", r.Name, Format(r, r.Value));
        if (r.Detail != null)
            text += ", " + r.Detail;
        return text + string.Format(" (max {0} in {1} min)", Format(r, h.Max(x => x.Value)), minutes);
    }

    static string Format(Reading r, double value)
    {
        switch (r.Kind)
        {
            case Kind.Fan: return value.ToString("0") + " rpm";
            case Kind.Load: return value.ToString("0") + "%";
            default: return value.ToString("0") + " " + Degree + "C";
        }
    }

    static string Clip(string s, int max) { return s.Length <= max ? s : s.Substring(0, max); }

    void SetIcon(string key, NotifyIcon icon, Bitmap bmp)
    {
        IntPtr handle = bmp.GetHicon();
        icon.Icon = Icon.FromHandle(handle);
        bmp.Dispose();
        IntPtr old;
        if (_iconHandles.TryGetValue(key, out old))
            DestroyIcon(old);
        _iconHandles[key] = handle;
    }

    void RemoveIcon(string key)
    {
        _icons[key].Visible = false;
        _icons[key].Dispose();
        _icons.Remove(key);
        DestroyIcon(_iconHandles[key]);
        _iconHandles.Remove(key);
    }

    Bitmap Render(Reading r)
    {
        Size size = SystemInformation.SmallIconSize;
        var bmp = new Bitmap(size.Width, size.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            if (_settings.Mode == Mode.Number)
            {
                DrawNumber(g, size, r);
                return bmp;
            }
            DrawGraph(g, size, r);
            if (_settings.Mode == Mode.GraphWithNumber)
                DrawSmallNumber(g, size, r);
        }
        return bmp;
    }

    // 3x5 pixel digits, one string per row: a vector font blurs into mush at this size, hand-set pixels stay sharp.
    static readonly Dictionary<char, string[]> PixelGlyphs = new Dictionary<char, string[]>
    {
        { '0', new[] { "###", "#.#", "#.#", "#.#", "###" } },
        { '1', new[] { ".#.", "##.", ".#.", ".#.", "###" } },
        { '2', new[] { "###", "..#", "###", "#..", "###" } },
        { '3', new[] { "###", "..#", "###", "..#", "###" } },
        { '4', new[] { "#.#", "#.#", "###", "..#", "..#" } },
        { '5', new[] { "###", "#..", "###", "..#", "###" } },
        { '6', new[] { "###", "#..", "###", "#.#", "###" } },
        { '7', new[] { "###", "..#", "..#", "..#", "..#" } },
        { '8', new[] { "###", "#.#", "###", "#.#", "###" } },
        { '9', new[] { "###", "#.#", "###", "..#", "###" } },
        { '.', new[] { ".", ".", ".", ".", "#" } },
        { '%', new[] { "#.#", "..#", ".#.", "#..", "#.#" } },
        // The degree sign alone, without the C: the icon has room for two digits and one narrow mark.
        { 'o', new[] { "##", "##", "..", "..", ".." } },
    };

    // Fans carry no unit: "5.4" already reads as thousands of rpm next to the temperatures.
    static string SmallUnit(Reading r)
    {
        switch (r.Kind)
        {
            case Kind.Load: return "%";
            case Kind.Temperature: return "o";
            default: return "";
        }
    }

    // The value at the top of the graph in the pixel font: white with a dark 1-pixel outline, so it reads over any bar color.
    // Each font pixel becomes a square of `scale` icon pixels, so it grows with larger tray icons on high-DPI screens.
    static void DrawSmallNumber(Graphics g, Size size, Reading r)
    {
        string text = ShortValue(r) + SmallUnit(r);
        int scale = Math.Max(1, size.Height / 16);
        var lit = new List<Point>();
        int x = 0;
        foreach (char ch in text)
        {
            string[] rows = PixelGlyphs[ch];
            for (int row = 0; row < rows.Length; row++)
                for (int col = 0; col < rows[row].Length; col++)
                    if (rows[row][col] == '#')
                        lit.Add(new Point(x + col, row));
            x += rows[0].Length + 1;
        }
        int width = x - 1;
        int left = (size.Width / scale - width) / 2;
        // One pixel down from the border, inside the box.
        const int top = 2;

        using (var dark = new SolidBrush(Color.FromArgb(30, 30, 30)))
        {
            foreach (Point p in lit)
                g.FillRectangle(dark, (left + p.X - 1) * scale, (top + p.Y - 1) * scale, 3 * scale, 3 * scale);
        }
        foreach (Point p in lit)
            g.FillRectangle(Brushes.White, (left + p.X) * scale, (top + p.Y) * scale, scale, scale);
    }

    // Fits two digits in an icon: fans in thousands of rpm, everything else capped at 99.
    static string ShortValue(Reading r)
    {
        if (r.Kind == Kind.Fan)
            return (r.Value / 1000).ToString("0.#");
        return Math.Min(r.Value, 99).ToString("0");
    }

    // Like Process Explorer: a bordered box, history filled from the bottom, newest sample on the right.
    void DrawGraph(Graphics g, Size size, Reading current)
    {
        int w = size.Width, h = size.Height;
        g.Clear(Color.FromArgb(250, 250, 250));
        Reading[] samples = _history[current.Key].ToArray();
        int inner = h - 2;
        int columns = w - 2;
        for (int c = 0; c < columns; c++)
        {
            int index = samples.Length - columns + c;
            if (index < 0)
                continue;
            Reading r = samples[index];
            DrawBar(g, c, inner, Scale(r, r.Value), Fill(r));
            if (r.Kernel > 0)
                DrawBar(g, c, inner, Scale(r, r.Kernel), Red);
        }
        using (var pen = new Pen(Color.FromArgb(90, 90, 90)))
            g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
    }

    static void DrawBar(Graphics g, int column, int inner, double scaled, Color color)
    {
        int bar = (int)Math.Round(scaled * inner);
        if (bar <= 0)
            return;
        using (var brush = new SolidBrush(color))
            g.FillRectangle(brush, 1 + column, 1 + inner - bar, 1, bar);
    }

    void DrawNumber(Graphics g, Size size, Reading r)
    {
        g.Clear(Fill(r));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        string text = ShortValue(r);
        float fontSize = size.Height * (text.Length > 2 ? 0.5f : 0.68f);
        using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(text, font, Brushes.White, new RectangleF(-1, 0, size.Width + 2, size.Height + 1), format);
    }

    // Temperatures span 30-100 C (100 is the usual Intel TjMax), fans 0-6000 rpm, loads 0-100%.
    static double Scale(Reading r, double value)
    {
        double scaled;
        switch (r.Kind)
        {
            case Kind.Fan: scaled = value / 6000; break;
            case Kind.Load: scaled = value / 100; break;
            default: scaled = (value - 30) / 70; break;
        }
        return Math.Max(0, Math.Min(1, scaled));
    }

    // Loads keep Process Explorer's colors; temperatures and fans go green, orange, red as they climb.
    static Color Fill(Reading r)
    {
        if (r.Kind == Kind.Load)
            return r.Key == "ram" ? Salmon : Green;
        double s = Scale(r, r.Value);
        if (s >= 0.72)
            return Red;
        if (s >= 0.5)
            return Orange;
        return Green;
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripLabel("Show in taskbar:") { Font = new Font(_menu.Font, FontStyle.Bold) });
        foreach (Reading r in _last)
        {
            // The value goes in the right-aligned shortcut column; a tab in the text renders as nothing.
            var item = new ToolStripMenuItem(r.Name) { Tag = r.Key, ShortcutKeyDisplayString = Format(r, r.Value) };
            item.Image = CheckBoxImage(IsShown(r.Key));
            item.Click += (s, e) => ToggleShown((ToolStripMenuItem)s);
            _menu.Items.Add(item);
        }
        if (_dellError != null)
            _menu.Items.Add(new ToolStripLabel(Clip(_dellError, 80)) { ForeColor = Red });
        _menu.Items.Add(new ToolStripSeparator());

        var overlay = new ToolStripMenuItem("Draw a small number over the graph") { Tag = Mode.GraphWithNumber, Image = CheckBoxImage(_settings.Mode == Mode.GraphWithNumber) };
        overlay.Click += (s, e) => ToggleMode((ToolStripMenuItem)s, Mode.GraphWithNumber);
        _menu.Items.Add(overlay);

        var number = new ToolStripMenuItem("Show only the number, no graph") { Tag = Mode.Number, Image = CheckBoxImage(_settings.Mode == Mode.Number) };
        number.Click += (s, e) => ToggleMode((ToolStripMenuItem)s, Mode.Number);
        _menu.Items.Add(number);

        var logon = new ToolStripMenuItem("Start at logon (as admin, via Task Scheduler)") { Image = CheckBoxImage(LogonTaskExists()) };
        logon.Click += (s, e) => SetLogonTask(!LogonTaskExists(), _configArg);
        _menu.Items.Add(logon);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (s, e) => Exit());
    }

    // A real checkbox, ticked or empty: a menu check mark shows nothing for unticked items, so they wouldn't look choosable.
    static Bitmap CheckBoxImage(bool ticked)
    {
        var bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            CheckBoxState state = ticked ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal;
            Size glyph = CheckBoxRenderer.GetGlyphSize(g, state);
            CheckBoxRenderer.DrawCheckBox(g, new Point((16 - glyph.Width) / 2, (16 - glyph.Height) / 2), state);
        }
        return bmp;
    }

    // The two number options exclude each other: ticking one unticks the other, unticking returns to the plain graph.
    void ToggleMode(ToolStripMenuItem item, Mode mode)
    {
        _settings.Mode = _settings.Mode == mode ? Mode.Graph : mode;
        _settings.Save();
        foreach (ToolStripMenuItem other in _menu.Items.OfType<ToolStripMenuItem>().Where(i => i.Tag is Mode))
            other.Image = CheckBoxImage(_settings.Mode == (Mode)other.Tag);
        _keepMenuOpen = true;
        SyncIcons();
    }

    void ToggleShown(ToolStripMenuItem item)
    {
        string key = (string)item.Tag;
        bool showing = IsShown(key);
        // The menu lives on the icons, so the last one can't be hidden.
        if (showing && _icons.Count <= 1)
            return;
        if (showing)
            _settings.Hidden.Add(key);
        else
            _settings.Hidden.Remove(key);
        _settings.Save();
        item.Image = CheckBoxImage(!showing);
        _keepMenuOpen = true;
        SyncIcons();
    }

    // The sensors need admin, so the logon task runs with highest privileges; a Run registry key would show a UAC prompt at every logon.
    static bool LogonTaskExists() { return Schtasks("/Query /TN " + TaskName) == 0; }

    static void SetLogonTask(bool enable, string configArg)
    {
        if (!enable)
        {
            if (Schtasks("/Delete /F /TN " + TaskName) != 0)
                MessageBox.Show("Task Scheduler refused to delete the " + TaskName + " task.", "TempTray");
            return;
        }

        // The task comes from XML because schtasks' /SC ONLOGON flags leave Windows' defaults on, and those suit no tray app
        // on a laptop: no start on battery, a stop when unplugged, and a kill after 3 days.
        string xml = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TempTray-task.xml");
        File.WriteAllText(xml, LogonTaskXml(configArg), System.Text.Encoding.Unicode);
        try
        {
            if (Schtasks("/Create /F /TN " + TaskName + " /XML \"" + xml + "\"") != 0)
                MessageBox.Show("Task Scheduler refused to create the " + TaskName + " task.", "TempTray");
        }
        finally
        {
            File.Delete(xml);
        }
    }

    static string LogonTaskXml(string configArg)
    {
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        string exe = Application.ExecutablePath;
        string arguments = configArg == null ? "" : "<Arguments>--config \"" + Escape(configArg) + "\"</Arguments>";
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
            + "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Escape(user) + "</UserId></LogonTrigger></Triggers>"
            + "<Principals><Principal id=\"Author\"><UserId>" + Escape(user) + "</UserId>"
            + "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
            + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"
            + "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
            + "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>"
            + "<Actions Context=\"Author\"><Exec><Command>\"" + Escape(exe) + "\"</Command>" + arguments
            + "<WorkingDirectory>" + Escape(System.IO.Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>"
            + "</Task>";
    }

    static string Escape(string s) { return System.Security.SecurityElement.Escape(s); }

    static int Schtasks(string args)
    {
        var info = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false };
        using (Process p = Process.Start(info))
        {
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    void Exit()
    {
        _timer.Stop();
        foreach (string key in _icons.Keys.ToList())
            RemoveIcon(key);
        ExitThread();
    }
}

enum Mode { Graph, GraphWithNumber, Number }

// A text file, settings.txt next to the exe unless --config names another: "mode=<Mode>" on its own line, then
// "hide:<key>" per hidden item. Hidden rather than shown items are stored, so a new item appears by itself.
class Settings
{
    public Mode Mode;
    public HashSet<string> Hidden = new HashSet<string>();
    string Path;

    public static Settings Load(string path)
    {
        var s = new Settings { Path = path };
        if (!File.Exists(path))
            return s;
        foreach (string line in File.ReadAllLines(path).Select(l => l.Trim()))
        {
            if (line.StartsWith("mode="))
                Enum.TryParse(line.Substring(5), out s.Mode);
            else if (line.StartsWith("hide:"))
                s.Hidden.Add(line.Substring(5));
        }
        return s;
    }

    public void Save()
    {
        string folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        var lines = new List<string>();
        lines.Add("mode=" + Mode);
        lines.AddRange(Hidden.OrderBy(k => k).Select(k => "hide:" + k));
        File.WriteAllLines(Path, lines);
    }
}
