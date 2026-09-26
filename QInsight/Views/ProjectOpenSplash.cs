using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.SplashScreen;

namespace Qenex.QInsight.Views;

/// <summary>
/// Splash shown while a project is opened (workspaces, controls and grids are built on the UI
/// thread; a 16x16 map alone costs a few hundred ms on the first show). RadSplashScreenManager
/// runs the splash on its own thread, so it keeps animating while the main thread works. The
/// splash has a fixed readable size (<see cref="ProjectOpenSplashContent"/>), is centred over
/// the main window and stays at least <see cref="MinimumDisplayMs"/> so a small project does
/// not make it flash (Radek 2026-09-27).
/// </summary>
public sealed class ProjectOpenSplash
{
    public const int MinimumDisplayMs = 800;
    private const double SplashWidth = 560;
    private const double SplashHeight = 280;

    private readonly Stopwatch shownFor = new();
    private SplashScreenDataContext? context;

    public void Show(string projectName)
    {
        context = new SplashScreenDataContext
        {
            ImagePath = "pack://application:,,,/Qenex.QInsight;component/Icons/About.png",
            ImageWidth = 96,
            ImageHeight = 96,
            Content = "Opening project",
            Footer = projectName,
            IsIndeterminate = true,
            IsProgressBarVisible = true
        };
        RadSplashScreenManager.SplashScreenDataContext = context;
        RadSplashScreenManager.StartupPosition = CentreOverMainWindow();
        RadSplashScreenManager.Show<ProjectOpenSplashContent>();
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

    /// <summary>
    /// Top-left of the splash so that it sits in the middle of the main window. The window
    /// rectangle comes from Win32 (Left/Top of a maximised WPF window are its restore bounds)
    /// in device pixels and is converted to the DIPs the splash window positions in.
    /// </summary>
    private static Point CentreOverMainWindow()
    {
        var main = Application.Current?.MainWindow;
        if (main == null || PresentationSource.FromVisual(main)?.CompositionTarget is not { } target ||
            !GetWindowRect(new WindowInteropHelper(main).Handle, out var rect))
        {
            var work = SystemParameters.WorkArea;
            return new Point(work.Left + (work.Width - SplashWidth) / 2, work.Top + (work.Height - SplashHeight) / 2);
        }

        var topLeft = target.TransformFromDevice.Transform(new Point(rect.Left, rect.Top));
        var bottomRight = target.TransformFromDevice.Transform(new Point(rect.Right, rect.Bottom));
        return new Point(
            topLeft.X + (bottomRight.X - topLeft.X - SplashWidth) / 2,
            topLeft.Y + (bottomRight.Y - topLeft.Y - SplashHeight) / 2);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
