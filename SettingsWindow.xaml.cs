using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipNest.Services;

namespace ClipNest;

public partial class SettingsWindow : Window
{
    private int _scaleBefore = App.Settings.PopupScale;
    /// <summary>A size chip checked from code (on load, after a cancelled preview): that one shows no preview.</summary>
    private bool _settingChip;

    public SettingsWindow()
    {
        InitializeComponent();
        Language = Loc.Xml;
        App.Current.Popup.SizingEnded += OnSizingEnded;
        foreach (var chip in new[] { Size80, Size90, Size100, Size110, Size125 })
            chip.Checked += (_, _) => PreviewScale();
        Closed += (_, _) =>
        {
            App.Current.Popup.SizingEnded -= OnSizingEnded;
            // closed without «Готово»: drop the drag; without «Сохранить»: drop a previewed scale
            if (App.Current.Popup.IsSizing) App.Current.Popup.EndSizing(false);
            if (!_saved) App.Settings.PopupScale = _scaleBefore;
        };
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        Load();
    }

    private void Load()
    {
        var s = App.Settings;
        ReplaceWinV.IsChecked = s.ReplaceWinV;
        Autostart.IsChecked = s.Autostart;
        MoveUsedToTop.IsChecked = s.MoveUsedToTop;
        (s.Theme switch { ThemeMode.Dark => ThemeDark, ThemeMode.Light => ThemeLight, _ => ThemeSystem }).IsChecked = true;
        (s.Language switch { UiLanguage.Russian => LangRu, UiLanguage.Chinese => LangZh, _ => LangEn }).IsChecked = true;
        SelectScaleChip(s.PopupScale);
        MaxItems.Text = s.MaxItems.ToString();
        MaxAgeDays.Text = s.MaxAgeDays.ToString();
        SaveImages.IsChecked = s.SaveImages;
        MaxImageMb.Text = s.MaxImageMb.ToString();
        RespectPrivacy.IsChecked = s.RespectPrivacyFlags;
        HideSecrets.IsChecked = s.HideSecrets;
        ScanImages.IsChecked = s.ScanImages;
        ClearSecrets.IsChecked = s.ClearSecretSeconds > 0;
        if (!ImageScanner.Available)
        {
            ScanImages.IsEnabled = false;
#if OCR
            ScanImagesHint.Text = Loc.T("ScanNoOcrLanguage");
#else
            ScanImagesHint.Text = Loc.T("ScanLiteEdition");
#endif
        }
        Excluded.Text = string.Join(Environment.NewLine, s.ExcludedApps);
        DataPath.Text = App.Storage.DataDir;
        DataPath.ToolTip = App.Storage.DataDir;
        ResetFolder.Visibility = DataLocation.SamePath(App.Storage.DataDir, DataLocation.DefaultDir) ? Visibility.Collapsed : Visibility.Visible;
        if (DataLocation.IsPortable)
        {
            ChangeFolder.IsEnabled = ResetFolder.IsEnabled = false;
            DataHint.Text = Loc.T("DataPortable");
        }
        if (!App.Hook.IsInstalled) ReplaceWinV.IsEnabled = false;
        UpdateStats();
    }

    private void UpdateStats() =>
        Stats.Text = Loc.F("Stats", Loc.Count(App.Storage.Count(), "Items"), Loc.Bytes(App.Storage.DiskUsage()));

    private static int ParseInt(string s) => int.TryParse(s.Trim(), out var v) && v > 0 ? v : 0;

    private bool _saved;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.Popup.IsSizing) App.Current.Popup.EndSizing(true); // «Сохранить» keeps a drag in progress too
        ApplyForm();
        _saved = true;
        Close();
    }

    private int ChosenScale() =>
        Size80.IsChecked == true ? 80 : Size90.IsChecked == true ? 90 : Size110.IsChecked == true ? 110
            : Size125.IsChecked == true ? 125 : 100;

    private void SelectScaleChip(int scale)
    {
        _settingChip = true;
        (scale switch { <= 85 => Size80, <= 95 => Size90, <= 105 => Size100, <= 117 => Size110, _ => Size125 }).IsChecked = true;
        _settingChip = false;
    }

    /// <summary>A size chip shows the clipboard window in that size at once, the same preview as sizing by mouse
    /// (it can be dragged further there). «Готово», Enter or «Сохранить» keep it; Esc or closing this window drop it.</summary>
    private void PreviewScale()
    {
        if (!IsLoaded || _settingChip) return;
        App.Settings.PopupScale = ChosenScale();
        if (App.Current.Popup.IsSizing) App.Current.Popup.RefreshSize();
        else StartSizing();
    }

    private void SizeByMouse_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.Popup.IsSizing) App.Current.Popup.EndSizing(true); // «Готово»
        else StartSizing();
    }

    private void StartSizing()
    {
        App.Settings.PopupScale = ChosenScale(); // size what is picked above
        App.Current.Popup.BeginSizing(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        SizeByMouse.Content = Loc.T("SizeDone");
        SizeHint.Text = Loc.T("SizeHintSizing");
    }

    private void OnSizingEnded(bool kept)
    {
        // «Готово» has saved the scale shown while sizing along with the size; a cancel drops the preview,
        // and the chip it came from goes back too
        if (kept) _scaleBefore = App.Settings.PopupScale;
        else
        {
            App.Settings.PopupScale = _scaleBefore;
            SelectScaleChip(_scaleBefore);
        }
        SizeByMouse.SetResourceReference(ContentControl.ContentProperty, "s.SizeByMouse");
        SizeHint.Text = kept ? Loc.F("SizeSaved", App.Settings.PopupWidth, App.Settings.PopupHeight) : Loc.T("SizeCancelled");
        Activate();
    }

    private void ResetSize_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.Popup.IsSizing) App.Current.Popup.EndSizing(false);
        App.Settings.PopupWidth = 420;
        App.Settings.PopupHeight = 600;
        App.Settings.Save();
        SizeHint.Text = Loc.T("SizeWasReset");
    }

    private void ApplyForm()
    {
        var s = App.Settings;
        s.ReplaceWinV = ReplaceWinV.IsChecked == true;
        s.Autostart = Autostart.IsChecked == true;
        s.MoveUsedToTop = MoveUsedToTop.IsChecked == true;
        s.Theme = ThemeDark.IsChecked == true ? ThemeMode.Dark : ThemeLight.IsChecked == true ? ThemeMode.Light : ThemeMode.System;
        s.Language = LangRu.IsChecked == true ? UiLanguage.Russian : LangZh.IsChecked == true ? UiLanguage.Chinese : UiLanguage.English;
        s.PopupScale = ChosenScale();
        s.MaxItems = ParseInt(MaxItems.Text);
        s.MaxAgeDays = ParseInt(MaxAgeDays.Text);
        s.SaveImages = SaveImages.IsChecked == true;
        s.MaxImageMb = ParseInt(MaxImageMb.Text);
        s.RespectPrivacyFlags = RespectPrivacy.IsChecked == true;
        s.HideSecrets = HideSecrets.IsChecked == true;
        if (ScanImages.IsEnabled) s.ScanImages = ScanImages.IsChecked == true;
        s.ClearSecretSeconds = ClearSecrets.IsChecked == true ? (s.ClearSecretSeconds > 0 ? s.ClearSecretSeconds : 60) : 0;
        s.ExcludedApps = Excluded.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        App.Current.ApplySettings();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", $"\"{App.Storage.DataDir}\""); } catch { }
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("FolderDialogTitle"),
            InitialDirectory = App.Storage.DataDir,
        };
        if (dlg.ShowDialog(this) != true) return;
        MoveTo(DataLocation.TargetFor(dlg.FolderName));
    }

    private void ResetFolder_Click(object sender, RoutedEventArgs e) => MoveTo(DataLocation.DefaultDir);

    private void MoveTo(string dir)
    {
        if (DataLocation.SamePath(dir, App.Storage.DataDir)) return;
        var text = DataLocation.HasHistory(dir)
            ? Loc.F("FolderSwitch", dir, App.Storage.DataDir)
            : Loc.F("FolderMove", dir);
        if (MessageBox.Show(this, text, "ClipNest", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK)
            != MessageBoxResult.OK) return;
        ApplyForm(); // keep what was changed in this window: settings.json moves along
        App.Current.RelocateData(dir);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, Loc.T("ClearConfirm"),
            "ClipNest", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        App.Storage.ClearUnpinned();
        UpdateStats();
    }
}
