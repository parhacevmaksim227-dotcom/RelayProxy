using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RelayProxy.Desktop;

// Process picker shown as a sheet inside the main window instead of a separate window.
public partial class RunningAppsPanel : UserControl
{
    public sealed record RunningApp(string Path, ImageSource Icon) { public string Name => System.IO.Path.GetFileNameWithoutExtension(Path); }
    // Raised with the chosen paths, or null when the sheet was dismissed.
    public event Action<string[]?>? Finished;
    public RunningAppsPanel() { InitializeComponent(); }

    public async Task LoadAsync()
    {
        Search.Clear(); ProcessList.ItemsSource = null;
        Empty.Text = "Загружаем приложения…"; Empty.Visibility = Visibility.Visible; Count.Text = "Ctrl / Shift — выбрать несколько"; AddButton.IsEnabled = false;
        var apps = await Task.Run(() =>
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { if (process.MainModule?.FileName is string path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) paths.Add(path); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
            return paths.Where(p => !p.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && !p.Equals(Core.EngineRunner.EnginePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Select(p => new RunningApp(p, ApplicationIcon.Get(p))).ToArray();
        });
        if (!IsLoaded) return;
        ProcessList.ItemsSource = apps; ApplyFilter(); GlassMotion.Present(ProcessList); Search.Focus();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        if (ProcessList?.ItemsSource == null) return;
        var view = CollectionViewSource.GetDefaultView(ProcessList.ItemsSource);
        var query = Search.Text.Trim();
        view.Filter = item => item is RunningApp app && app.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
        Empty.Text = "Ничего не найдено"; Empty.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AddButton == null) return;
        AddButton.IsEnabled = ProcessList.SelectedItems.Count > 0;
        Count.Text = ProcessList.SelectedItems.Count == 0 ? "Ctrl / Shift — выбрать несколько" : $"Выбрано: {ProcessList.SelectedItems.Count}";
    }
    private void Add_Click(object sender, RoutedEventArgs e) => Finished?.Invoke(ProcessList.SelectedItems.Cast<RunningApp>().Select(a => a.Path).ToArray());
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finished?.Invoke(null);
    public void Cancel() => Finished?.Invoke(null);
    public void ReleaseItems() { ProcessList.ItemsSource = null; }
}
