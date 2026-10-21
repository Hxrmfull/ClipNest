using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using ClipNest.Services;
using Forms = System.Windows.Forms;

namespace ClipNest;

public partial class App : Application
{
    // One running copy per data folder: the names carry a hash of the folder, so a second copy with its own data
    // (a portable or test copy) neither blocks nor commands the main one. Versions up to 1.x used the bare names.
    private const string LegacyMutexName = "ClipNest.SingleInstance.v1";
    private const string LegacyShowEventName = "ClipNest.ShowPopup.v1";
    private const string LegacyExitEventName = "ClipNest.Exit.v1";
    private static string _scope = "";
    private static string MutexName => "ClipNest.SingleInstance.v2." + _scope;
    private static string ShowEventName => "ClipNest.ShowPopup.v2." + _scope;
    private static string ExitEventName => "ClipNest.Exit.v2." + _scope;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _openItem, _pauseItem, _settingsItem, _exitItem;
    private SettingsWindow? _settingsWindow;
    private System.Windows.Threading.DispatcherTimer? _pruneTimer;

    public static AppSettings Settings { get; private set; } = null!;
    public static Storage Storage { get; private set; } = null!;
    public static ClipboardMonitor Monitor { get; private set; } = null!;
    public static KeyboardHook Hook { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    private MainWindow _popup = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _scope = DataLocation.InstanceScope();
        bool portable = DataLocation.IsPortable;

        if (e.Args.Contains("--exit"))
        {
            try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch { }
            if (!portable) { try { EventWaitHandle.OpenExisting(LegacyExitEventName).Set(); } catch { } }
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // another copy is running on this data: ask it to show the history and quit
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        // an older version (bare names) on the same default data: don't run a second copy next to it
        if (!portable && Mutex.TryOpenExisting(LegacyMutexName, out var legacy))
        {
            legacy.Dispose();
            try { EventWaitHandle.OpenExisting(LegacyShowEventName).Set(); } catch { }
            _mutex.ReleaseMutex();
            Shutdown();
            return;
        }

        var dataDir = DataLocation.Resolve();
        Directory.CreateDirectory(dataDir);
        Log.FilePath = Path.Combine(dataDir, "clipnest.log");
        DataLocation.RestrictToCurrentUser(dataDir);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("unhandled: " + ex.Exception);
            ex.Handled = true;
        };

        Settings = AppSettings.Load(dataDir);
        if (Settings.Language == UiLanguage.Unset)
        {
            // the first start (ClipNest has no installer): take the language Windows speaks, and keep it from now on
            Settings.Language = Loc.FromWindows();
            Settings.Save();
        }
        Loc.Apply(Settings.Language);
        Theme.Apply(Settings.Theme);

        Secrets.Init(dataDir);
        Storage = new Storage(dataDir);
        Storage.Prune(Settings.MaxItems, Settings.MaxAgeDays);

        Monitor = new ClipboardMonitor(Storage, Settings);
        Hook = new KeyboardHook(Dispatcher) { Enabled = Settings.ReplaceWinV };
        Hook.WinVPressed += ShowPopup;

        _popup = new MainWindow();
        _popup.Warmup();
        Monitor.ItemCaptured += _popup.OnItemCaptured;
        SchedulePrune();

        Autostart.Set(Settings.Autostart);
        CreateTray();
        ListenForSecondInstance();

        // locking the PC (Win+L) closes the popup: it must not keep taking keys after unlocking
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, ev) =>
        {
            if (ev.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
                Dispatcher.BeginInvoke(_popup.HidePopup);
        };
        // after a sleep the timer may be late: what expired meanwhile goes at once
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, ev) =>
        {
            if (ev.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.BeginInvoke(PruneNow);
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (Settings.Theme == ThemeMode.System) Dispatcher.BeginInvoke(() => Theme.Apply(ThemeMode.System));
        };

        var prof = Array.IndexOf(e.Args, "--profile");
        if (prof >= 0 && prof + 1 < e.Args.Length)
        {
            Snapshots.Profile(_popup, e.Args[prof + 1]);
            return;
        }

        var hk = Array.IndexOf(e.Args, "--hooktest");
        if (hk >= 0 && hk + 1 < e.Args.Length)
        {
            // dev check of the hook thread: record each intercepted Win+V instead of opening the popup
            var marker = e.Args[hk + 1];
            Hook.WinVPressed -= ShowPopup;
            Hook.WinVPressed += () => File.AppendAllText(marker, $"winv {Environment.TickCount64}{Environment.NewLine}");
            Hook.Enabled = true;
            File.WriteAllText(marker, $"ready installed={Hook.IsInstalled}{Environment.NewLine}");
            return;
        }

        var scr = Array.IndexOf(e.Args, "--screencheck");
        if (scr >= 0 && scr + 1 < e.Args.Length)
        {
            Snapshots.ScreenCheck(_popup, e.Args[scr + 1]);
            return;
        }

        var lc = Array.IndexOf(e.Args, "--layoutcheck");
        if (lc >= 0 && lc + 1 < e.Args.Length)
        {
            Snapshots.LayoutCheck(_popup, e.Args[lc + 1]);
            return;
        }

        var snap = Array.IndexOf(e.Args, "--snapshot");
        if (snap >= 0 && snap + 1 < e.Args.Length)
        {
            Snapshots.Run(_popup, e.Args[snap + 1]);
            return;
        }

        if (!Settings.ShownWelcome)
        {
            Settings.ShownWelcome = true;
            Settings.Save();
            _tray!.ShowBalloonTip(5000, Loc.T("WelcomeTitle"), Loc.T(Settings.ReplaceWinV ? "WelcomeWinV" : "WelcomeTray"),
                Forms.ToolTipIcon.None);
        }
        else if (e.Args.Contains("--settings"))
        {
            OpenSettings();
        }
        else if (!e.Args.Contains("--tray"))
        {
            ShowPopup();
        }
    }

    private void ListenForSecondInstance()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var t = new Thread(() =>
        {
            while (true)
            {
                if (WaitHandle.WaitAny(new WaitHandle[] { _showEvent, exitEvent }) == 1)
                {
                    Dispatcher.BeginInvoke(Quit);
                    return;
                }
                Dispatcher.BeginInvoke(ShowPopup);
            }
        }) { IsBackground = true, Name = "ClipNest.ShowListener" };
        t.Start();
    }

    private void CreateTray()
    {
        System.Drawing.Icon icon;
        var res = GetResourceStream(new Uri("pack://application:,,,/Assets/clipnest.ico"));
        using (var s = res.Stream) icon = new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);

        var menu = new Forms.ContextMenuStrip();
        _openItem = new Forms.ToolStripMenuItem("", null, (_, _) => ShowPopup());
        _pauseItem = new Forms.ToolStripMenuItem("", null, (_, _) => TogglePause());
        _settingsItem = new Forms.ToolStripMenuItem("", null, (_, _) => OpenSettings());
        _exitItem = new Forms.ToolStripMenuItem("", null, (_, _) => Quit());
        menu.Items.AddRange(new Forms.ToolStripItem[] { _openItem, _pauseItem, _settingsItem, new Forms.ToolStripSeparator(), _exitItem });

        _tray = new Forms.NotifyIcon
        {
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == Forms.MouseButtons.Left) ShowPopup();
        };
        UpdateTrayTexts();
        Loc.Changed += UpdateTrayTexts;
    }

    private void UpdateTrayTexts()
    {
        if (_tray is null) return;
        _openItem!.Text = Loc.T("TrayOpen");
        _pauseItem!.Text = Loc.T("TrayPause");
        _settingsItem!.Text = Loc.T("TraySettings");
        _exitItem!.Text = Loc.T("TrayExit");
        _tray.Text = Loc.T(Monitor.Paused ? "TrayTipPaused" : "TrayTip");
    }

    public void TogglePause()
    {
        Monitor.Paused = !Monitor.Paused;
        if (_pauseItem is not null) _pauseItem.Checked = Monitor.Paused;
        UpdateTrayTexts();
        _popup.UpdatePausedState();
    }

    public void ShowPopup() => _popup.ShowPopup();

    public MainWindow Popup => _popup;

    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Keeps «Сколько дней хранить» while ClipNest runs, not only at its start: the timer wakes when the oldest
    /// item expires (<see cref="Storage.NextPruneIn"/>). Off when the history is kept forever, so then an idle ClipNest
    /// never wakes up; a check that finds nothing to delete writes nothing to the disk either.</summary>
    private void SchedulePrune()
    {
        if (_pruneTimer is null)
        {
            _pruneTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher);
            _pruneTimer.Tick += (_, _) => PruneNow();
        }
        _pruneTimer.Stop();
        if (Settings.MaxAgeDays <= 0) return;
        _pruneTimer.Interval = Storage.NextPruneIn(Storage.OldestUnpinned(), Settings.MaxAgeDays, DateTime.Now);
        _pruneTimer.Start();
    }

    private void PruneNow()
    {
        if (_pruneTimer is null) return; // shutting down
        // not under an open list: its cards would point at deleted items (it goes a few seconds later)
        if (!_popup.IsVisible) Storage.Prune(Settings.MaxItems, Settings.MaxAgeDays);
        SchedulePrune();
    }

    private void StopPruning()
    {
        _pruneTimer?.Stop();
        _pruneTimer = null;
    }

    /// <summary>Re-applies settings after the settings window saves them.</summary>
    public void ApplySettings()
    {
        Settings.Save();
        Hook.Enabled = Settings.ReplaceWinV;
        Autostart.Set(Settings.Autostart);
        Theme.Apply(Settings.Theme);
        Loc.Apply(Settings.Language);
        Storage.Prune(Settings.MaxItems, Settings.MaxAgeDays);
        SchedulePrune();
    }

    /// <summary>Switches to another data folder: closes the database, moves the data there
    /// (unless the folder already has a ClipNest history, which is then used as is) and restarts.</summary>
    public void RelocateData(string newDir)
    {
        var oldDir = Storage.DataDir;
        bool move = !DataLocation.HasHistory(newDir);
        Settings.Save();
        _popup.HidePopup();
        StopPruning();
        Monitor.ItemCaptured -= _popup.OnItemCaptured;
        Hook.Dispose();
        Monitor.Dispose();
        Storage.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // pooled connections would keep history.db open
        var log = Log.FilePath;
        Log.FilePath = null;

        try
        {
            if (move) DataLocation.Move(oldDir, newDir);
            DataLocation.CustomDir = newDir;
        }
        catch (Exception ex)
        {
            Log.FilePath = log;
            Log.Write("relocate: " + ex);
            MessageBox.Show(Loc.F("MoveFailed", newDir, ex.Message, oldDir),
                "ClipNest", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Restart("--settings");
    }

    private void Restart(string args)
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        // the new copy must not find our single-instance mutex
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
        try { if (Environment.ProcessPath is { } exe) Process.Start(exe, args); }
        catch (Exception ex) { Log.Write("restart: " + ex.Message); }
        Shutdown();
    }

    public void Quit()
    {
        StopPruning();
        ClipNest.MainWindow.CleanViewCopies();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Hook?.Dispose();
        Monitor?.Dispose();
        Storage?.Dispose();
        Shutdown();
    }
}
