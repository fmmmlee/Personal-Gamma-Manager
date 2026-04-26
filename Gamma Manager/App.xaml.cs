using System;
using System.Linq;
using System.Windows;

namespace Gamma_Manager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool startMinimized = e.Args.Any(
                a => string.Equals(a, "/startup", StringComparison.OrdinalIgnoreCase));

            var window = new MainWindow(startMinimized);
            MainWindow = window;
            if (!startMinimized) window.Show();
        }
    }
}
