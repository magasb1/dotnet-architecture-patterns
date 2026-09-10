using System.Windows;

namespace StorageDemo.Client;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Only the starting point; the toolbar switches servers without a restart.
        var address = e.Args.FirstOrDefault()
            ?? Environment.GetEnvironmentVariable("STORAGEDEMO_API")
            ?? "http://127.0.0.1:5080";

        new MainWindow(address).Show();
    }
}
