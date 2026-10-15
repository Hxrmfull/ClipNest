using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using ClipNest.Services;

namespace ClipNest;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
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
        MaxItems.Text = s.MaxItems.ToString();
        MaxAgeDays.Text = s.MaxAgeDays.ToString();
        SaveImages.IsChecked = s.SaveImages;
        MaxImageMb.Text = s.MaxImageMb.ToString();
        RespectPrivacy.IsChecked = s.RespectPrivacyFlags;
        HideSecrets.IsChecked = s.HideSecrets;
        Excluded.Text = string.Join(Environment.NewLine, s.ExcludedApps);
        DataPath.Text = App.Storage.DataDir;
        DataPath.ToolTip = App.Storage.DataDir;
        if (!App.Hook.IsInstalled) ReplaceWinV.IsEnabled = false;
        UpdateStats();
    }

    private void UpdateStats() =>
        Stats.Text = $"{Ru.Plural(App.Storage.Count(), "запись", "записи", "записей")}  ·  {Ru.Bytes(App.Storage.DiskUsage())} на диске";

    private static int ParseInt(string s) => int.TryParse(s.Trim(), out var v) && v > 0 ? v : 0;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.ReplaceWinV = ReplaceWinV.IsChecked == true;
        s.Autostart = Autostart.IsChecked == true;
        s.MoveUsedToTop = MoveUsedToTop.IsChecked == true;
        s.Theme = ThemeDark.IsChecked == true ? ThemeMode.Dark : ThemeLight.IsChecked == true ? ThemeMode.Light : ThemeMode.System;
        s.MaxItems = ParseInt(MaxItems.Text);
        s.MaxAgeDays = ParseInt(MaxAgeDays.Text);
        s.SaveImages = SaveImages.IsChecked == true;
        s.MaxImageMb = ParseInt(MaxImageMb.Text);
        s.RespectPrivacyFlags = RespectPrivacy.IsChecked == true;
        s.HideSecrets = HideSecrets.IsChecked == true;
        s.ExcludedApps = Excluded.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        App.Current.ApplySettings();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", $"\"{App.Storage.DataDir}\""); } catch { }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "Удалить всю историю, кроме закреплённых записей? Это нельзя отменить.",
            "ClipNest", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        App.Storage.ClearUnpinned();
        UpdateStats();
    }
}
