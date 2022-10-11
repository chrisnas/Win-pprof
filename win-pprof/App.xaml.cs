using System.Windows;

namespace win_pprof
{
    public partial class App : Application
    {
        // remove the automatic binding to MainWindow in App.xaml
        //StartupUri="MainWindow.xaml"
        private void OnStartup(object sender, StartupEventArgs e)
        {
            var mainWnd = new MainWindow();

            // support double-clicking a .pprof file in Explorer
            if (e.Args.Length == 1)
            {
                mainWnd.OpenFile(e.Args[0]);
            }

            mainWnd.Show();
        }
    }
}
