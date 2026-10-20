using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Windows.Media;

namespace LivePixListener
{
    internal static class Program
    {
        private const string Version = "1.0.0";

        [STAThread]
        private static void Main(string[] args)
        {
            Net.Configure();
            Logger.CleanTemp();

            if (HasFlag(args, "--probe"))
            {
                AllocConsole();
                Console.OutputEncoding = Encoding.UTF8;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ProbeHost(ArgAfterFlag(args, "--probe")));
                return;
            }

            if (!SingleInstance.Acquire())
                return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                Logger.Write("ui " + e.Exception);
                try { MessageBox.Show(e.Exception.Message, "LivePix Listener"); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Logger.Write("fatal " + e.ExceptionObject);
            };

            Application.Run(new AppHost(Version));
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string ArgAfterFlag(string[] args, string flag)
        {
            if (args == null) return "";
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return "";
        }

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();
    }

    internal static class SingleInstance
    {
        private static Mutex _mutex;
        private static EventWaitHandle _show;
        private static Control _ui;
        private static Action _onShow;

        public static bool Acquire()
        {
            bool created;
            _mutex = new Mutex(true, @"Local\LivePixListener", out created);
            if (!created)
            {
                try
                {
                    using (var existing = EventWaitHandle.OpenExisting(@"Local\LivePixListener.Show"))
                        existing.Set();
                }
                catch { }
                return false;
            }

            _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LivePixListener.Show");
            var thread = new Thread(WaitForShow);
            thread.IsBackground = true;
            thread.Start();
            return true;
        }

        public static void Bind(Control ui, Action onShow)
        {
            _ui = ui;
            _onShow = onShow;
        }

        private static void WaitForShow()
        {
            while (_show.WaitOne())
            {
                var ui = _ui;
                var action = _onShow;
                if (ui == null || action == null) continue;
                try { ui.BeginInvoke(action); } catch { }
            }
        }
    }

    internal sealed class AppHost : ApplicationContext
    {
        private readonly string _version;
        private readonly SettingsForm _form;
        private readonly NotifyIcon _tray;
        private readonly AlertPlayer _player;
        private readonly LivePixConnection _connection;
        private AppConfig _config;

        public AppHost(string version)
        {
            _version = version;
            _config = ConfigStore.Load();
            _form = new SettingsForm(this);
            _player = new AlertPlayer(_form);
            _connection = new LivePixConnection();
            _connection.StatusChanged += OnStatus;
            _connection.AlertReceived += OnAlert;
            _connection.Skipped += OnSkip;
            _connection.Debug += Logger.Write;

            _tray = new NotifyIcon();
            _tray.Icon = AppIcon.Create();
            _form.Icon = (Icon)_tray.Icon.Clone();
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowSettings(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Configurações", null, delegate { ShowSettings(); });
            menu.Items.Add("Testar som", null, delegate { _form.TestFromTray(); });
            menu.Items.Add("Sobre", null, delegate { ShowAbout(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, delegate { Quit(); });
            _tray.ContextMenuStrip = menu;

            SingleInstance.Bind(_form, ShowSettings);
            _form.LoadFrom(_config);
            SetTip("Iniciando");

            if (string.IsNullOrWhiteSpace(_config.WidgetUrl))
            {
                OnStatus("Cole o link do widget e salve.");
                ShowSettings();
            }
            else
            {
                _connection.Start(_config.WidgetUrl);
            }
        }

        public int Volume { get { return _config.Volume; } }

        public void ShowSettings()
        {
            _form.ShowFromTray();
        }

        public void Apply(string url, int volume, bool startWithWindows)
        {
            url = (url ?? "").Trim();
            if (volume < 0) volume = 0;
            if (volume > 100) volume = 100;

            var previous = _config.WidgetUrl ?? "";
            _config.WidgetUrl = url;
            _config.Volume = volume;
            _config.StartWithWindows = startWithWindows;
            ConfigStore.Save(_config);

            try
            {
                StartupShortcut.Apply(startWithWindows);
            }
            catch (Exception ex)
            {
                Logger.Write("atalho " + ex.Message);
                OnStatus("Não consegui criar o atalho de inicialização.");
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                _connection.Stop();
                OnStatus("Sem link. Cole a URL do widget.");
                return;
            }

            if (!string.Equals(previous, url, StringComparison.Ordinal) || !_connection.IsRunning)
                _connection.Start(url);
        }

        public void TestSound(string url, int volume)
        {
            if (volume < 0) volume = 0;
            if (volume > 100) volume = 100;
            _config.Volume = volume;
            _connection.PlayTest(url, _player, MasterVolume);
        }

        public double MasterVolume()
        {
            var volume = _config.Volume;
            if (volume < 0) volume = 0;
            if (volume > 100) volume = 100;
            return volume / 100.0;
        }

        private void OnStatus(string text)
        {
            Logger.Write("status " + text);
            TryUi(delegate
            {
                _form.SetStatus(text);
                SetTip(text);
            });
        }

        private void OnAlert(Alert alert)
        {
            TryUi(delegate
            {
                _player.Enqueue(alert, MasterVolume());
                var title = string.IsNullOrWhiteSpace(alert.Title) ? "LivePix" : alert.Title.Trim();
                var body = string.IsNullOrWhiteSpace(alert.Detail) ? "Nova mensagem." : alert.Detail.Trim();
                if (body.Length > 180) body = body.Substring(0, 177) + "...";
                try { _tray.ShowBalloonTip(5000, title, body, ToolTipIcon.Info); } catch { }
            });
        }

        private void OnSkip()
        {
            TryUi(delegate { _player.Stop(); });
        }

        private void TryUi(Action action)
        {
            try
            {
                if (_form.IsHandleCreated && !_form.IsDisposed)
                    _form.BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        private void SetTip(string text)
        {
            var tip = "LivePix Listener - " + (text ?? "");
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            try { _tray.Text = tip; } catch { }
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                "LivePix Listener " + _version + Environment.NewLine + Environment.NewLine +
                "Toca o som e a voz dos alertas do LivePix em segundo plano." + Environment.NewLine +
                "Não é um programa oficial do LivePix." + Environment.NewLine + Environment.NewLine +
                "Fala apenas com os servidores do LivePix. Não envia os alertas para outro lugar.",
                "Sobre",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void Quit()
        {
            _connection.Stop();
            _player.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _form.AllowClose();
            _form.Close();
            ExitThread();
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly AppHost _host;
        private readonly TextBox _url;
        private readonly TrackBar _volume;
        private readonly Label _volumeValue;
        private readonly CheckBox _startup;
        private readonly Label _status;
        private bool _allowClose;

        public SettingsForm(AppHost host)
        {
            _host = host;
            Text = "LivePix Listener";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 430);
            BackColor = UiTheme.Background;
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = false;

            var title = LabelOf("LivePix Listener", 20, 16, 480, 32);
            title.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            title.ForeColor = UiTheme.Ink;

            var hint = LabelOf(
                "Cole o link do widget de notificação, o mesmo que está no OBS. O programa fica na bandeja e toca o som e a voz no PC do fone. Não abra esse link no navegador: o site entra na fila e pode engolir o alerta da live.",
                20, 56, 480, 72);
            hint.ForeColor = UiTheme.Muted;

            var urlLabel = LabelOf("Link do widget", 20, 136, 480, 22);
            _url = new TextBox();
            _url.SetBounds(20, 160, 480, 28);
            _url.Font = new Font("Segoe UI", 10f);
            _url.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

            var volumeLabel = LabelOf("Volume geral", 20, 200, 200, 22);
            _volume = new TrackBar();
            _volume.SetBounds(16, 222, 420, 45);
            _volume.Minimum = 0;
            _volume.Maximum = 100;
            _volume.TickFrequency = 10;
            _volume.Value = 100;
            _volume.Scroll += delegate { UpdateVolumeLabel(); };
            _volumeValue = LabelOf("100%", 440, 230, 60, 24);
            _volumeValue.TextAlign = ContentAlignment.MiddleLeft;

            _startup = new CheckBox();
            _startup.Text = "Iniciar com o Windows";
            _startup.SetBounds(20, 272, 480, 28);
            _startup.ForeColor = UiTheme.Ink;
            _startup.BackColor = UiTheme.Background;

            var save = ButtonOf("Salvar", 20, 318, true);
            save.Click += delegate { _host.Apply(_url.Text, _volume.Value, _startup.Checked); };
            var test = ButtonOf("Testar som", 176, 318, false);
            test.Click += delegate { TestFromTray(); };

            _status = LabelOf(" ", 20, 368, 480, 48);
            _status.ForeColor = UiTheme.Muted;

            Controls.Add(title);
            Controls.Add(hint);
            Controls.Add(urlLabel);
            Controls.Add(_url);
            Controls.Add(volumeLabel);
            Controls.Add(_volume);
            Controls.Add(_volumeValue);
            Controls.Add(_startup);
            Controls.Add(save);
            Controls.Add(test);
            Controls.Add(_status);
            AcceptButton = save;

            if (!IsHandleCreated)
                CreateHandle();
        }

        public void LoadFrom(AppConfig config)
        {
            _url.Text = config.WidgetUrl ?? "";
            var volume = config.Volume;
            if (volume < 0) volume = 0;
            if (volume > 100) volume = 100;
            _volume.Value = volume;
            _startup.Checked = config.StartWithWindows;
            UpdateVolumeLabel();
        }

        public void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        public void TestFromTray()
        {
            _host.TestSound(_url.Text, _volume.Value);
        }

        public void SetStatus(string text)
        {
            _status.Text = text ?? "";
            if (_status.Text.StartsWith("Ouvindo", StringComparison.Ordinal))
                _status.ForeColor = UiTheme.Green;
            else if (_status.Text.StartsWith("Reconect", StringComparison.Ordinal)
                || _status.Text.IndexOf("invál", StringComparison.OrdinalIgnoreCase) >= 0
                || _status.Text.IndexOf("Não", StringComparison.Ordinal) >= 0
                || _status.Text.IndexOf("Erro", StringComparison.Ordinal) >= 0)
                _status.ForeColor = UiTheme.Danger;
            else
                _status.ForeColor = UiTheme.Muted;
        }

        public void AllowClose()
        {
            _allowClose = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                _host.Apply(_url.Text, _volume.Value, _startup.Checked);
                ShowInTaskbar = false;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void UpdateVolumeLabel()
        {
            _volumeValue.Text = _volume.Value.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private Label LabelOf(string text, int x, int y, int width, int height)
        {
            var label = new Label();
            label.Text = text;
            label.SetBounds(x, y, width, height);
            label.ForeColor = UiTheme.Ink;
            label.BackColor = UiTheme.Background;
            return label;
        }

        private Button ButtonOf(string text, int x, int y, bool primary)
        {
            var button = new Button();
            button.Text = text;
            button.SetBounds(x, y, 144, 36);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = UiTheme.Green;
            button.BackColor = primary ? UiTheme.Green : System.Drawing.Color.White;
            button.ForeColor = primary ? System.Drawing.Color.White : UiTheme.Ink;
            button.Cursor = Cursors.Hand;
            return button;
        }
    }

    internal static class UiTheme
    {
        public static readonly System.Drawing.Color Background = System.Drawing.Color.FromArgb(246, 248, 246);
        public static readonly System.Drawing.Color Ink = System.Drawing.Color.FromArgb(22, 32, 28);
        public static readonly System.Drawing.Color Muted = System.Drawing.Color.FromArgb(90, 110, 100);
        public static readonly System.Drawing.Color Green = System.Drawing.Color.FromArgb(0, 158, 112);
        public static readonly System.Drawing.Color Danger = System.Drawing.Color.FromArgb(176, 64, 48);
    }

    internal static class AppIcon
    {
        private static Bitmap _bitmap;

        public static Icon Create()
        {
            _bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(_bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(System.Drawing.Color.Transparent);
                using (var brush = new SolidBrush(UiTheme.Green))
                    graphics.FillEllipse(brush, 1, 1, 30, 30);
                graphics.FillPolygon(System.Drawing.Brushes.White, new Point[]
                {
                    new Point(9, 14),
                    new Point(13, 14),
                    new Point(18, 10),
                    new Point(18, 22),
                    new Point(13, 18),
                    new Point(9, 18)
                });
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 1.8f))
                    graphics.DrawArc(pen, 19, 11, 7, 10, -50, 100);
            }
            return Icon.FromHandle(_bitmap.GetHicon());
        }
    }

    internal sealed class Alert
    {
        public string SoundUrl;
        public double SoundVolume;
        public double SoundSeconds;
        public double MinSeconds;
        public string SpeechUrl;
        public double SpeechVolume;
        public double MaxSeconds;
        public string Title;
        public string Detail;
    }

    internal sealed class AlertPlayer
    {
        private readonly Control _ui;
        private readonly Queue<Alert> _queue = new Queue<Alert>();
        private readonly MediaPlayer _sound = new MediaPlayer();
        private readonly MediaPlayer _speech = new MediaPlayer();
        private int _generation;
        private bool _draining;

        public AlertPlayer(Control ui)
        {
            _ui = ui;
        }

        public void Enqueue(Alert alert, double master)
        {
            if (_ui.InvokeRequired)
            {
                _ui.BeginInvoke((Action)delegate { Enqueue(alert, master); });
                return;
            }
            alert.SoundVolume = Clamp(alert.SoundVolume * master);
            alert.SpeechVolume = Clamp(alert.SpeechVolume * master);
            _queue.Enqueue(alert);
            Drain();
        }

        public void Stop()
        {
            if (_ui.InvokeRequired)
            {
                _ui.BeginInvoke((Action)delegate { Stop(); });
                return;
            }
            _generation++;
            _queue.Clear();
            Silence(_sound);
            Silence(_speech);
        }

        private async void Drain()
        {
            if (_draining) return;
            _draining = true;
            try
            {
                while (_queue.Count > 0)
                {
                    var alert = _queue.Dequeue();
                    var generation = _generation;
                    await Play(alert, generation);
                    if (generation != _generation) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Write("audio " + ex.Message);
            }
            finally
            {
                _draining = false;
                if (_queue.Count > 0 && _ui.IsHandleCreated)
                    _ui.BeginInvoke((Action)Drain);
            }
        }

        private async Task Play(Alert alert, int generation)
        {
            string soundPath = null;
            string speechPath = null;
            try
            {
                if (!string.IsNullOrEmpty(alert.SoundUrl))
                    soundPath = await DownloadPreferred(alert.SoundUrl);
                if (generation != _generation) return;
                if (!string.IsNullOrEmpty(alert.SpeechUrl))
                    speechPath = await DownloadPreferred(alert.SpeechUrl);
                if (generation != _generation) return;

                if (soundPath != null && speechPath != null)
                {
                    var soundTask = PlayLocal(_sound, soundPath, alert.SoundVolume, TimeSpan.FromSeconds(Math.Max(alert.SoundSeconds + 2, 4)), generation);
                    var waitSeconds = alert.SoundSeconds;
                    if (waitSeconds < 0.2) waitSeconds = 0.2;
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
                    if (generation != _generation)
                    {
                        try { await soundTask; } catch { }
                        return;
                    }
                    Silence(_sound);
                    await PlayLocal(_speech, speechPath, alert.SpeechVolume, TimeSpan.FromSeconds(Math.Max(alert.MaxSeconds, 3)), generation);
                    try { await soundTask; } catch { }
                }
                else if (soundPath != null)
                {
                    var cap = Math.Max(alert.SoundSeconds, alert.MinSeconds);
                    if (cap < 2) cap = 4;
                    await PlayLocal(_sound, soundPath, alert.SoundVolume, TimeSpan.FromSeconds(cap + 2), generation);
                }
                else if (speechPath != null)
                {
                    await PlayLocal(_speech, speechPath, alert.SpeechVolume, TimeSpan.FromSeconds(Math.Max(alert.MaxSeconds, 3)), generation);
                }
            }
            finally
            {
                TryDelete(soundPath);
                TryDelete(speechPath);
            }
        }

        private static async Task<string> DownloadPreferred(string url)
        {
            var mp3 = Urls.Mp3Sibling(url);
            if (mp3 != null)
            {
                var path = await Net.Download(mp3);
                if (path != null) return path;
            }
            return await Net.Download(url);
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { }
        }

        private Task PlayLocal(MediaPlayer player, string path, double volume, TimeSpan timeout, int generation)
        {
            var done = new TaskCompletionSource<bool>();
            EventHandler opened = null;
            EventHandler ended = null;
            EventHandler<ExceptionEventArgs> failed = null;

            opened = delegate
            {
                if (generation != _generation)
                {
                    done.TrySetResult(false);
                    return;
                }
                try
                {
                    player.Volume = Clamp(volume);
                    player.Play();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            };
            ended = delegate { done.TrySetResult(true); };
            failed = delegate(object sender, ExceptionEventArgs e)
            {
                var error = e.ErrorException;
                done.TrySetException(error ?? new InvalidOperationException("Falha ao tocar o áudio."));
            };

            player.MediaOpened += opened;
            player.MediaEnded += ended;
            player.MediaFailed += failed;
            try
            {
                player.Volume = Clamp(volume);
                player.Open(new Uri(path));
                player.Play();
            }
            catch (Exception ex)
            {
                player.MediaOpened -= opened;
                player.MediaEnded -= ended;
                player.MediaFailed -= failed;
                var failedOpen = new TaskCompletionSource<bool>();
                failedOpen.SetException(ex);
                return failedOpen.Task;
            }

            return Task.WhenAny(done.Task, Task.Delay(timeout)).ContinueWith(delegate
            {
                player.MediaOpened -= opened;
                player.MediaEnded -= ended;
                player.MediaFailed -= failed;
                Silence(player);
                if (done.Task.IsFaulted)
                    throw done.Task.Exception.GetBaseException();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static void Silence(MediaPlayer player)
        {
            try { player.Stop(); } catch { }
            try { player.Close(); } catch { }
        }

        private static double Clamp(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            if (double.IsNaN(value)) return 0;
            return value;
        }
    }

    /// <summary>
    /// Escuta o canal público do widget. Não reserva e não confirma alertas,
    /// para o OBS continuar dono da fila.
    /// </summary>
    internal sealed class LivePixConnection
    {
        public event Action<string> StatusChanged;
        public event Action<Alert> AlertReceived;
        public event Action Skipped;
        public event Action<string> Debug;

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly SemaphoreSlim _send = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cts;
        private ClientWebSocket _socket;
        private DateTime _lastReceiveUtc;
        private int _running;

        public LivePixConnection()
        {
            _json.MaxJsonLength = int.MaxValue;
        }

        public bool IsRunning { get { return _running == 1; } }

        public void Start(string widgetUrl)
        {
            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Interlocked.Exchange(ref _running, 1);
            Task.Run(delegate { return Run(widgetUrl, token); });
        }

        public void Stop()
        {
            Interlocked.Exchange(ref _running, 0);
            var cts = _cts;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
            var socket = _socket;
            if (socket != null)
            {
                try { socket.Abort(); } catch { }
            }
        }

        public async void PlayTest(string widgetUrl, AlertPlayer player, Func<double> master)
        {
            try
            {
                Report("Baixando som de teste...");
                var id = WidgetIds.Extract(widgetUrl);
                if (id == null)
                {
                    Report("Link inválido. Cole o link do widget de notificação.");
                    return;
                }
                var widget = await Net.GetJson(_json, "https://webservice.livepix.gg/widgets/" + id, CancellationToken.None);
                var config = JsonMap.Get(widget, "config") as Dictionary<string, object>;
                var sound = Urls.Resolve(JsonMap.Get(config, "audioUrl") as string);
                if (string.IsNullOrEmpty(sound))
                {
                    Report("Esse widget não tem som de alerta.");
                    return;
                }
                var alert = new Alert();
                alert.SoundUrl = sound;
                alert.SoundVolume = JsonMap.ToDouble(JsonMap.Get(config, "audioVolume"), 50) / 100.0;
                alert.SoundSeconds = Math.Max(1, JsonMap.ToDouble(JsonMap.Get(config, "audioDuration"), 1));
                alert.MinSeconds = alert.SoundSeconds;
                alert.MaxSeconds = 8;
                player.Enqueue(alert, master());
                Report(IsRunning ? "Ouvindo" : "Tocando o som deste widget.");
            }
            catch (InvalidWidgetException)
            {
                Report("Não achei esse widget. Confira o link.");
            }
            catch (Exception ex)
            {
                Logger.Write("teste " + ex.Message);
                Report("Não consegui baixar o som de teste.");
            }
        }

        private async Task Run(string widgetUrl, CancellationToken token)
        {
            var delayMs = 1000;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var retry = await ConnectOnce(widgetUrl, token);
                        if (!retry) return;
                        delayMs = 1000;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (InvalidWidgetException ex)
                    {
                        Report(ex.Message);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Note("falha " + ex.Message);
                        Report("Reconectando...");
                    }

                    try { await Task.Delay(delayMs, token); }
                    catch (OperationCanceledException) { return; }
                    if (delayMs < 15000) delayMs = Math.Min(delayMs * 2, 15000);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private async Task<bool> ConnectOnce(string widgetUrl, CancellationToken token)
        {
            var id = WidgetIds.Extract(widgetUrl);
            if (id == null)
            {
                Report("Link inválido. Cole o link do widget de notificação.");
                return false;
            }

            Report("Conectando...");
            var widget = await Net.GetJson(_json, "https://webservice.livepix.gg/widgets/" + id, token);
            var type = Convert.ToString(JsonMap.Get(widget, "type"));
            if (!string.Equals(type, "notification", StringComparison.OrdinalIgnoreCase))
                throw new InvalidWidgetException("Esse link é um widget de " + type + ". Use um widget de notificação.");

            var userId = Convert.ToString(JsonMap.Get(widget, "userId"));
            if (string.IsNullOrWhiteSpace(userId))
                throw new InvalidWidgetException("O LivePix não devolveu o dono desse widget.");

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            HeaderUtil.TrySet(socket, "Origin", "https://widget.livepix.gg");
            _socket = socket;

            try
            {
                await socket.ConnectAsync(new Uri("wss://pubsub.livepix.gg/ws"), token);
                var auth = await FetchToken(id, token);
                await Send(socket, "auth", auth, token);
                await Send(socket, "subscribe", "widget:" + id, token);
                await Send(socket, "subscribe", "user:widgets:notification:" + userId, token);
                _lastReceiveUtc = DateTime.UtcNow;
                Report("Ouvindo");
                Note("conectado");

                var refresh = RefreshLoop(socket, id, token);
                var watch = WatchLoop(socket, token);
                await ReceiveLoop(socket, token);
                Note("canal fechado (" + refresh.Status + ", " + watch.Status + ")");
                return true;
            }
            finally
            {
                try { socket.Abort(); } catch { }
                if (object.ReferenceEquals(_socket, socket)) _socket = null;
            }
        }

        private async Task<string> FetchToken(string id, CancellationToken token)
        {
            var payload = await Net.GetJson(_json, "https://webservice.livepix.gg/pubsub/widget/" + id, token);
            var auth = JsonMap.Get(payload, "token") as string;
            if (string.IsNullOrEmpty(auth))
                throw new InvalidOperationException("Token vazio.");
            return auth;
        }

        private async Task RefreshLoop(ClientWebSocket socket, string id, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), token);
                    var auth = await FetchToken(id, token);
                    await Send(socket, "auth", auth, token);
                    Note("token renovado");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Note("refresh " + ex.Message);
                try { socket.Abort(); } catch { }
            }
        }

        private async Task WatchLoop(ClientWebSocket socket, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await Task.Delay(5000, token);
                    if (DateTime.UtcNow - _lastReceiveUtc > TimeSpan.FromSeconds(75))
                    {
                        Note("sem resposta, reconectando");
                        try { socket.Abort(); } catch { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[16384];
            var stream = new MemoryStream();
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                stream.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    stream.Write(buffer, 0, result.Count);
                    if (stream.Length > 2 * 1024 * 1024)
                        return;
                }
                while (!result.EndOfMessage);

                _lastReceiveUtc = DateTime.UtcNow;
                var text = Encoding.UTF8.GetString(stream.ToArray());
                await Handle(socket, text, token);
            }
        }

        private async Task Handle(ClientWebSocket socket, string text, CancellationToken token)
        {
            Dictionary<string, object> packet;
            try
            {
                packet = _json.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch
            {
                return;
            }
            if (packet == null) return;

            var message = JsonMap.Get(packet, "message") as Dictionary<string, object>;
            if (message == null) return;

            var name = Convert.ToString(JsonMap.Get(message, "event"));
            var type = JsonMap.ToInt(JsonMap.Get(message, "type"));
            if (type == 2) Note("evento " + name);
            else if (!string.IsNullOrEmpty(name)) Note("proto " + name);

            if (name == "ping")
                await Send(socket, "pong", JsonMap.Get(message, "payload"), token);

            var id = JsonMap.Get(packet, "id") as string;
            if (!string.IsNullOrEmpty(id))
                await Send(socket, "confirmation", id, token);

            if (type == 2 && name == "notification:skip")
            {
                var skip = Skipped;
                if (skip != null) skip();
            }

            if (type == 2 && name == "notification:show")
            {
                var alert = ParseAlert(JsonMap.Get(message, "payload") as Dictionary<string, object>);
                if (alert != null)
                {
                    var received = AlertReceived;
                    if (received != null) received(alert);
                    Report("Ouvindo. Alerta às " + DateTime.Now.ToString("HH:mm"));
                }
            }
        }

        private static Alert ParseAlert(Dictionary<string, object> payload)
        {
            if (payload == null) return null;
            var data = JsonMap.Get(payload, "data") as Dictionary<string, object>;
            var config = data == null ? null : JsonMap.Get(data, "config") as Dictionary<string, object>;
            if (config == null) return null;

            var alert = new Alert();
            if (JsonMap.ToBool(JsonMap.Get(config, "audioEnabled"), true))
                alert.SoundUrl = Urls.Resolve(JsonMap.Get(config, "audioUrl") as string);
            if (JsonMap.ToBool(JsonMap.Get(config, "textToSpeechEnabled"), true))
                alert.SpeechUrl = Urls.Resolve(JsonMap.Get(config, "textToSpeechUrl") as string);

            alert.SoundVolume = JsonMap.ToDouble(JsonMap.Get(config, "audioVolume"), 50) / 100.0;
            alert.SpeechVolume = JsonMap.ToDouble(JsonMap.Get(config, "textToSpeechVolume"), 50) / 100.0;
            alert.SoundSeconds = JsonMap.ToDouble(JsonMap.Get(config, "audioDuration"), 1);
            alert.MinSeconds = JsonMap.ToDouble(JsonMap.Get(config, "minimumDuration"), 0);
            alert.MaxSeconds = JsonMap.ToDouble(JsonMap.Get(config, "maximumDuration"), 60);

            var inner = JsonMap.Get(data, "data") as Dictionary<string, object>;
            alert.Title = JsonMap.FirstString(inner, new string[] { "username", "userName", "name", "displayName", "author" });
            alert.Detail = JsonMap.FirstString(inner, new string[] { "message", "text", "content" });
            return alert;
        }

        private async Task Send(ClientWebSocket socket, string name, object payload, CancellationToken token)
        {
            var message = new Dictionary<string, object>();
            message["type"] = 1;
            message["event"] = name;
            message["payload"] = payload;
            message["time"] = Net.UnixNow();
            var root = new Dictionary<string, object>();
            root["message"] = message;
            var bytes = Encoding.UTF8.GetBytes(_json.Serialize(root));

            await _send.WaitAsync(token);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, token);
            }
            finally
            {
                _send.Release();
            }

            if (name != "pong" && name != "confirmation" && name != "auth")
                Note("enviei " + name);
        }

        private void Report(string text)
        {
            var handler = StatusChanged;
            if (handler != null) handler(text);
        }

        private void Note(string text)
        {
            var handler = Debug;
            if (handler != null) handler(text);
        }
    }

    internal sealed class ProbeHost : ApplicationContext
    {
        public ProbeHost(string url)
        {
            var form = new Form();
            form.ShowInTaskbar = false;
            form.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            form.Opacity = 0;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-3200, -3200);
            form.Load += async delegate
            {
                Console.WriteLine("Probe do LivePix Listener");
                var player = new AlertPlayer(form);
                var connection = new LivePixConnection();
                connection.Debug += delegate(string line) { Console.WriteLine(line); };
                connection.StatusChanged += delegate(string line) { Console.WriteLine("status: " + line); };
                connection.AlertReceived += delegate(Alert alert)
                {
                    Console.WriteLine("alerta recebido");
                    player.Enqueue(alert, 1);
                };
                try
                {
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        var config = ConfigStore.Load();
                        url = config.WidgetUrl;
                    }
                    connection.PlayTest(url, player, delegate { return 1.0; });
                    connection.Start(url);
                    await Task.Delay(12000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("erro: " + ex.Message);
                }
                connection.Stop();
                await Task.Delay(2500);
                form.Close();
                ExitThread();
            };
            form.Show();
        }
    }

    internal sealed class InvalidWidgetException : Exception
    {
        public InvalidWidgetException(string message) : base(message) { }
    }

    internal static class WidgetIds
    {
        private static readonly Regex Pattern = new Regex(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        public static string Extract(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = Pattern.Match(text.Trim());
            if (!match.Success) return null;
            return match.Value;
        }
    }

    internal static class Urls
    {
        public static string Resolve(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
            if (url.StartsWith("/", StringComparison.Ordinal)) return "https://widget.livepix.gg" + url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
            return "https://widget.livepix.gg/" + url;
        }

        public static string Mp3Sibling(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var query = url.IndexOf('?');
            var path = query >= 0 ? url.Substring(0, query) : url;
            if (!path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return null;
            var mp3 = path.Substring(0, path.Length - 4) + ".mp3";
            if (query >= 0) mp3 += url.Substring(query);
            return mp3;
        }
    }

    internal static class JsonMap
    {
        public static object Get(Dictionary<string, object> map, string key)
        {
            if (map == null || key == null) return null;
            object value;
            if (map.TryGetValue(key, out value)) return value;
            return null;
        }

        public static bool ToBool(object value, bool fallback)
        {
            if (value is bool) return (bool)value;
            return fallback;
        }

        public static int ToInt(object value)
        {
            if (value == null) return 0;
            if (value is int) return (int)value;
            if (value is long) return (int)(long)value;
            if (value is decimal) return (int)(decimal)value;
            int number;
            if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
            return 0;
        }

        public static double ToDouble(object value, double fallback)
        {
            if (value == null) return fallback;
            if (value is double) return (double)value;
            if (value is float) return (float)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is decimal) return (double)(decimal)value;
            double number;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
            return fallback;
        }

        public static string FirstString(Dictionary<string, object> map, string[] keys)
        {
            if (map == null || keys == null) return null;
            for (int i = 0; i < keys.Length; i++)
            {
                foreach (var pair in map)
                {
                    if (!string.Equals(pair.Key, keys[i], StringComparison.OrdinalIgnoreCase))
                        continue;
                    var text = pair.Value as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    text = text.Trim();
                    if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        continue;
                    return text;
                }
            }
            return null;
        }
    }

    internal static class Net
    {
        private static readonly HttpClient Http = Create();

        public static void Configure()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }
            catch { }
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288;
            }
            catch { }
        }

        public static long UnixNow()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalSeconds;
        }

        public static async Task<Dictionary<string, object>> GetJson(JavaScriptSerializer json, string url, CancellationToken token)
        {
            using (var response = await Http.GetAsync(url, token))
            {
                var body = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode == 404)
                    throw new InvalidWidgetException("Não achei esse widget. Confira o link.");
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("HTTP " + (int)response.StatusCode);
                var map = json.DeserializeObject(body) as Dictionary<string, object>;
                if (map == null)
                    throw new InvalidOperationException("Resposta inválida do LivePix.");
                return map;
            }
        }

        public static async Task<string> Download(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            using (var response = await Http.GetAsync(url))
            {
                if (!response.IsSuccessStatusCode) return null;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes == null || bytes.Length < 64) return null;
                var dir = Path.Combine(Path.GetTempPath(), "LivePixListener");
                Directory.CreateDirectory(dir);
                var ext = ExtensionOf(url);
                var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
                File.WriteAllBytes(path, bytes);
                return path;
            }
        }

        private static HttpClient Create()
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LivePixListener/1.0");
            return http;
        }

        private static string ExtensionOf(string url)
        {
            var query = url.IndexOf('?');
            var path = query >= 0 ? url.Substring(0, query) : url;
            var dot = path.LastIndexOf('.');
            if (dot < 0 || path.Length - dot > 5) return ".bin";
            return path.Substring(dot);
        }
    }

    internal static class HeaderUtil
    {
        public static void TrySet(ClientWebSocket socket, string name, string value)
        {
            try
            {
                socket.Options.SetRequestHeader(name, value);
                return;
            }
            catch (Exception ex)
            {
                Logger.Write("header " + name + ": " + ex.Message);
            }

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var field = socket.Options.GetType().GetField("_requestHeaders", flags);
                if (field == null)
                {
                    Logger.Write("sem campo de cabeçalho interno");
                    return;
                }
                var headers = field.GetValue(socket.Options) as WebHeaderCollection;
                if (headers == null) return;
                var add = typeof(WebHeaderCollection).GetMethod(
                    "AddWithoutValidate",
                    flags,
                    null,
                    new Type[] { typeof(string), typeof(string) },
                    null);
                if (add == null) return;
                add.Invoke(headers, new object[] { name, value });
            }
            catch (Exception ex)
            {
                Logger.Write("header reflexao " + ex.Message);
            }
        }
    }

    internal sealed class AppConfig
    {
        public string WidgetUrl { get; set; }
        public int Volume { get; set; }
        public bool StartWithWindows { get; set; }

        public AppConfig()
        {
            WidgetUrl = "";
            Volume = 100;
            StartWithWindows = false;
        }
    }

    internal static class ConfigStore
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static string Dir()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LivePixListener");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static AppConfig Load()
        {
            try
            {
                var path = Path.Combine(Dir(), "config.json");
                if (!File.Exists(path)) return new AppConfig();
                var config = Json.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8));
                if (config == null) return new AppConfig();
                if (config.WidgetUrl == null) config.WidgetUrl = "";
                if (config.Volume < 0 || config.Volume > 100) config.Volume = 100;
                return config;
            }
            catch (Exception ex)
            {
                Logger.Write("config " + ex.Message);
                return new AppConfig();
            }
        }

        public static void Save(AppConfig config)
        {
            var path = Path.Combine(Dir(), "config.json");
            File.WriteAllText(path, Json.Serialize(config), Encoding.UTF8);
        }
    }

    internal static class StartupShortcut
    {
        public static void Apply(bool enabled)
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var link = Path.Combine(startup, "LivePix Listener.lnk");
            if (!enabled)
            {
                if (File.Exists(link)) File.Delete(link);
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Atalho indisponível.");
            var shell = Activator.CreateInstance(shellType);
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
            var type = shortcut.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppDomain.CurrentDomain.BaseDirectory });
            type.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, shortcut, new object[] { 7 });
            type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "LivePix Listener" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
    }

    internal static class Logger
    {
        private static readonly object Gate = new object();

        public static void Write(string line)
        {
            try
            {
                var path = Path.Combine(ConfigStore.Dir(), "log.txt");
                var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine;
                lock (Gate)
                {
                    File.AppendAllText(path, text, Encoding.UTF8);
                    var info = new FileInfo(path);
                    if (info.Length > 200000)
                    {
                        var all = File.ReadAllText(path, Encoding.UTF8);
                        if (all.Length > 100000)
                            all = all.Substring(all.Length - 100000);
                        File.WriteAllText(path, all, Encoding.UTF8);
                    }
                }
            }
            catch { }
        }

        public static void CleanTemp()
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "LivePixListener");
                if (!Directory.Exists(dir)) return;
                var files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        var info = new FileInfo(files[i]);
                        if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(6))
                            info.Delete();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
