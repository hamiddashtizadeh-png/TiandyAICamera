using System;
using System.Windows;

namespace MehrganSurveillance
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args.Length > 0)
            {
                string arg = e.Args[0].ToLowerInvariant();
                if (arg == "--test-optimizer" || arg == "--test" || arg == "--test-all")
                {
                    bool optPassed = await Models.CameraOptimizerTests.RunAllTestsAsync();
                    Environment.Exit(optPassed ? 0 : 1);
                    return;
                }
            }
        }
    }
}
