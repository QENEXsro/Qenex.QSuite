using System.Diagnostics;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.SplashScreen;

namespace Qenex.QInsight.Views;

/// <summary>
/// Splash shown while a project is opened (workspaces, controls and grids are built on the UI
/// thread; a 16x16 map alone costs a few hundred ms on the first show). RadSplashScreenManager
/// runs the splash on its own thread, so it keeps animating while the main thread works. The
/// splash stays at least <see cref="MinimumDisplayMs"/> so a small project does not make it
/// flash (Radek 2026-09-27).
/// </summary>
public sealed class ProjectOpenSplash
{
    public const int MinimumDisplayMs = 800;

    private readonly Stopwatch shownFor = new();
    private SplashScreenDataContext? context;

    public void Show(string projectName)
    {
        context = new SplashScreenDataContext
        {
            ImagePath = "pack://application:,,,/Qenex.QInsight;component/Icons/QInsight.png",
            ImageWidth = 48,
            ImageHeight = 48,
            Content = "Opening project",
            Footer = projectName,
            IsIndeterminate = true,
            IsProgressBarVisible = true
        };
        RadSplashScreenManager.SplashScreenDataContext = context;
        RadSplashScreenManager.Show();
        shownFor.Restart();
    }

    /// <summary>Progress text under the title (e.g. "Building workspaces...").</summary>
    public void Step(string text)
    {
        if (context != null && RadSplashScreenManager.IsSplashScreenActive)
        {
            context.Footer = text;
        }
    }

    public async Task CloseAsync()
    {
        if (!RadSplashScreenManager.IsSplashScreenActive)
        {
            return;
        }

        var remaining = MinimumDisplayMs - (int)shownFor.ElapsedMilliseconds;
        if (remaining > 0)
        {
            await Task.Delay(remaining);
        }

        RadSplashScreenManager.Close();
    }
}
