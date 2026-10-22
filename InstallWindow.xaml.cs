using System.Windows;
using ClipNest.Services;

namespace ClipNest;

/// <summary>The one question at a start from outside the program folder (<see cref="Installer"/>):
/// true = move ClipNest there, false (also when closed) = keep running from where it is.</summary>
public partial class InstallWindow : Window
{
    public InstallWindow(string folder)
    {
        InitializeComponent();
        Language = Loc.Xml;
        Folder.Text = folder;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
    }

    private void Install_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Keep_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
