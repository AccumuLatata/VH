using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LossLock
{
    /// <summary>
    /// Read-only countdown. Closing it does not stop the strategy, and updating
    /// it does not take focus from the DOM. No buttons.
    /// </summary>
    internal sealed class StatusWindow : IDisposable
    {
        private const int OpenTimeoutMilliseconds = 2000;
        private const int JoinTimeoutMilliseconds = 2000;

        private readonly object gate = new object();
        private readonly Thread thread;
        private readonly StatusForm form;
        private readonly Life life;
        private bool disposed;

        private StatusWindow(StatusForm form, Thread thread, Life life)
        {
            this.form = form;
            this.thread = thread;
            this.life = life;
        }

        public bool IsOpen
        {
            get
            {
                lock (this.gate)
                    return !this.disposed && this.life.IsOpen && !this.form.IsDisposed;
            }
        }

        public static StatusWindow TryOpen(out string error)
        {
            error = null;
            Exception failure = null;
            StatusForm created = null;
            Life life = new Life();
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            Thread thread = new Thread(() =>
            {
                try
                {
                    try
                    {
                        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    created = new StatusForm();
                    created.FormClosed += delegate { life.MarkClosed(); };
                    created.Shown += delegate
                    {
                        if (life.IsAbandoned)
                        {
                            created.Close();
                            return;
                        }

                        ready.Set();
                    };
                    Application.Run(created);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    ready.Set();
                }
            });

            thread.IsBackground = true;
            thread.Name = "LossLock status";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            bool shown = ready.Wait(OpenTimeoutMilliseconds);
            if (!shown || failure != null || created == null || !life.IsOpen)
            {
                life.Abandon();
                error = failure != null ? failure.Message : "Das Fenster hat sich nicht rechtzeitig geöffnet.";
                CloseForm(created);
                if (thread.IsAlive)
                    thread.Join(JoinTimeoutMilliseconds);
                return null;
            }

            return new StatusWindow(created, thread, life);
        }

        public void Publish(StatusSnapshot snapshot, string accountName, string lastRoundTrip)
        {
            StatusForm target;
            lock (this.gate)
            {
                if (this.disposed || !this.life.IsOpen || this.form.IsDisposed)
                    return;
                target = this.form;
            }

            try
            {
                target.BeginInvoke(new Action(() => target.Apply(snapshot, accountName, lastRoundTrip)));
            }
            catch (InvalidOperationException)
            {
                // ObjectDisposedException is an InvalidOperationException.
                // The form can close between the check and the marshal.
            }
        }

        public void Dispose()
        {
            StatusForm target;
            Thread worker;
            lock (this.gate)
            {
                if (this.disposed)
                    return;
                this.disposed = true;
                this.life.MarkClosed();
                target = this.form;
                worker = this.thread;
            }

            CloseForm(target);
            if (worker != null && worker.IsAlive)
                worker.Join(JoinTimeoutMilliseconds);
        }

        private static void CloseForm(StatusForm form)
        {
            if (form == null)
                return;

            try
            {
                if (!form.IsDisposed && form.IsHandleCreated)
                    form.BeginInvoke(new Action(form.Close));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private sealed class Life
        {
            private readonly object gate = new object();
            private bool open = true;
            private bool abandoned;

            public bool IsOpen
            {
                get
                {
                    lock (this.gate)
                        return this.open && !this.abandoned;
                }
            }

            public bool IsAbandoned
            {
                get
                {
                    lock (this.gate)
                        return this.abandoned;
                }
            }

            public void MarkClosed()
            {
                lock (this.gate)
                    this.open = false;
            }

            public void Abandon()
            {
                lock (this.gate)
                    this.abandoned = true;
            }
        }

        private sealed class StatusForm : Form
        {
            private static readonly Color Background = Color.FromArgb(17, 19, 24);
            private static readonly Color Ink = Color.FromArgb(232, 230, 227);
            private static readonly Color Muted = Color.FromArgb(168, 164, 156);
            private static readonly Color Amber = Color.FromArgb(240, 193, 74);

            private readonly Label accountLabel;
            private readonly Label stateLabel;
            private readonly Label heroLabel;
            private readonly Label detailLabel;
            private readonly Label seriesLabel;
            private readonly Label lastLabel;
            private readonly Font heroFont;
            private long lastSaveTick;

            public StatusForm()
            {
                this.Font = SystemFonts.MessageBoxFont;
                this.heroFont = new Font(this.Font.FontFamily, 28f, FontStyle.Bold, GraphicsUnit.Point);
                this.Text = "Loss Lock";
                this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                this.StartPosition = FormStartPosition.Manual;
                this.ShowInTaskbar = true;
                this.TopMost = true;
                this.MaximizeBox = false;
                this.MinimizeBox = false;
                this.BackColor = Background;
                this.ForeColor = Ink;
                this.ClientSize = new Size(296, 220);

                this.accountLabel = this.AddLabel(10, 22, Ink);
                this.stateLabel = this.AddLabel(34, 22, Ink);
                this.heroLabel = this.AddLabel(60, 52, Ink);
                this.heroLabel.Font = this.heroFont;
                this.accountLabel.Text = "Loss Lock";
                this.stateLabel.Text = "Startet";
                this.detailLabel = this.AddLabel(114, 40, Muted);
                this.seriesLabel = this.AddLabel(156, 22, Ink);
                this.lastLabel = this.AddLabel(182, 28, Muted);

                this.Location = ResolveLocation(this.Size);
                this.LocationChanged += this.OnLocationChanged;
                this.FormClosing += delegate { this.SaveLocation(); };
                this.FormClosed += delegate { this.heroFont.Dispose(); };
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            public void Apply(StatusSnapshot snapshot, string accountName, string lastRoundTrip)
            {
                if (this.IsDisposed)
                    return;

                this.accountLabel.Text = string.IsNullOrEmpty(accountName) ? "Konto" : accountName;
                this.stateLabel.Text = snapshot.State;
                this.heroLabel.Text = snapshot.Hero;
                this.detailLabel.Text = snapshot.Detail;
                this.seriesLabel.Text = snapshot.SeriesLine;
                this.lastLabel.Text = "Letzter Abschluss: " + (string.IsNullOrEmpty(lastRoundTrip) ? "-" : lastRoundTrip);

                Color emphasis = snapshot.Locked ? Amber : Ink;
                this.heroLabel.ForeColor = emphasis;
                this.stateLabel.ForeColor = snapshot.Pending && !snapshot.Locked ? Muted : emphasis;
            }

            private Label AddLabel(int top, int height, Color color)
            {
                Label label = new Label();
                label.Left = 12;
                label.Top = top;
                label.Width = this.ClientSize.Width - 24;
                label.Height = height;
                label.ForeColor = color;
                label.BackColor = Background;
                label.AutoEllipsis = true;
                this.Controls.Add(label);
                return label;
            }

            private void OnLocationChanged(object sender, EventArgs e)
            {
                long now = Environment.TickCount64;
                if (now - this.lastSaveTick < 400)
                    return;
                this.lastSaveTick = now;
                this.SaveLocation();
            }

            private void SaveLocation()
            {
                try
                {
                    if (this.WindowState != FormWindowState.Normal)
                        return;

                    string path = LocationPath();
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    string[] lines =
                    {
                        "x=" + this.Location.X.ToString(CultureInfo.InvariantCulture),
                        "y=" + this.Location.Y.ToString(CultureInfo.InvariantCulture)
                    };
                    File.WriteAllLines(path, lines);
                    this.lastSaveTick = Environment.TickCount64;
                }
                catch (Exception)
                {
                }
            }

            private static Point ResolveLocation(Size size)
            {
                Point fallback = DefaultLocation(size);
                try
                {
                    string path = LocationPath();
                    if (!File.Exists(path))
                        return fallback;

                    int? x = null;
                    int? y = null;
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        int split = raw.IndexOf('=');
                        if (split <= 0)
                            continue;
                        string key = raw.Substring(0, split);
                        string value = raw.Substring(split + 1);
                        int parsed;
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                            continue;
                        if (key == "x")
                            x = parsed;
                        else if (key == "y")
                            y = parsed;
                    }

                    if (!x.HasValue || !y.HasValue)
                        return fallback;

                    Rectangle bounds = new Rectangle(x.Value, y.Value, Math.Max(size.Width, 1), Math.Max(size.Height, 1));
                    foreach (Screen screen in Screen.AllScreens)
                    {
                        if (screen.WorkingArea.IntersectsWith(bounds))
                            return new Point(x.Value, y.Value);
                    }
                }
                catch (Exception)
                {
                    return fallback;
                }

                return fallback;
            }

            private static Point DefaultLocation(Size size)
            {
                Screen screen = Screen.PrimaryScreen;
                if (screen == null)
                    return new Point(100, 100);

                Rectangle area = screen.WorkingArea;
                int x = area.Right - size.Width - 16;
                int y = area.Top + 16;
                if (x < area.Left)
                    x = area.Left;
                if (y < area.Top)
                    y = area.Top;
                return new Point(x, y);
            }

            private static string LocationPath()
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LossLock");
                return Path.Combine(folder, "window.location");
            }
        }
    }
}
