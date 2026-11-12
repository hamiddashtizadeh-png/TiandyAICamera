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
                if (arg == "--test-image-optimizer" || arg == "--test-image")
                {
                    bool imgPassed = await Models.ImageOptimizationEngineTests.RunAllTestsAsync();
                    Environment.Exit(imgPassed ? 0 : 1);
                    return;
                }
                if (arg == "--test-optimizer")
                {
                    bool optPassed = await Models.CameraOptimizerTests.RunAllTestsAsync();
                    Environment.Exit(optPassed ? 0 : 1);
                    return;
                }
                if (arg == "--test" || arg == "--test-all")
                {
                    bool optPassed = await Models.CameraOptimizerTests.RunAllTestsAsync();
                    bool imgPassed = await Models.ImageOptimizationEngineTests.RunAllTestsAsync();
                    Environment.Exit(optPassed && imgPassed ? 0 : 1);
                    return;
                }
            }
        }
    }
}
