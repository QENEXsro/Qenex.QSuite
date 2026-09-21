using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Xml.Linq;
using Qenex.QInsight.AppConfig;
using Qenex.QInsight.EventAggregatorMsgs;
using Qenex.QInsight.Helpers;
using Qenex.QInsight.Licensing;
using Qenex.QInsight.Models.Project;
using Qenex.QInsight.ViewModels.ModelWrappers;
using Qenex.QInsight.Views;
using Qenex.QLibs.QUI;
using Qenex.QLibs.QUI.TelerikDocking;
using Qenex.QLibs.QUI.Wpf;
using Qenex.QSuite.Common.PluginManager;
using Qenex.QSuite.Controls.Control;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using ProjectUnzipStatus = Qenex.QSuite.Helpers.ProjectFile.ProjectUnzipStatus;
using Qenex.QSuite.ModuleXmlHandler;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Scripting.ScriptingEngine;
using Qenex.QSuite.Variables.QVariables;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.Docking;
using Telerik.Windows.Controls.FileDialogs;

namespace Qenex.QInsight.ViewModels;

public partial class ShellWindowModel
{
    #region Fields

    // if true, only elements with the specified tag condition will be saved/cleaned in layout saving/cleaning process.
    // If false, elements with the specified tag condition will be excluded from layout saving/cleaning process.
    bool requireSerializationTag = false;
    private bool isReplayMode;
    private IReplayDriver? activeReplayDriver;
    private List<(IDriverBase Driver, bool IsEnabled)>? replayDriverStates;
    private List<(IProtocolBase Protocol, bool IsEnabled)>? replayProtocolStates;
    private bool isReplayDataLogImported;
    private string? replayDataLogFilePath;
    private bool canUseHomeRibbon = true;
    private bool canConnectRuntime = true;
    private bool isAppCloseConfirmed;
    private bool isAppClosePromptOpen;
    private bool canDisconnectRuntime;
    private bool canImportDataLog = true;
    private bool canExportDataLog = true;
    private bool canReplay = true;
    private bool canStopReplay;
    private bool isHandlingLicenseLoss;
    private bool isLicenseDialogOpen;
    private bool isLicenseDialogDeferred;
    private DispatcherTimer? runtimeTimeLimitTimer;
    private bool isUpdatingReplayPositionFromDriver;
    private bool isUpdatingReplayPositionTextFromPosition;
    private int replaySeekRequestVersion;
    private RadOpenFileDialog? openProjectDialog;
    private RadSaveFileDialog? saveProjectDialog;
    private RadOpenFileDialog? importDataLogDialog;
    private RadSaveFileDialog? exportDataLogDialog;
    private string? lastProjectDialogDirectory;

    // Session value first, persisted app-settings value as the cross-session fallback.
    // The setter mirrors into app settings, which are written to disk on app close.
    private string? LastProjectDialogDirectory
    {
        get => lastProjectDialogDirectory
               ?? (string.IsNullOrWhiteSpace(ShellWindow.MainAppSettings?.LastProjectDirectory)
                   ? null
                   : ShellWindow.MainAppSettings!.LastProjectDirectory);
        set
        {
            lastProjectDialogDirectory = value;
            if (ShellWindow.MainAppSettings != null && !string.IsNullOrWhiteSpace(value))
            {
                ShellWindow.MainAppSettings.LastProjectDirectory = value;
            }
        }
    }
    private string? lastDataLogDialogDirectory;
    private string? lastGraphExportDialogDirectory;
    
    #endregion
    
    #region Windows command Properties
   
    public RelayCommand<object> ShellWindowLocationChangedCommand { get; set; }
    public RelayCommand<object> ShellWindowSizeChangedCommand { get; set; }
    public RelayCommandAsync<RadDocking> ShellWindowLoadedCommandAsync { get; set; }
    public RelayCommand<CancelEventArgs> ShellWindowClosingCommand { get; set; }
    
    public RelayCommandAsync<StateChangeEventArgs> PanelCloseCommandAsync { get; set; }
    
    public RelayCommand<LayoutSerializationCleaningEventArgs> DockingElementLayoutCleaningCommand { get; set; }
    public RelayCommand<LayoutSerializationSavingEventArgs> DockingElementLayoutSavingCommand { get; set; }
    
    #endregion

    #region Ribbon command Properties

    public RelayCommandAsync<RadDocking> RibbonNewProjectCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonOpenProjectCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonSaveProjectCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonSaveProjectAsCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonCloseProjectCommand { get; set; }
    public RelayCommand<object> RibbonProjectPasswordCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonAddWorkspaceCommand { get; set; }
    public RelayCommand<RadDocking> RibbonRemoveWorkspaceCommand { get; set; }
    public RelayCommand<object> RibbonProjectConfigurationProjectCommand { get; set; }
    public RelayCommand<object> RibbonProjectConfigurationDriversCommand { get; set; }
    public RelayCommand<object> RibbonProjectConfigurationVariablesCommand { get; set; }
    public RelayCommand<object> RibbonProjectConfigurationConversionsCommand { get; set; }
    public RelayCommand<object> RibbonProjectConfigurationPresentationsCommand { get; set; }
    public RelayCommand<object> RibbonProjectConfigurationEventsCommand { get; set; }
    public RelayCommand<object> RibbonProjectConfigurationScriptsCommand { get; set; }
    
    public RelayCommandAsync<RadDocking> RibbonConnectCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonDisconnectCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonImportDataLogCommand { get; set; }
    public RelayCommand<object> RibbonCancelReplayDataLoadCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonExportDataLogCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonReplayCommand { get; set; }
    public RelayCommandAsync<RadDocking> RibbonStopReplayCommand { get; set; }
    public RelayCommand<object> RibbonReplayPauseResumeCommand { get; set; }
    
    public RelayCommand<RadDocking> RibbonSaveLayoutCommand { get; set; }
    public RelayCommand<object> RibbonThemePreferencesCommand { get; set; }
    public RelayCommand<object> RibbonGeneralPreferencesCommand { get; set; }
    
    
    public RelayCommand<RadDocking> RibbonPythonInterpreterCommand { get; set; }
    public RelayCommand<RadDocking> RibbonVariableWatchCommand { get; set; }
    public RelayCommand<object> RibbonLicenseCommand { get; set; }
    public RelayCommand<object> RibbonAboutAppCommand { get; set; }
    public RelayCommand<string> RibbonOpenDocumentationCommand { get; set; }

    #endregion
    
    #region Create all ShellViewModel commands
    
    private void CreateCommands()
    {
        ShellWindowLoadedCommandAsync = new RelayCommandAsync<RadDocking>(OnShellWindowLoadedAsync);
        ShellWindowClosingCommand = new RelayCommand<CancelEventArgs>(OnShellWindowClosing);

        ShellWindowLocationChangedCommand = new RelayCommand<object>(OnShellWindowLocationChanged);
        ShellWindowSizeChangedCommand = new RelayCommand<object>(OnShellWindowSizeChanged);
        
        // License gate (defense in depth next to the disabled Home tab): no project
        // creation or opening without a valid license.
        RibbonNewProjectCommand = new RelayCommandAsync<RadDocking>(NewProjectAsync,
            _ => canUseHomeRibbon && licenseService.IsRuntimeAllowed);
        RibbonOpenProjectCommand = new RelayCommandAsync<RadDocking>(OpenProjectAsync,
            _ => canUseHomeRibbon && licenseService.IsRuntimeAllowed);
        RibbonSaveProjectCommand = new RelayCommandAsync<RadDocking>(SaveProjectAsync, _ => CanUseProjectCommand());
        RibbonSaveProjectAsCommand = new RelayCommandAsync<RadDocking>(SaveProjectAsAsync, _ => CanUseProjectCommand());
        RibbonCloseProjectCommand = new RelayCommandAsync<RadDocking>(CloseProjectAsync, _ => CanUseProjectCommand());
        RibbonProjectPasswordCommand = new RelayCommand<object>(_ => ManageProjectPassword(), _ => CanUseProjectCommand());
        RibbonAddWorkspaceCommand = new RelayCommandAsync<RadDocking>(AddWorkspaceAsync, _ => CanUseProjectCommand());
        RibbonRemoveWorkspaceCommand = new RelayCommand<RadDocking>(RemoveWorkspace, _ => CanUseProjectCommand());
        RibbonProjectConfigurationProjectCommand = new RelayCommand<object>(
            _ => OpenProjectConfiguration(ProjectConfigurationSection.Project),
            _ => CanUseProjectCommand());
        RibbonProjectConfigurationDriversCommand = new RelayCommand<object>(
            _ => OpenProjectConfiguration(ProjectConfigurationSection.CommunicationDrivers),
            _ => CanUseProjectCommand());
        RibbonProjectConfigurationVariablesCommand = new RelayCommand<object>(
            _ => OpenProjectConfiguration(ProjectConfigurationSection.Variables),
            _ => CanUseProjectCommand());
        RibbonProjectConfigurationConversionsCommand = new RelayCommand<object>(
            _ => OpenProjectConfiguration(ProjectConfigurationSection.Conversions),
            _ => CanUseProjectCommand());
        RibbonProjectConfigurationPresentationsCommand = new RelayCommand<object>(
            _ => OpenProjectConfiguration(ProjectConfigurationSection.Presentations),
            _ => CanUseProjectCommand());
        RibbonProjectConfigurationEventsCommand = new RelayCommand<object>(
            _ => OpenProjectConfiguration(ProjectConfigurationSection.Events),
            _ => CanUseProjectCommand());
        RibbonProjectConfigurationScriptsCommand = new RelayCommand<object>(
            _ => OpenProjectConfiguration(ProjectConfigurationSection.Scripts),
            _ => CanUseProjectCommand());

        RibbonConnectCommand = new RelayCommandAsync<RadDocking>(ConnectAsync, _ => CanConnectRuntime());
        RibbonDisconnectCommand = new RelayCommandAsync<RadDocking>(DisconnectAsync, _ => canDisconnectRuntime);
        RibbonImportDataLogCommand = new RelayCommandAsync<RadDocking>(ImportDataLogAsync, _ => CanImportDataLog());
        RibbonCancelReplayDataLoadCommand = new RelayCommand<object>(_ => CancelReplayDataLoad(), _ => CanCancelReplayDataLoad());
        RibbonExportDataLogCommand = new RelayCommandAsync<RadDocking>(ExportDataLogAsync, _ => CanExportDataLog());
        RibbonReplayCommand = new RelayCommandAsync<RadDocking>(ReplayAsync, _ => CanStartReplay());
        RibbonStopReplayCommand = new RelayCommandAsync<RadDocking>(StopReplayAsync, _ => canStopReplay);
        RibbonReplayPauseResumeCommand = new RelayCommand<object>(_ => ToggleReplayPause(), _ => IsReplayControlEnabled);
        
        RibbonSaveLayoutCommand = new RelayCommand<RadDocking>(r =>
        {
            requireSerializationTag = false;
            SaveLayout(r);
        });
        RibbonThemePreferencesCommand = new RelayCommand<object>(_ => OpenThemePreferences(), _ => CanOpenThemePreferences());
        RibbonGeneralPreferencesCommand = new RelayCommand<object>(_ => OpenGeneralPreferences(), _ => CanOpenGeneralPreferences());

        // The interpreter needs the Python runtime; with Python disabled in preferences the
        // ribbon button is inactive regardless of the open project. CanExecute is first queried
        // while the XAML loads, before the settings file is read (MainAppSettings still null);
        // the state is re-evaluated once settings are loaded (OnLoaded) and after Apply.
        RibbonPythonInterpreterCommand = new RelayCommand<RadDocking>(OpenPythonInterpreter,
            _ => ShellWindow.MainAppSettings?.ScriptEngine.UsePythonScripts == true);
        RibbonVariableWatchCommand = new RelayCommand<RadDocking>(OpenVariableWatch);
        
        RibbonLicenseCommand = new RelayCommand<object>(_ => OpenLicenseDialog());

        RibbonAboutAppCommand = new RelayCommand<object>((o) =>
        {
            var aboutViewModel = new AboutAppViewModel();
            var aboutWin = new AboutAppWindow()
            {
                DataContext = aboutViewModel
            };
            var aboutDlg = new RadWindow()
            {
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Header = "About QInsight",
                ResizeMode = ResizeMode.NoResize,
                Content = aboutWin,
                CanClose = false
            };
            aboutViewModel.SetParentWindow(aboutDlg);
            aboutDlg.ShowDialog();
        });

        RibbonOpenDocumentationCommand = new RelayCommand<string>(OpenDocumentation);


        PanelCloseCommandAsync = new RelayCommandAsync<StateChangeEventArgs>(ClosePanelAsync);
        DockingElementLayoutCleaningCommand =
            new RelayCommand<LayoutSerializationCleaningEventArgs>(DockingElementLayoutCleaning);
        DockingElementLayoutSavingCommand = new RelayCommand<LayoutSerializationSavingEventArgs>(DockingElementLayoutSaving);
    }

    #endregion

    // Python is optional: with 'Use Python scripts' off nothing is reported. With it on, warns
    // when the DLL path is not configured or points to a missing file (e.g. Python was
    // uninstalled since the preferences were saved). The application keeps running.
    private void ValidatePythonDllPath()
    {
        var scriptEngine = ShellWindow.MainAppSettings.ScriptEngine;
        if (!scriptEngine.UsePythonScripts)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(scriptEngine.PythonDllPath))
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Warn,
                "Python scripting is enabled but the Python DLL path is not set; scripts cannot run. Set it in Options -> Preferences -> General."));
        }
        else if (!File.Exists(scriptEngine.PythonDllPath))
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Warn,
                $"Python scripting is enabled but the Python DLL '{scriptEngine.PythonDllPath}' does not exist; scripts cannot run. Fix the path in Options -> Preferences -> General."));
        }
    }

    private const string DocumentationBaseUrl = "https://qinsight.qenex.net/";

    // Opens the online documentation in the default browser; the parameter is the section
    // slug on qinsight.qenex.net (null or empty opens the documentation home page).
    private void OpenDocumentation(string? sectionSlug)
    {
        var url = DocumentationBaseUrl + sectionSlug;
        if (!WebBrowserLauncher.OpenUrl(url))
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Error, $"Failed to open the documentation page {url}."));
        }
    }

    private const string PythonInstallDocsSlug = "getting-started/installation#python-optional";

    // Python setup guard before a runtime/replay start. The module would refuse the start
    // (enabled scripts while Python is disabled) or fail loading the runtime (DLL path not set
    // or missing); instead of a bare log line the user gets a prompt that leads straight to
    // Preferences or to the installation chapter of the documentation. Returns false when the
    // start must not proceed. Expects Scripting.IsReplayMode already set for the session.
    private bool EnsurePythonReadyForStart(ScriptingContext scripting)
    {
        string? problem = null;
        switch (scripting.GetStartDecision())
        {
            case ScriptingStartDecision.EnabledScriptsBlocked:
                problem = scripting.BuildBlockedStartMessage();
                break;
            case ScriptingStartDecision.PythonEnabled:
                var dllPath = scripting.EngineSettings.PythonDllPath;
                if (string.IsNullOrWhiteSpace(dllPath))
                {
                    problem = "Python scripting is enabled but the Python DLL path is not set, so the session cannot start.";
                }
                else if (!File.Exists(dllPath))
                {
                    problem = $"Python scripting is enabled but the Python DLL '{dllPath}' does not exist, so the session cannot start.";
                }

                break;
        }

        if (problem == null)
        {
            return true;
        }

        logger.Log(LogLevel.Error, problem);
        ShowPythonSetupPrompt(problem);
        return false;
    }

    private void ShowPythonSetupPrompt(string message)
    {
        var promptViewModel = new PythonSetupPromptViewModel(message);
        var promptView = new PythonSetupPromptView
        {
            DataContext = promptViewModel
        };

        var promptDialog = new RadWindow
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Header = "Python Setup Required",
            Width = 620,
            Height = 300,
            MinWidth = 620,
            MinHeight = 300,
            ResizeMode = ResizeMode.NoResize,
            Content = promptView
        };

        promptViewModel.SetParentWindow(promptDialog);
        promptDialog.ShowDialog();

        switch (promptViewModel.Decision)
        {
            case PythonSetupDecision.OpenPreferences:
                OpenGeneralPreferences();
                break;
            case PythonSetupDecision.OpenDocumentation:
                OpenDocumentation(PythonInstallDocsSlug);
                break;
        }
    }
    
    #region Window command methods

    private void OnShellWindowLocationChanged(object obj)
    {
        if (Application.Current.MainWindow == null) return;

		ShellWindow.MainAppSettings.WinStyle.Top = Application.Current.MainWindow.Top;
		ShellWindow.MainAppSettings.WinStyle.Left = Application.Current.MainWindow.Left;
    }
    private void OnShellWindowSizeChanged(object obj)
    {
        if (Application.Current.MainWindow == null) return;

		ShellWindow.MainAppSettings.WinStyle.Height = (int)Application.Current.MainWindow.Height;
		ShellWindow.MainAppSettings.WinStyle.Width = (int) Application.Current.MainWindow.Width;
		ShellWindow.MainAppSettings.WinStyle.WinState = Application.Current.MainWindow.WindowState;
    }

    // This method is called when the docking manager is loaded
    private async Task OnShellWindowLoadedAsync(RadDocking docking)
    {
        try
        {
            shellRadDocking = docking;

            Application.Current.MainWindow!.WindowState = ShellWindow.MainAppSettings.WinStyle.WinState;

            // Load drivers, protocols and controls BEFORE any docking layout: layout
            // restore resolves pane types by assembly NAME, and whichever file wins that
            // first name-based load owns the assembly identity for the whole session.
            // Loading the plugins first pins the identity to the plugin folders — a stray
            // or stale copy elsewhere can then never shadow them. Plugin folders live next
            // to the executable; resolve them from BaseDirectory because the process
            // working directory differs when the app is started via the .qproj file
            // association or a shortcut.
            var pluginBaseDir = AppContext.BaseDirectory;
            pluginLoader = new PluginLoader(logger);
            driverPlugins = pluginLoader.GetPluginDetails<IDriverBase>(Path.Combine(pluginBaseDir, "Drivers"), TransportTypes.Probe);
            protocolPlugins = pluginLoader.GetPluginDetails<IProtocolBase>(Path.Combine(pluginBaseDir, "Protocols"), TransportTypes.Probe,
                TransportTypes.ProbeCompatibleDrivers);
            // Controls are loaded dynamically as plugins; the folder may not exist (e.g. nothing copied)
            var controlsDir = Path.Combine(pluginBaseDir, "Controls");
            controlPlugins = Directory.Exists(controlsDir)
                ? pluginLoader.GetPluginDetails<IControlBase>(controlsDir)
                : [];
            controlsViewModel.SetControlPlugins(controlPlugins);

            // Load layout settings (after plugin load — see above)
            LoadSettingsFromFile(shellRadDocking, editModeSettingLayoutFile);

            ValidatePythonDllPath();
            RibbonPythonInterpreterCommand.OnCanExecuteChanged();

            // License gate: with no valid license (missing file, failed validation or
            // expired token) the ribbon is locked to Help — bring the user straight to
            // the activation dialog and skip any command-line project open, which would
            // otherwise bypass the disabled ribbon (file association double-click).
            if (!licenseService.IsRuntimeAllowed)
            {
                SelectedRibbonTabIndex = HelpTabIndex;
                if (GetCommandLineProjectFilePath() is { } skippedProjectFilePath)
                {
                    logger.Log(LogLevel.Warn,
                        $"Project \"{Path.GetFileName(skippedProjectFilePath)}\" was not opened: no valid license.");
                }

                OpenLicenseDialog();
            }
            else if (GetCommandLineProjectFilePath() is { } projectFilePath)
            {
                await OpenProjectFileAsync(projectFilePath);
            }
        }
        catch (Exception e)
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Error, e.Message));
        }
    }

    private static string? GetCommandLineProjectFilePath()
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length != 2)
        {
            return null;
        }

        var projectFilePath = cmdArgs[1];
        return File.Exists(projectFilePath)
               && Path.GetExtension(projectFilePath).Equals(".qproj", StringComparison.OrdinalIgnoreCase)
            ? projectFilePath
            : null;
    }

    // This method is called when the shell window is closing. With a project open the
    // close is cancelled first and re-triggered after the user confirms it.
    private void OnShellWindowClosing(CancelEventArgs e)
    {
        if (isProjectMade && !isAppCloseConfirmed)
        {
            // Dialogs cannot be shown while the window is still closing — cancel the
            // close first and ask only after this Closing event has fully unwound.
            e.Cancel = true;
            _ = Application.Current.Dispatcher.InvokeAsync(
                () => _ = ConfirmAppCloseAsync(),
                DispatcherPriority.Background);
            return;
        }

        try
        {
            licenseService.Dispose();
            AppSettings.SaveAppSettingsToFile(AppDataPaths.AppSettingsFile, ShellWindow.MainAppSettings);
        }
        catch (Exception ex)
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Error, ex.Message));
        }
    }

    private async Task ConfirmAppCloseAsync()
    {
        if (isAppClosePromptOpen)
        {
            return;
        }

        isAppClosePromptOpen = true;
        try
        {
            var confirmed = await ConfirmCloseProjectAsync(
                "A project is open and will be closed.\nDo you want to close the application?",
                "Close Application");
            if (confirmed)
            {
                isAppCloseConfirmed = true;
                Application.Current.MainWindow?.Close();
            }
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, e.Message);
        }
        finally
        {
            isAppClosePromptOpen = false;
        }
    }
    
    private async Task ClosePanelAsync(StateChangeEventArgs arg)
    {
        foreach (var pane in arg.Panes.ToList())
        {
            if (pane.DataContext is PythonInterpreterViewModel pythonInterpreterViewModel)
            {
                await pythonInterpreterViewModel.CleanAsync();
                ViewModels.Remove(pythonInterpreterViewModel);
            }

            if (pane.DataContext is VariableWatchViewModel variableWatchViewModel)
            {
                await variableWatchViewModel.CleanAsync();
                ViewModels.Remove(variableWatchViewModel);
            }
        }
    }
    
    private void DockingElementLayoutSaving(LayoutSerializationSavingEventArgs e)
    {
        CancelLayoutSavingByTagCondition(e, requireSerializationTag);
        // var tag = e.AffectedElementSerializationTag;
        // if (!tag.Contains("WorkspaceViewModel"))
        // {
        //     e.Cancel = true;
        // }
    }
    
    private void CancelLayoutSavingByTagCondition(LayoutSerializationSavingEventArgs eventArgs, bool reqSerializationTag)
    {
        var isWorkspaceLayoutElement = IsProjectWorkspaceLayoutElement(eventArgs.AffectedElementSerializationTag);
        if (isWorkspaceLayoutElement != reqSerializationTag)
        {
            eventArgs.Cancel = true;
        }
    }
    
    private void DockingElementLayoutCleaning(LayoutSerializationCleaningEventArgs e)
    {
        CancelLayoutSCleaningByTagCondition(e, requireSerializationTag);
        // var tag = e.AffectedElementSerializationTag;
        // if (!tag.Contains("WorkspaceViewModel"))
        // {
        //     e.Cancel = true;
        // }
    }

    private void CancelLayoutSCleaningByTagCondition(LayoutSerializationCleaningEventArgs eventArgs, bool reqSerializationTag)
    {
        var isWorkspaceLayoutElement = IsProjectWorkspaceLayoutElement(eventArgs.AffectedElementSerializationTag);
        if (isWorkspaceLayoutElement != reqSerializationTag)
        {
            eventArgs.Cancel = true;
        }
    }

    private static bool IsProjectWorkspaceLayoutElement(string serializationTag)
    {
        return serializationTag.Contains("WorkspaceViewModel", StringComparison.Ordinal);
    }

	#endregion

    #region Ribbon command methods

    private const double ProjectConfigurationDialogMinWidth = 600;
    private const double ProjectConfigurationDialogMinHeight = 450;

    /// <summary>
    /// Remembered dialog size clamped to the application window (a modal dialog larger than its
    /// owner would hide its own edges); unset or nonsense values fall back to the defaults.
    /// </summary>
    private static (double Width, double Height) GetProjectConfigurationDialogSize(Window? owner)
    {
        var remembered = ShellWindow.MainAppSettings.ProjectConfigurationWindow;
        var width = remembered.Width > 0 ? remembered.Width : AppSettings.ProjectConfigurationDefaultWidth;
        var height = remembered.Height > 0 ? remembered.Height : AppSettings.ProjectConfigurationDefaultHeight;

        if (owner is { ActualWidth: > 0, ActualHeight: > 0 })
        {
            width = Math.Min(width, owner.ActualWidth);
            height = Math.Min(height, owner.ActualHeight);
        }

        return (Math.Max(width, ProjectConfigurationDialogMinWidth), Math.Max(height, ProjectConfigurationDialogMinHeight));
    }

    private void RememberProjectConfigurationDialogSize(double width, double height)
    {
        if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
        {
            return;
        }

        var remembered = ShellWindow.MainAppSettings.ProjectConfigurationWindow;
        if (Math.Abs(remembered.Width - width) < 1 && Math.Abs(remembered.Height - height) < 1)
        {
            return;
        }

        remembered.Width = Math.Round(width);
        remembered.Height = Math.Round(height);

        // Persist right away (like the preference dialogs do) so the size survives a crash or
        // a forced exit; the file is small.
        try
        {
            AppSettings.SaveAppSettingsToFile(AppDataPaths.AppSettingsFile, ShellWindow.MainAppSettings);
        }
        catch (Exception ex)
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Warn, $"Could not save the application settings: {ex.Message}"));
        }
    }

    private void OpenProjectConfiguration(ProjectConfigurationSection selectedSection)
    {
        var projectConfigurationViewModel = new ProjectConfigurationViewModel(
            eventAggregator,
            realProjectData.Module,
            realProjectData.Module.Drivers,
            driverPlugins,
            protocolPlugins,
            realProjectData.Module.Variables,
            realProjectData.Module.Conversions,
            realProjectData.Module.Presentations,
            realProjectData.Module.VarEvents,
            realProjectData.Module.Scripting.OnValueChangedScriptTriggers,
            realProjectData.Module.Scripting.Scripts,
            CommunicatedSignals.GetLimit(licenseService));
        projectConfigurationViewModel.SelectSection(selectedSection);

        var projectConfigurationView = new ProjectConfigurationView()
        {
            DataContext = projectConfigurationViewModel
        };

        // The dialog opens with the size the user last gave it (QInsightAppSettings.xml),
        // never larger than the application window it is centred on.
        var owner = Application.Current.MainWindow;
        var (width, height) = GetProjectConfigurationDialogSize(owner);

        var projectConfigurationDialog = new RadWindow()
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Header = "Project Configuration",
            Width = width,
            Height = height,
            MinWidth = ProjectConfigurationDialogMinWidth,
            MinHeight = ProjectConfigurationDialogMinHeight,
            ResizeMode = ResizeMode.CanResize,
            Content = projectConfigurationView
        };

        // Captured while the window is still laid out (PreviewClosed); a maximized dialog keeps
        // the previously remembered size so it does not come back maximized-sized next time.
        projectConfigurationDialog.PreviewClosed += (_, _) =>
        {
            if (projectConfigurationDialog.WindowState == WindowState.Normal)
            {
                RememberProjectConfigurationDialogSize(projectConfigurationDialog.ActualWidth, projectConfigurationDialog.ActualHeight);
            }
        };

        projectConfigurationViewModel.SetParentWindow(projectConfigurationDialog);
        projectConfigurationDialog.ShowDialog();

        // The communicated-signal count may have changed; re-query the Free-tier limit guards
        // and refresh the badge counter.
        NotifyLicenseDependentCommands();
        UpdateLicenseBadge();
    }

    private void OpenThemePreferences()
    {
        var preferencesViewModel = new ThemePreferencesViewModel(ShellWindow.MainAppSettings, logger);
        var preferencesView = new ThemePreferencesView
        {
            DataContext = preferencesViewModel
        };

        var preferencesDialog = new RadWindow
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Header = "Theme Preferences",
            Width = 500,
            Height = 300,
            MinWidth = 500,
            MinHeight = 300,
            ResizeMode = ResizeMode.NoResize,
            Content = preferencesView
        };

        preferencesViewModel.SetParentWindow(preferencesDialog);
        preferencesDialog.ShowDialog();
    }

    private void OpenLicenseDialog()
    {
        // A heartbeat refusal (e.g. LicenseRevoked) can arrive before the shell window is
        // shown; RadWindow.Owner requires a shown window ("Cannot set Owner property to a
        // Window that has not been shown previously", seen 2026-09-12). Defer until loaded.
        if (Application.Current.MainWindow is not { IsLoaded: true } mainWindow)
        {
            if (isLicenseDialogDeferred)
            {
                return;
            }

            isLicenseDialogDeferred = true;
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                isLicenseDialogDeferred = false;
                if (!isLicenseDialogOpen)
                {
                    OpenLicenseDialog();
                }
            });
            return;
        }

        var licenseViewModel = new LicenseViewModel(licenseService, logger,
            () => CommunicatedSignals.Count(realProjectData?.Module));
        var licenseView = new LicenseView
        {
            DataContext = licenseViewModel
        };

        var licenseDialog = new RadWindow
        {
            Owner = mainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Header = "QInsight License",
            ResizeMode = ResizeMode.NoResize,
            Content = licenseView,
            CanClose = false
        };

        licenseViewModel.SetParentWindow(licenseDialog);
        // The flag stops HandleLicenseStateChangedAsync from opening a second dialog
        // when a heartbeat refusal arrives while the user is already in this one.
        isLicenseDialogOpen = true;
        try
        {
            licenseDialog.ShowDialog();
        }
        finally
        {
            isLicenseDialogOpen = false;
        }
    }

    /// <summary>License-gate follow-up to every license state change. When the license
    /// stops being valid mid-session, an open project is saved on demand and closed so
    /// the application ends in the same Help-only state as an unlicensed startup.</summary>
    private async Task HandleLicenseStateChangedAsync()
    {
        if (licenseService.IsRuntimeAllowed || isHandlingLicenseLoss)
        {
            return;
        }

        isHandlingLicenseLoss = true;
        try
        {
            if (isProjectMade)
            {
                var saveRequested = await ConfirmCloseProjectAsync(
                    "The license is no longer valid and the project will be closed.\n"
                    + "Do you want to save the project first?",
                    "License Required");
                if (saveRequested)
                {
                    await SaveProjectAsync(shellRadDocking);
                }

                await CloseProjectCoreAsync();
            }

            SelectedRibbonTabIndex = HelpTabIndex;
            if (!isLicenseDialogOpen)
            {
                OpenLicenseDialog();
            }
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, e.Message);
        }
        finally
        {
            isHandlingLicenseLoss = false;
        }
    }

    private void OpenGeneralPreferences()
    {
        var generalPreferencesViewModel = new GeneralPreferencesViewModel(ShellWindow.MainAppSettings, logger,
            () =>
            {
                LogsViewModel.ShowDebugMessages = ShellWindow.MainAppSettings.ShowDebugLogMessages;
                RibbonPythonInterpreterCommand.OnCanExecuteChanged();
            });
        var generalPreferencesView = new GeneralPreferencesView
        {
            DataContext = generalPreferencesViewModel
        };

        var generalPreferencesDialog = new RadWindow
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Header = "General",
            Width = 640,
            Height = 330,
            MinWidth = 640,
            MinHeight = 330,
            ResizeMode = ResizeMode.NoResize,
            Content = generalPreferencesView
        };

        generalPreferencesViewModel.SetParentWindow(generalPreferencesDialog);
        generalPreferencesDialog.ShowDialog();
    }

    #region Project menu

    private async Task NewProjectAsync(object obj)
    {
        if (!TemplateSealValid())
            return;

        try
        {
            if (isProjectMade)
            {
                var decision = PromptCloseOpenProject("New Project");
                if (decision == CloseProjectDecision.Cancel)
                {
                    return;
                }

                if (decision == CloseProjectDecision.SaveAndClose && !await TrySaveProjectForCloseAsync())
                {
                    return;
                }

                await CloseProjectCoreAsync();
            }
            else
            {
                await CloseProjectWorkspacesAsync();
            }

            CreateNewProject();
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, e.Message);
        }
    }

    private async Task OpenProjectAsync(object obj)
    {
        var initialDirectory = GetInitialDialogDirectory(LastProjectDialogDirectory, currentProjectFilePath);
        var dlg = openProjectDialog ??= CreateOpenFileDialog("QInsight project files (*.qproj)|*.qproj", initialDirectory);
        PrepareFileDialog(dlg, initialDirectory);

        dlg.ShowDialog();

        
        if (dlg.DialogResult == true)
        {
            LastProjectDialogDirectory = Path.GetDirectoryName(dlg.FileName);
            await OpenProjectFileAsync(dlg.FileName);
        }
    }

    private async Task OpenProjectFileAsync(string filePath)
    {
        if (!ArchiveSealValid())
            return;

        try
        {
            if (isProjectMade)
            {
                var decision = PromptCloseOpenProject("Open Project");
                if (decision == CloseProjectDecision.Cancel)
                {
                    return;
                }

                if (decision == CloseProjectDecision.SaveAndClose && !await TrySaveProjectForCloseAsync())
                {
                    return;
                }
            }

            // Password flow: first try without a password; a protected file answers
            // PasswordRequired and the prompt loop runs until success or Cancel.
            string? password = null;
            ProjectOpenResult openResult;
            while (true)
            {
                openResult = await ProjectZip.UnzipProjectFileAsync(filePath, password, logger);
                if (openResult.Status is ProjectUnzipStatus.PasswordRequired or ProjectUnzipStatus.WrongPasswordOrCorrupt)
                {
                    password = PromptProjectPassword(
                        Path.GetFileName(filePath),
                        isRetry: openResult.Status == ProjectUnzipStatus.WrongPasswordOrCorrupt);
                    if (password == null)
                    {
                        return;
                    }

                    continue;
                }

                break;
            }

            var projectData = openResult.Data;
            if (openResult.Status != ProjectUnzipStatus.Success || projectData == null)
            {
                logger?.Log(LogLevel.Warn, $"Failed to load project file \"{Path.GetFileName(filePath)}\".");
                return;
            }

            var findings = ReportProjectValidationFindings(projectData, Path.GetFileName(filePath));

            await CloseProjectWorkspacesAsync();
            isEditProjectEnabled = false;
            isEditVariableEnabled = false;
            isEditConversionEnabled = false;
            isEditPresentationEnabled = false;
            isEditEventEnabled = false;
            realProjectData = RealProjectData.CreateRealProjectData(driverPlugins, protocolPlugins, projectData, ShellWindow.MainAppSettings.ScriptEngine, logger);
            solutionExplorerViewModel.ReloadProjectData(realProjectData);
            LoadProjectWorkspaces(projectData.Workspaces);
            LoadProjectScriptDocuments(projectData.ScriptDocuments);
            LoadProjectPythonInterpreter(projectData.WorkspaceLayout);
            LoadProjectVariableWatch(projectData.WorkspaceLayout);
            await Application.Current.Dispatcher.InvokeAsync(
                () => LoadWorkspaceLayoutFromData(projectData.WorkspaceLayout, projectData.FailedWorkspaces.Count > 0),
                DispatcherPriority.ApplicationIdle);
                
            currentProjectFilePath = Path.GetFullPath(filePath);
            currentProjectPassword = password;
            SetProjectWindowTitle(currentProjectFilePath);
            ChangeIsProjectMade(true);
            ClearReplayDataLogImportState();
            RefreshVariableWatch();
            NotifyRuntimeCommandsCanExecuteChanged();
            RibbonReplayCommand.OnCanExecuteChanged();
            // Exactly one closing log line: clean open = Info, otherwise a single Warn
            // carrying every finding (the details are not logged anywhere else).
            if (findings.Count > 0)
            {
                logger.Log(LogLevel.Warn,
                    $"Project file \"{Path.GetFileName(filePath)}\" opened with {findings.Count} issue(s): {string.Join(" | ", findings)}");
            }
            else
            {
                logger.Log(LogLevel.Info, $"Project file \"{Path.GetFileName(filePath)}\" opened.");
            }
                
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, e.Message);
        }        
    }
    
    // Consistency checks of the loaded project file (format version, plugin references,
    // driver-protocol compatibility, variable references, missing scripts, failed
    // workspaces). Findings go into one Alert window; the caller folds them into the
    // single closing log line. The project opens regardless.
    private List<string> ReportProjectValidationFindings(ProjectFilesData projectData, string fileName)
    {
        const int maxAlertLines = 15;

        var findings = new List<string>(XmlModuleValidator.Validate(projectData.Module, driverPlugins, protocolPlugins));
        findings.AddRange(projectData.MissingScriptFiles.Select(scriptFile =>
            $"Script file \"{scriptFile}\" is listed in the project but missing in the project file."));
        findings.AddRange(projectData.FailedWorkspaces);

        if (findings.Count == 0)
        {
            return findings;
        }

        var alertLines = findings.Take(maxAlertLines).Select(finding => $"• {finding}");
        var overflow = findings.Count > maxAlertLines
            ? $"\n… and {findings.Count - maxAlertLines} more issue(s), see the log."
            : string.Empty;

        RadWindow.Alert(new DialogParameters
        {
            Header = "Project Validation",
            Content = $"The project \"{fileName}\" was opened with {findings.Count} issue(s):\n\n"
                      + string.Join("\n", alertLines) + overflow,
            Owner = Application.Current.MainWindow,
            DialogStartupLocation = WindowStartupLocation.CenterOwner
        });

        return findings;
    }

    private async Task SaveProjectAsync(object obj)
    {
        if (!isProjectMade)
        {
            logger.Log(LogLevel.Warn, "No project to save.");
            return;
        }

        if (string.IsNullOrWhiteSpace(currentProjectFilePath))
        {
            await SaveProjectAsAsync(obj);
            return;
        }

        await SaveProjectFileAsync(currentProjectFilePath);
    }
    
    private async Task SaveProjectAsAsync(object obj)
    {
        if (!isProjectMade)
        {
            logger.Log(LogLevel.Warn, "No project to save.");
            return;
        }
        
        var initialDirectory = GetInitialDialogDirectory(LastProjectDialogDirectory, currentProjectFilePath);
        var dlg = saveProjectDialog ??= CreateSaveFileDialog("QInsight project files (*.qproj)|*.qproj", initialDirectory);
        PrepareFileDialog(dlg, initialDirectory);

        dlg.ShowDialog();

        
        if (dlg.DialogResult == true)
        {
            LastProjectDialogDirectory = Path.GetDirectoryName(dlg.FileName);
            var saved = await SaveProjectFileAsync(dlg.FileName);
            if (saved)
            {
                currentProjectFilePath = Path.GetFullPath(dlg.FileName);
                SetProjectWindowTitle(currentProjectFilePath);
            }
        }
    }
    
    /// <summary>Save used by the close-project prompts. Returns false when nothing was
    /// saved (Save As cancelled or save failed) so the caller can abort the close.</summary>
    private async Task<bool> TrySaveProjectForCloseAsync()
    {
        if (string.IsNullOrWhiteSpace(currentProjectFilePath))
        {
            await SaveProjectAsAsync(shellRadDocking);
            return !string.IsNullOrWhiteSpace(currentProjectFilePath);
        }

        return await SaveProjectFileAsync(currentProjectFilePath);
    }

    private async Task<bool> SaveProjectFileAsync(string filePath)
    {
        var tempFilePath = GetTempProjectFilePath(filePath);
        var hasProjectBackup = false;
        
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
            
            if (File.Exists(filePath))
            {
                File.Move(filePath, tempFilePath);
                hasProjectBackup = true;
            }
            
            var workspaces = CreateWorkspaceProjectData();
            var scriptDocuments = CreateScriptDocumentProjectData();
            using var workspaceLayoutStream = CreateWorkspaceLayoutStream(shellRadDocking);
            await ProjectZip.ZipProjectFileAsync(filePath, realProjectData, workspaces, scriptDocuments, workspaceLayoutStream, currentProjectPassword, logger);
                
            if (hasProjectBackup)
            {
                File.Delete(tempFilePath);
            }
            
            logger.Log(LogLevel.Info, $"Project file \"{Path.GetFileName(filePath)}\" saved.");
            return true;
                
        }
        catch (Exception e)
        {
            RestoreProjectFile(filePath, tempFilePath, hasProjectBackup);
            logger.Log(LogLevel.Error, e.Message);
            return false;
        }        
    }

    private static string GetTempProjectFilePath(string filePath)
    {
        var directoryName = Path.GetDirectoryName(filePath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        var tempFileName = $"{fileNameWithoutExtension}_temp{extension}";

        return string.IsNullOrWhiteSpace(directoryName)
            ? tempFileName
            : Path.Combine(directoryName, tempFileName);
    }

    private void RestoreProjectFile(string filePath, string tempFilePath, bool hasProjectBackup)
    {
        if (!hasProjectBackup || !File.Exists(tempFilePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            File.Move(tempFilePath, filePath);
        }
        catch (Exception restoreException)
        {
            logger.Log(LogLevel.Error, $"Project file \"{Path.GetFileName(filePath)}\" restore failed.", restoreException);
        }
    }

    private async Task CloseProjectAsync(object obj)
    {
        if (!isProjectMade)
        {
            logger.Log(LogLevel.Warn, "No project to close.");
            return;
        }

        if (!await ConfirmCloseProjectAsync())
        {
            return;
        }

        try
        {
            await CloseProjectCoreAsync();
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, e.Message);
        }
    }

    private Task<bool> ConfirmCloseProjectAsync()
    {
        return ConfirmCloseProjectAsync("Do you want to close current project?", "Close Project");
    }

    /// <summary>Modal three-way prompt shown before an open project is replaced by
    /// New/Open. Closing the window without choosing means Cancel.</summary>
    private CloseProjectDecision PromptCloseOpenProject(string header)
    {
        var projectName = !string.IsNullOrWhiteSpace(currentProjectFilePath)
            ? Path.GetFileName(currentProjectFilePath)
            : realProjectData.Module.Specification.Label;
        var promptViewModel = new CloseProjectPromptViewModel(
            $"Project \"{projectName}\" is open and will be closed.\nDo you want to save it first?");
        var promptView = new CloseProjectPromptView
        {
            DataContext = promptViewModel
        };

        var promptDialog = new RadWindow
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Header = header,
            Width = 560,
            Height = 200,
            MinWidth = 560,
            MinHeight = 200,
            ResizeMode = ResizeMode.NoResize,
            Content = promptView
        };

        promptViewModel.SetParentWindow(promptDialog);
        promptDialog.ShowDialog();
        return promptViewModel.Decision;
    }

    /// <summary>Modal password prompt for opening a protected project. Returns the
    /// entered password, or null when the user cancels the open.</summary>
    private static string? PromptProjectPassword(string fileName, bool isRetry)
    {
        var promptViewModel = new ProjectPasswordPromptViewModel(fileName, isRetry);
        var promptView = new ProjectPasswordPromptView
        {
            DataContext = promptViewModel
        };

        var promptDialog = new RadWindow
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Header = "Project Password",
            Width = 420,
            Height = 220,
            MinWidth = 420,
            MinHeight = 220,
            ResizeMode = ResizeMode.NoResize,
            Content = promptView
        };

        promptViewModel.SetParentWindow(promptDialog);
        promptDialog.ShowDialog();
        return promptViewModel.EnteredPassword;
    }

    /// <summary>Ribbon Password button: set, change or remove the password of the open
    /// project. The change is held in memory and takes effect on the next save.</summary>
    private void ManageProjectPassword()
    {
        var passwordViewModel = new ProjectPasswordViewModel(currentProjectPassword);
        var passwordView = new ProjectPasswordView
        {
            DataContext = passwordViewModel
        };

        var passwordDialog = new RadWindow
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Header = "Project Password",
            Width = 480,
            Height = 340,
            MinWidth = 480,
            MinHeight = 340,
            ResizeMode = ResizeMode.NoResize,
            Content = passwordView
        };

        passwordViewModel.SetParentWindow(passwordDialog);
        passwordDialog.ShowDialog();

        switch (passwordViewModel.Action)
        {
            case ProjectPasswordAction.Set:
                currentProjectPassword = passwordViewModel.ResultPassword;
                logger.Log(LogLevel.Info, "Project password set. It will be applied when the project is saved.");
                break;
            case ProjectPasswordAction.Remove:
                currentProjectPassword = null;
                logger.Log(LogLevel.Info, "Project password removed. The change will be applied when the project is saved.");
                break;
        }
    }

    private Task<bool> ConfirmCloseProjectAsync(string content, string header)
    {
        var closeConfirmed = new TaskCompletionSource<bool>();
        RadWindow.Confirm(new DialogParameters()
        {
            Content = content,
            Header = header,
            Owner = Application.Current.MainWindow,
            DialogStartupLocation = WindowStartupLocation.CenterOwner,
            Closed = (_, arg) => closeConfirmed.SetResult(arg.DialogResult == true)
        });

        return closeConfirmed.Task;
    }

    private async Task CloseProjectCoreAsync()
    {
        if (IsRuntimeStarted)
        {
            await realProjectData.Module.StopAsync();
            SetRuntimeStartedState(false);
            SetRuntimeCommandStates(false);
        }

        await CloseProjectWorkspacesAsync();
        solutionExplorerViewModel.DisposeAll();

        realProjectData = null!;
        isEditProjectEnabled = false;
        isEditVariableEnabled = false;
        isEditConversionEnabled = false;
        isEditPresentationEnabled = false;
        isEditEventEnabled = false;
        currentProjectFilePath = null;
        currentProjectPassword = null;
        SetProjectWindowTitle(currentProjectFilePath);
        ChangeIsProjectMade(false);
        ClearReplayDataLogImportState();
        RefreshVariableWatch();
        NotifyRuntimeCommandsCanExecuteChanged();
        RibbonReplayCommand.OnCanExecuteChanged();

        ScriptLogsViewModel.ClearLog();
        LogsViewModel.ClearLog();
        SetDefaultPropertiesView();
    }

    private void CreateNewProject()
    {
        isEditProjectEnabled = false;
        isEditVariableEnabled = false;
        isEditConversionEnabled = false;
        isEditPresentationEnabled = false;
        isEditEventEnabled = false;
        realProjectData = RealProjectData.CreateEmptyProjectData(ShellWindow.MainAppSettings.ScriptEngine, logger);
        solutionExplorerViewModel.ReloadProjectData(realProjectData);
        currentProjectFilePath = null;
        currentProjectPassword = null;
        SetProjectWindowTitle(currentProjectFilePath);
        ChangeIsProjectMade(true);
        ClearReplayDataLogImportState();
        RefreshVariableWatch();
        NotifyRuntimeCommandsCanExecuteChanged();
        RibbonReplayCommand.OnCanExecuteChanged();
        SetDefaultPropertiesView();
        logger.Log(LogLevel.Info, "New project created.");
    }

    private async Task CloseProjectWorkspacesAsync()
    {
        var workspaceViewModels = ViewModels
            .Where(vm => vm is WorkspaceViewModel or ScriptViewModel or PythonInterpreterViewModel or VariableWatchViewModel)
            .ToList();
        foreach (var workspaceViewModelBase in workspaceViewModels)
        {
            if (workspaceViewModelBase is IWorkspaceViewModel workspaceViewModel)
            {
                await workspaceViewModel.CleanAsync();
            }
            
            ViewModels.Remove(workspaceViewModelBase);
        }
    }

    #endregion
    
    #region Workspace menu

    private WorkspaceViewModel CreateWorkspaceViewModel()
    {
        return new WorkspaceViewModel(eventAggregator)
        {
            ConfigureGraphControlSaveDialog = FileDialogConfiguration.ConfigureFastFileDialog,
            GraphControlSaveDialogInitialDirectoryProvider = () =>
                GetInitialDialogDirectory(lastGraphExportDialogDirectory, currentProjectFilePath),
            GraphControlSaveDialogDirectoryChanged = directory => lastGraphExportDialogDirectory = directory,
            CanWriteProtocolVariable = CanWriteProtocolVariable
        };
    }

    /// <summary>
    /// Zapisovatelnost promenne urcuje protokol (Modbus Direction, XCP calibration, ...);
    /// protokol si ownership promenne kontroluje sam v CanWriteVariable.
    /// </summary>
    private bool CanWriteProtocolVariable(IProtocolVariable protocolVariable)
    {
        return realProjectData?.Module?.Drivers != null
               && realProjectData.Module.Drivers
                   .SelectMany(driver => driver.Protocols)
                   .OfType<IProtocolVariableWriteProtocol>()
                   .Any(protocol => protocol.CanWriteVariable(protocolVariable));
    }

    private async Task AddWorkspaceAsync(RadDocking docking)
    {
        var targetGroup = GetTargetDocumentPaneGroup(docking);
        var workspaceViewModel = CreateWorkspaceViewModel();
        ViewModels.Add(workspaceViewModel);
        await MoveNewWorkspaceDocumentToTargetGroupAsync(docking, workspaceViewModel, targetGroup);
        
        // Add new workspace to Solution Explorer
        eventAggregator.Publish(new AddWorkspaceEaMsg() { WorkspaceViewModel =  workspaceViewModel});
    }

    private void LoadProjectWorkspaces(IEnumerable<WorkspaceProjectData> workspaces)
    {
        foreach (var workspace in workspaces)
        {
            var workspaceViewModel = CreateWorkspaceViewModel();
            workspaceViewModel.Name = workspace.Name;
            workspaceViewModel.WinTitle = workspace.WinTitle;
            workspaceViewModel.SetControlProjectData(workspace.Controls);
            workspaceViewModel.BindLoadedControlVariables(GetProjectProtocolVariables());

            ViewModels.Add(workspaceViewModel);
            solutionExplorerViewModel.AddWorkspace(workspaceViewModel);
        }
    }

    private void LoadProjectScriptDocuments(IEnumerable<ScriptDocumentProjectData> scriptDocuments)
    {
        foreach (var scriptDocument in scriptDocuments)
        {
            var script = realProjectData.Module.Scripting.Scripts.FirstOrDefault(script =>
                script.FileName.Equals(scriptDocument.FileName, StringComparison.OrdinalIgnoreCase));

            if (script == null)
            {
                logger.Log(LogLevel.Warn, $"Script document \"{scriptDocument.FileName}\" was not found in project.");
                continue;
            }

            var scriptWrapper = new ScriptWrapper(script, realProjectData.Module.Scripting);
            var scriptViewModel = new ScriptViewModel(eventAggregator, scriptWrapper)
            {
                Name = scriptDocument.Name,
                IsRuntimeRunning = IsRuntimeStarted
            };

            ViewModels.Add(scriptViewModel);
        }
    }
    
    private void RemoveWorkspace(RadDocking docking)
    {
        if (docking.ActivePane is not QRadDocumentPane pane || pane.DataContext is not WorkspaceViewModel workspaceViewModel)
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Warn, "No active workspace to remove."));
            return;
        }
        
        RadWindow.Confirm(new DialogParameters()
        {
            Content = $"Do you want to remove workspace \"{workspaceViewModel.WinTitle}\"?",
            Header = "Remove Workspace",
            Owner = Application.Current.MainWindow,
            DialogStartupLocation = WindowStartupLocation.CenterOwner,
            Closed = (_, arg) =>
            {
                if (arg.DialogResult != true) return;
                
                workspaceViewModel.Clean();
                eventAggregator.Publish<RemoveWorkspaceFromMainMenuMsg>(new RemoveWorkspaceFromMainMenuMsg() { Name = workspaceViewModel.Name, Label = workspaceViewModel.WinTitle });
                ViewModels.Remove(workspaceViewModel);
                pane.RemoveFromParent();
            }
        });
    }
    
    #endregion

    private void OpenPythonInterpreter(RadDocking docking)
    {
        foreach (var pythonInterpreterViewModel in ViewModels.OfType<PythonInterpreterViewModel>().ToList())
        {
            if (FindDockingPaneForViewModel(docking, pythonInterpreterViewModel) != null)
            {
                pythonInterpreterViewModel.IsHidden = false;
                return;
            }

            ViewModels.Remove(pythonInterpreterViewModel);
        }

        var targetGroup = GetTargetDocumentPaneGroup(docking);
        var newPythonInterpreterViewModel = CreatePythonInterpreterViewModel();
        ViewModels.Add(newPythonInterpreterViewModel);
        _ = MoveNewWorkspaceDocumentToTargetGroupAsync(docking, newPythonInterpreterViewModel, targetGroup);
    }

    private void OpenVariableWatch(RadDocking docking)
    {
        foreach (var variableWatchViewModel in ViewModels.OfType<VariableWatchViewModel>().ToList())
        {
            if (FindDockingPaneForViewModel(docking, variableWatchViewModel) != null)
            {
                variableWatchViewModel.Refresh(realProjectData);
                variableWatchViewModel.IsHidden = false;
                return;
            }

            ViewModels.Remove(variableWatchViewModel);
        }

        var targetGroup = GetTargetDocumentPaneGroup(docking);
        var newVariableWatchViewModel = CreateVariableWatchViewModel();
        newVariableWatchViewModel.Refresh(realProjectData);
        ViewModels.Add(newVariableWatchViewModel);
        _ = MoveNewWorkspaceDocumentToTargetGroupAsync(docking, newVariableWatchViewModel, targetGroup);
    }

    private VariableWatchViewModel CreateVariableWatchViewModel()
    {
        return new VariableWatchViewModel(eventAggregator);
    }

    private void LoadProjectPythonInterpreter(byte[]? workspaceLayoutData)
    {
        var layoutName = GetPythonInterpreterLayoutName(workspaceLayoutData);
        if (layoutName == null)
        {
            return;
        }

        if (ViewModels.OfType<PythonInterpreterViewModel>().Any())
        {
            return;
        }

        var pythonInterpreterViewModel = CreatePythonInterpreterViewModel();
        pythonInterpreterViewModel.Name = layoutName;
        ViewModels.Add(pythonInterpreterViewModel);
    }

    private void LoadProjectVariableWatch(byte[]? workspaceLayoutData)
    {
        var layoutName = GetVariableWatchLayoutName(workspaceLayoutData);
        if (layoutName == null)
        {
            return;
        }

        if (ViewModels.OfType<VariableWatchViewModel>().Any())
        {
            return;
        }

        var variableWatchViewModel = CreateVariableWatchViewModel();
        variableWatchViewModel.Name = layoutName;
        variableWatchViewModel.Refresh(realProjectData);
        ViewModels.Add(variableWatchViewModel);
    }

    private PythonInterpreterViewModel CreatePythonInterpreterViewModel()
    {
        return new PythonInterpreterViewModel(
            eventAggregator,
            GetPythonInterpreterScriptingContextAsync);
    }

    private static string? GetPythonInterpreterLayoutName(byte[]? workspaceLayoutData)
    {
        if (workspaceLayoutData == null || workspaceLayoutData.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(workspaceLayoutData);
            var document = XDocument.Load(stream);

            var pythonInterpreterPane = document
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Contains("Pane", StringComparison.Ordinal)
                    && IsPythonInterpreterLayoutPane(element));

            return pythonInterpreterPane?.Attribute("SerializationTag")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetVariableWatchLayoutName(byte[]? workspaceLayoutData)
    {
        if (workspaceLayoutData == null || workspaceLayoutData.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(workspaceLayoutData);
            var document = XDocument.Load(stream);

            var variableWatchPane = document
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Contains("Pane", StringComparison.Ordinal)
                    && IsVariableWatchLayoutPane(element));

            return variableWatchPane?.Attribute("SerializationTag")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPythonInterpreterLayoutPane(XElement pane)
    {
        var serializationTag = pane.Attribute("SerializationTag")?.Value ?? string.Empty;
        if (serializationTag.Contains("PythonInterpreter", StringComparison.Ordinal))
        {
            return true;
        }

        var header = pane.Attribute("Header")?.Value ?? string.Empty;
        return pane.Name.LocalName.Equals("QRadDocumentPane", StringComparison.Ordinal)
               && (header.Equals("Python", StringComparison.Ordinal)
                   || header.Equals("Python Interpreter", StringComparison.Ordinal));
    }

    private static bool IsVariableWatchLayoutPane(XElement pane)
    {
        var serializationTag = pane.Attribute("SerializationTag")?.Value ?? string.Empty;
        if (serializationTag.Contains("VariableWatch", StringComparison.Ordinal))
        {
            return true;
        }

        var header = pane.Attribute("Header")?.Value ?? string.Empty;
        return pane.Name.LocalName.Equals("QRadDocumentPane", StringComparison.Ordinal)
               && header.Equals("Variable Watch", StringComparison.Ordinal);
    }

    private static RadPane? FindDockingPaneForViewModel(RadDocking docking, object viewModel)
    {
        return docking
            .Panes
            .FirstOrDefault(pane => ReferenceEquals(pane.DataContext, viewModel));
    }

    private RadPaneGroup? GetTargetDocumentPaneGroup(RadDocking docking)
    {
        if (docking.ActivePane is RadDocumentPane activeDocumentPane)
        {
            var activeGroup = FindParent<RadPaneGroup>(activeDocumentPane);
            if (activeGroup != null && ContainsDocumentPane(activeGroup))
            {
                return activeGroup;
            }
        }

        return FindTopLeftDocumentPaneGroup(docking);
    }

    private async Task MoveNewWorkspaceDocumentToTargetGroupAsync(
        RadDocking docking,
        object viewModel,
        RadPaneGroup? targetGroup)
    {
        try
        {
            if (MoveWorkspaceDocumentToTargetGroup(docking, viewModel, targetGroup))
            {
                return;
            }

            await docking.Dispatcher.InvokeAsync(
                () => MoveWorkspaceDocumentToTargetGroup(docking, viewModel, targetGroup),
                DispatcherPriority.DataBind);
        }
        catch (Exception ex)
        {
            logger.Log(LogLevel.Error, ex.Message);
        }
    }

    private bool MoveWorkspaceDocumentToTargetGroup(RadDocking docking, object viewModel, RadPaneGroup? targetGroup)
    {
        if (FindDockingPaneForViewModel(docking, viewModel) is not RadDocumentPane documentPane)
        {
            return false;
        }

        if (targetGroup != null)
        {
            var currentGroup = FindParent<RadPaneGroup>(documentPane);
            if (!ReferenceEquals(currentGroup, targetGroup))
            {
                documentPane.RemoveFromParent();
                targetGroup.Items.Add(documentPane);
            }
        }

        docking.ActivePane = documentPane;
        documentPane.IsActive = true;
        documentPane.Focus();
        return true;
    }

    private static RadPaneGroup? FindTopLeftDocumentPaneGroup(RadDocking docking)
    {
        return FindVisualChildren<RadPaneGroup>(docking)
            .Where(ContainsDocumentPane)
            .Select(group => new
            {
                Group = group,
                Position = TryGetPosition(docking, group)
            })
            .OrderBy(item => item.Position.HasValue ? 0 : 1)
            .ThenBy(item => item.Position?.Y ?? 0)
            .ThenBy(item => item.Position?.X ?? 0)
            .Select(item => item.Group)
            .FirstOrDefault();
    }

    private static Point? TryGetPosition(RadDocking docking, RadPaneGroup group)
    {
        try
        {
            return group.TransformToAncestor(docking).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool ContainsDocumentPane(RadPaneGroup group)
    {
        return group.Items.OfType<RadDocumentPane>().Any();
    }

    private static T? FindParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        var parent = GetParent(child);
        while (parent != null)
        {
            if (parent is T typedParent)
            {
                return typedParent;
            }

            parent = GetParent(parent);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or Visual3D)
        {
            var visualParent = VisualTreeHelper.GetParent(child);
            if (visualParent != null)
            {
                return visualParent;
            }
        }

        return LogicalTreeHelper.GetParent(child);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private async Task<ScriptingContext?> GetPythonInterpreterScriptingContextAsync()
    {
        if (!ShellWindow.MainAppSettings.ScriptEngine.UsePythonScripts)
        {
            throw new InvalidOperationException("Python scripting is disabled. Enable 'Use Python scripts' in Options -> Preferences -> General.");
        }

        if (IsRuntimeStarted && realProjectData?.Module.Scripting.SharedScope != null)
        {
            return realProjectData.Module.Scripting;
        }

        if (pythonInterpreterStandaloneContext?.SharedScope != null)
        {
            return pythonInterpreterStandaloneContext;
        }

        pythonInterpreterStandaloneContext = new ScriptingContext(ShellWindow.MainAppSettings.ScriptEngine, logger);
        await pythonInterpreterStandaloneContext.InitializeSharedScopeAsync([]);
        return pythonInterpreterStandaloneContext;
    }

    #region Run menu

    private async Task ConnectAsync(object obj)
    {
        // Licensing guard: runtime start requires a valid license token (defense in depth
        // next to CanConnectRuntime; intentionally not a shared helper).
        if (!licenseService.IsRuntimeAllowed)
        {
            logger.Log(LogLevel.Warn, "Runtime start blocked: no valid license.");
            RadWindow.Alert(new DialogParameters
            {
                Header = "License Required",
                Content = "A valid license is required to start measurement.\nOpen Help -> License to enter your license key.",
                Owner = Application.Current.MainWindow,
                DialogStartupLocation = WindowStartupLocation.CenterOwner
            });
            return;
        }

        if (!RuntimeSealValid())
            return;

        // Free-tier guard: limited number of communicated signals (defense in depth next to
        // CanConnectRuntime; the comparison is intentionally inline, not a shared helper).
        if (CommunicatedSignals.GetLimit(licenseService) is { } connectSignalLimit)
        {
            var communicatedCount = CommunicatedSignals.Count(realProjectData?.Module);
            if (communicatedCount > connectSignalLimit)
            {
                logger.Log(LogLevel.Warn,
                    $"Runtime start blocked: {communicatedCount} communicated signals exceed the Free license limit of {connectSignalLimit}.");
                RadWindow.Alert(new DialogParameters
                {
                    Header = "Free License Limit",
                    Content = CommunicatedSignals.BuildOverLimitMessage(communicatedCount, connectSignalLimit)
                              + "\nUncheck 'Communicated' on some variables in Project Configuration -> Variables.",
                    Owner = Application.Current.MainWindow,
                    DialogStartupLocation = WindowStartupLocation.CenterOwner
                });
                return;
            }
        }

        if (!isProjectMade || realProjectData?.Module == null)
        {
            logger.Log(LogLevel.Warn, "No project opened for runtime.");
            return;
        }

        realProjectData.Module.Scripting.IsReplayMode = false;
        if (!EnsurePythonReadyForStart(realProjectData.Module.Scripting))
        {
            return;
        }

        LoadSettingsFromFile(shellRadDocking, runtimeSettingLayoutFile);
        try
        {
            ConfigureDataLoggerFileNames();
            await realProjectData.Module.StartAsync();
            SetRuntimeStartedState(true);
            SetRuntimeCommandStates(true);
            StartRuntimeTimeLimit();
        }
        catch (Exception e)
        {
            SetRuntimeStartedState(false);
            SetRuntimeCommandStates(false);
            logger.Log(LogLevel.Error, e.Message);
        }
    }

    private async Task DisconnectAsync(object obj)
    {
        if (isReplayMode)
        {
            await StopReplayAsync(obj);
            return;
        }

        LoadSettingsFromFile(shellRadDocking, editModeSettingLayoutFile);
        try
        {
            await realProjectData.Module.StopAsync();
            SetRuntimeStartedState(false);
            SetRuntimeCommandStates(false);
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, e.Message);
        }
    }

    private void ConfigureDataLoggerFileNames()
    {
        var projectFileName = GetProjectDataLogFileName();
        foreach (var driver in realProjectData.Module.Drivers.OfType<IDataLogFileNameDriver>())
        {
            driver.DataLogFileName = projectFileName;
        }
    }

    private string GetProjectDataLogFileName()
    {
        var projectName = !string.IsNullOrWhiteSpace(currentProjectFilePath)
            ? Path.GetFileNameWithoutExtension(currentProjectFilePath)
            : realProjectData.Module.Specification.Name;

        projectName = SanitizeFileName(projectName);
        return string.IsNullOrWhiteSpace(projectName) ? "values" : projectName;
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(fileName
            .Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
    }
    
    #endregion

    #region Options
    
    private void LoadSettingsFromFile(RadDocking radDocking, string settingsLayoutFile)
    {
        try
        {
            if (!File.Exists(settingsLayoutFile)) throw new FileNotFoundException($"Settings layout file \"{settingsLayoutFile}\" not found.");
            using var stream = new FileStream(settingsLayoutFile, FileMode.Open);
            //radDocking.Tag = "Settings";
            radDocking.LoadLayout(stream);
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Warn, $"Open settings file {settingsLayoutFile} failed.", e);
        }
    } 

    private void LoadWorkspaceLayoutFromData(byte[]? workspaceLayoutData, bool workspaceLoadFailed = false)
    {
        if (workspaceLayoutData == null || workspaceLayoutData.Length == 0)
        {
            return;
        }

        var previousRequireSerializationTag = requireSerializationTag;
        try
        {
            requireSerializationTag = true;
            using var stream = new MemoryStream(workspaceLayoutData);
            shellRadDocking.LoadLayout(stream);
        }
        catch (Exception e)
        {
            if (workspaceLoadFailed)
            {
                // Expected cascade: the layout references panes of a workspace that was
                // skipped; the single closing "opened with issue(s)" line names the cause.
                return;
            }

            logger.Log(LogLevel.Warn, "Open workspace layout from project file failed.", e);
        }
        finally
        {
            requireSerializationTag = previousRequireSerializationTag;
        }
    }

    private async Task ReplayAsync(object obj)
    {
        // Licensing guard: replay requires a valid license token (defense in depth next to
        // CanStartReplay; intentionally not a shared helper).
        if (!licenseService.IsRuntimeAllowed)
        {
            logger.Log(LogLevel.Warn, "Replay start blocked: no valid license.");
            RadWindow.Alert(new DialogParameters
            {
                Header = "License Required",
                Content = "A valid license is required to start replay.\nOpen Help -> License to enter your license key.",
                Owner = Application.Current.MainWindow,
                DialogStartupLocation = WindowStartupLocation.CenterOwner
            });
            return;
        }

        // Free-tier guard: limited number of communicated signals (defense in depth next to
        // CanStartReplay; the comparison is intentionally inline, not a shared helper).
        if (CommunicatedSignals.GetLimit(licenseService) is { } replaySignalLimit)
        {
            var communicatedCount = CommunicatedSignals.Count(realProjectData?.Module);
            if (communicatedCount > replaySignalLimit)
            {
                logger.Log(LogLevel.Warn,
                    $"Replay start blocked: {communicatedCount} communicated signals exceed the Free license limit of {replaySignalLimit}.");
                RadWindow.Alert(new DialogParameters
                {
                    Header = "Free License Limit",
                    Content = CommunicatedSignals.BuildOverLimitMessage(communicatedCount, replaySignalLimit)
                              + "\nUncheck 'Communicated' on some variables in Project Configuration -> Variables.",
                    Owner = Application.Current.MainWindow,
                    DialogStartupLocation = WindowStartupLocation.CenterOwner
                });
                return;
            }
        }

        if (!isProjectMade)
        {
            logger.Log(LogLevel.Warn, "No project opened for replay.");
            return;
        }

        if (IsRuntimeStarted)
        {
            logger.Log(LogLevel.Warn, "Stop runtime before starting replay.");
            return;
        }

        if (!isReplayDataLogImported || string.IsNullOrWhiteSpace(replayDataLogFilePath) || !File.Exists(replayDataLogFilePath))
        {
            logger.Log(LogLevel.Warn, "Import a data log before starting replay.");
            return;
        }

        var replayDriver = FindReplayDriver();
        if (replayDriver == null)
        {
            logger.Log(LogLevel.Warn, "No FileDataReplayDriver found. Import a data log first.");
            return;
        }

        var replayProtocol = FindReplayProtocol(replayDriver);
        if (replayProtocol == null)
        {
            logger.Log(LogLevel.Warn, "No DataLogReplayProtocol found in replay driver.");
            return;
        }

        realProjectData.Module.Scripting.IsReplayMode = true;
        if (!EnsurePythonReadyForStart(realProjectData.Module.Scripting))
        {
            realProjectData.Module.Scripting.IsReplayMode = false;
            return;
        }

        try
        {
            SaveReplayStates();
            ApplyReplayStates(replayDriver, replayProtocol);
            RefreshCommunicatedDriversProperties();
            SubscribeReplayCompleted(replayDriver);
            LoadSettingsFromFile(shellRadDocking, runtimeSettingLayoutFile);
            RebindWorkspaceControlVariables(GetReplayControlVariables(replayProtocol));
            SetRuntimeStartedState(true);
            isReplayMode = true;
            realProjectData.Module.Scripting.IsReplayMode = true;
            SetRuntimeCommandStates(true, true);
            SetReplayControlEnabled(true);
            await realProjectData.Module.StartAsync();

            if (!isReplayMode)
            {
                return;
            }

            solutionExplorerViewModel.ReloadProjectData(realProjectData, preserveWorkspaces: true);
            logger.Log(LogLevel.Info, "Replay started.");
        }
        catch (Exception e)
        {
            UnsubscribeReplayCompleted();
            RestoreReplayStates();
            LoadSettingsFromFile(shellRadDocking, editModeSettingLayoutFile);
            RebindWorkspaceControlVariables(GetProjectProtocolVariables());
            SetRuntimeStartedState(false);
            isReplayMode = false;
            realProjectData.Module.Scripting.IsReplayMode = false;
            SetRuntimeCommandStates(false);
            SetReplayControlEnabled(false);
            logger.Log(LogLevel.Error, $"Replay start failed: {e.Message}");
        }
    }

    private async Task StopReplayAsync(object obj)
    {
        if (!isReplayMode)
        {
            logger.Log(LogLevel.Warn, "Replay is not running.");
            return;
        }

        var stopFailed = false;

        try
        {
            if (IsRuntimeStarted)
            {
                await realProjectData.Module.StopAsync();
            }
        }
        catch (Exception e)
        {
            stopFailed = true;
            logger.Log(LogLevel.Error, $"Replay runtime stop failed: {e.Message}");
        }
        finally
        {
            SetRuntimeStartedState(false);
            isReplayMode = false;
            realProjectData.Module.Scripting.IsReplayMode = false;
            var keepReplayProgressSubscription = activeReplayDriver?.IsDataLoading == true;
            SetRuntimeCommandStates(false);
            if (!keepReplayProgressSubscription)
            {
                UnsubscribeReplayCompleted();
            }

            SetReplayControlEnabled(false);
            RestoreReplayStates();
            RefreshCommunicatedDriversProperties();
            LoadSettingsFromFile(shellRadDocking, editModeSettingLayoutFile);
            RebindWorkspaceControlVariables(GetProjectProtocolVariables());
            solutionExplorerViewModel.ReloadProjectData(realProjectData, preserveWorkspaces: true);

            if (!stopFailed)
            {
                logger.Log(LogLevel.Info, "Replay stopped.");
            }
        }
    }

    private async Task ImportDataLogAsync(object obj)
    {
        if (!isProjectMade)
        {
            logger.Log(LogLevel.Warn, "No project opened for data log import.");
            return;
        }

        if (IsRuntimeStarted)
        {
            logger.Log(LogLevel.Warn, "Stop runtime before importing a data log.");
            return;
        }

        var initialDirectory = GetInitialDialogDirectory(lastDataLogDialogDirectory, replayDataLogFilePath);
        var dlg = importDataLogDialog ??= CreateOpenFileDialog(
            "QSuite data log files (*.qilog)|*.qilog|Legacy MessagePack logs (*.msgpack)|*.msgpack|All files (*.*)|*.*",
            initialDirectory);
        PrepareFileDialog(dlg, initialDirectory);

        dlg.ShowDialog();
        if (dlg.DialogResult != true)
        {
            return;
        }

        try
        {
            lastDataLogDialogDirectory = Path.GetDirectoryName(dlg.FileName);
            var existingReplayDriver = realProjectData.Module.Drivers.FirstOrDefault(driver =>
                driver.Specification.Name.Equals("FileDataReplayDriver", StringComparison.OrdinalIgnoreCase));
            var replayDriver = CreateFileDataReplayDriver(dlg.FileName, existingReplayDriver?.RawSettings);
            if (existingReplayDriver != null)
            {
                if (existingReplayDriver is IReplayDriver existingReplayDriverWithEvents
                    && ReferenceEquals(activeReplayDriver, existingReplayDriverWithEvents))
                {
                    UnsubscribeReplayCompleted();
                }

                existingReplayDriver.Dispose();
                realProjectData.Module.RemoveDriver(existingReplayDriver);
            }

            realProjectData.Module.AddDriver(replayDriver);
            StartReplayDataLoad(replayDriver);
            solutionExplorerViewModel.ReloadProjectData(realProjectData, preserveWorkspaces: true);
            SetReplayDataLogImportState(dlg.FileName);
            RefreshVariableWatch();
            RibbonExportDataLogCommand.OnCanExecuteChanged();
            RibbonReplayCommand.OnCanExecuteChanged();
            logger.Log(LogLevel.Info, $"Data log \"{Path.GetFileName(dlg.FileName)}\" imported for replay.");
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, $"Data log import failed: {e.Message}");
        }

        await Task.CompletedTask;
    }

    private async Task ExportDataLogAsync(object obj)
    {
        if (!CanExportDataLog())
        {
            logger.Log(LogLevel.Warn, "No data log imported for export.");
            return;
        }

        var initialDirectory = GetInitialDialogDirectory(lastDataLogDialogDirectory, replayDataLogFilePath);
        var dlg = exportDataLogDialog ??= CreateSaveFileDialog("CSV files (*.csv)|*.csv|All files (*.*)|*.*", initialDirectory);
        PrepareFileDialog(dlg, initialDirectory);
        dlg.FileName = GetDefaultDataLogCsvFileName();

        dlg.ShowDialog();
        if (dlg.DialogResult != true)
        {
            return;
        }

        try
        {
            var exportFilePath = EnsureFileExtension(dlg.FileName, ".csv");
            lastDataLogDialogDirectory = Path.GetDirectoryName(exportFilePath);
            var replayDriver = FindReplayDriver();
            if (replayDriver is not IDataLogCsvExportDriver exportDriver)
            {
                logger.Log(LogLevel.Warn, "Replay driver does not support CSV export.");
                return;
            }

            await exportDriver.ExportCsvAsync(exportFilePath);
            logger.Log(LogLevel.Info, $"Data log exported to \"{Path.GetFileName(exportFilePath)}\".");
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, $"Data log export failed: {e.Message}", e);
        }
    }

    private static RadOpenFileDialog CreateOpenFileDialog(string filter, string initialDirectory)
    {
        var dialog = new RadOpenFileDialog()
        {
            Owner = App.Current.MainWindow,
            Multiselect = false,
            Filter = filter,
            InitialDirectory = initialDirectory
        };

        FileDialogConfiguration.ConfigureFastFileDialog(dialog);
        return dialog;
    }

    private static RadSaveFileDialog CreateSaveFileDialog(string filter, string initialDirectory)
    {
        var dialog = new RadSaveFileDialog()
        {
            Owner = App.Current.MainWindow,
            Filter = filter,
            InitialDirectory = initialDirectory
        };

        FileDialogConfiguration.ConfigureFastFileDialog(dialog);
        return dialog;
    }

    private static void PrepareFileDialog(DialogWindowBase dialog, string initialDirectory)
    {
        FileDialogConfiguration.PrepareFileDialog(dialog, initialDirectory);
    }

    private static void ConfigureFastFileDialog(DialogWindowBase dialog)
    {
        FileDialogConfiguration.ConfigureFastFileDialog(dialog);
    }

    private static string GetInitialDialogDirectory(string? lastDirectory, string? currentFilePath)
    {
        return FileDialogConfiguration.GetInitialDialogDirectory(lastDirectory, currentFilePath);
    }

    private string GetDefaultDataLogCsvFileName()
    {
        if (!string.IsNullOrWhiteSpace(replayDataLogFilePath))
        {
            return $"{Path.GetFileNameWithoutExtension(replayDataLogFilePath)}.csv";
        }

        return $"{GetProjectDataLogFileName()}.csv";
    }

    private static string EnsureFileExtension(string filePath, string extension)
    {
        return Path.GetExtension(filePath).Equals(extension, StringComparison.OrdinalIgnoreCase)
            ? filePath
            : $"{filePath}{extension}";
    }

    private void StartReplayDataLoad(IDriverBase replayDriver)
    {
        if (replayDriver is not IReplayDriver replayDriverWithDataLoad)
        {
            return;
        }

        SubscribeReplayCompleted(replayDriver);
        replayDriverWithDataLoad.StartLoadingData();
        logger.Log(LogLevel.Info, "Replay data loading started.");
        RibbonCancelReplayDataLoadCommand.OnCanExecuteChanged();
    }

    private void CancelReplayDataLoad()
    {
        if (activeReplayDriver?.IsDataLoading != true)
        {
            return;
        }

        activeReplayDriver.CancelLoadingData();
        RibbonCancelReplayDataLoadCommand.OnCanExecuteChanged();
    }

    private void SaveLayout(RadDocking radDocking, string? filePrep = null)
    {
        var settingsLayoutFile = IsRuntimeStarted ? runtimeSettingLayoutFile : editModeSettingLayoutFile;
        if (filePrep != null)
        {
            settingsLayoutFile = Path.Combine(
                Path.GetDirectoryName(settingsLayoutFile) ?? string.Empty,
                $"{filePrep}_{Path.GetFileName(settingsLayoutFile)}");
        }
        
        try
        {
            using var stream = new FileStream(settingsLayoutFile, FileMode.Create);
            //radDocking.Tag = "Settings";
            radDocking.SaveLayout(stream);
            
            logger.Log(LogLevel.Info, $"Layout settings saved.");
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, $"Save settings file {settingsLayoutFile} failed.", e);
        }
    }

    private List<WorkspaceProjectData> CreateWorkspaceProjectData()
    {
        return ViewModels
            .OfType<WorkspaceViewModel>()
            .Select(WorkspaceProjectData.FromWorkspace)
            .ToList();
    }

    private List<ScriptDocumentProjectData> CreateScriptDocumentProjectData()
    {
        return ViewModels
            .OfType<ScriptViewModel>()
            .Select(ScriptDocumentProjectData.FromScriptViewModel)
            .ToList();
    }

    private IEnumerable<IProtocolVariable> GetProjectProtocolVariables()
    {
        return realProjectData.Module.Drivers
            .Where(IsLiveSourceDriver)
            .SelectMany(driver => driver.Protocols)
            .SelectMany(protocol => protocol.Variables);
    }

    private static bool IsLiveSourceDriver(IDriverBase driver)
    {
        return driver is not IProtocolVariableSinkDriver
               && driver is not IReplayDriver;
    }

    private IDriverBase CreateFileDataReplayDriver(string logFilePath, string? previousRawSettings)
    {
        var driverDetails = driverPlugins.FirstOrDefault(plugin =>
            plugin.Name.Equals("FileDataReplayDriver", StringComparison.OrdinalIgnoreCase));
        if (driverDetails == null)
        {
            throw new InvalidOperationException("FileDataReplayDriver plugin was not found.");
        }

        var protocolDetails = protocolPlugins.FirstOrDefault(plugin =>
            plugin.Name.Equals("DataLogReplayProtocol", StringComparison.OrdinalIgnoreCase));
        if (protocolDetails == null)
        {
            throw new InvalidOperationException("DataLogReplayProtocol plugin was not found.");
        }

        var driver = pluginLoader.LoadPlugin<IDriverBase>(driverDetails.PathName)
            ?? throw new InvalidOperationException("FileDataReplayDriver could not be loaded.");
        var protocol = pluginLoader.LoadPlugin<IProtocolBase>(protocolDetails.PathName)
            ?? throw new InvalidOperationException("DataLogReplayProtocol could not be loaded.");

        driver.Label = $"Replay: {Path.GetFileName(logFilePath)}";
        driver.IsEnabled = false;
        driver.RawSettings = BuildReplayRawSettings(logFilePath, previousRawSettings);
        driver.SetConfiguration();

        protocol.IsEnabled = false;
        protocol.RawSettings = string.Empty;
        foreach (var variable in realProjectData.Module.Variables)
        {
            var protocolVariable = protocol.CreateProtocolVariable(variable, string.Empty, true);
            if (protocolVariable != null)
            {
                protocol.AddVariable(protocolVariable);
            }
        }

        protocol.SetConfiguration();
        driver.AddProtocol(protocol);

        return driver;
    }

    // Import always points the driver at the newly chosen file but keeps the operator's
    // mode/speed/loop tuning from the previous replay driver, if there was one.
    private static string BuildReplayRawSettings(string logFilePath, string? previousRawSettings)
    {
        var mode = "realtime";
        var speed = "1";
        var loop = "false";

        foreach (var part in (previousRawSettings ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0].Equals("mode", StringComparison.OrdinalIgnoreCase))
            {
                mode = pair[1];
            }
            else if (pair[0].Equals("speed", StringComparison.OrdinalIgnoreCase))
            {
                speed = pair[1];
            }
            else if (pair[0].Equals("loop", StringComparison.OrdinalIgnoreCase))
            {
                loop = pair[1];
            }
        }

        return $"file={logFilePath};mode={mode};speed={speed};loop={loop}";
    }

    private IDriverBase? FindReplayDriver()
    {
        return realProjectData.Module.Drivers.FirstOrDefault(driver =>
            driver.Specification.Name.Equals("FileDataReplayDriver", StringComparison.OrdinalIgnoreCase));
    }

    private static IProtocolBase? FindReplayProtocol(IDriverBase replayDriver)
    {
        return replayDriver.Protocols.FirstOrDefault(protocol =>
            protocol.Specification.Name.Equals("DataLogReplayProtocol", StringComparison.OrdinalIgnoreCase));
    }

    private void SaveReplayStates()
    {
        replayDriverStates = realProjectData.Module.Drivers
            .Select(driver => (driver, driver.IsEnabled))
            .ToList();
        replayProtocolStates = realProjectData.Module.Drivers
            .SelectMany(driver => driver.Protocols)
            .Select(protocol => (protocol, protocol.IsEnabled))
            .ToList();
    }

    private void ApplyReplayStates(IDriverBase replayDriver, IProtocolBase replayProtocol)
    {
        // Collected before the flags are overwritten below.
        var scriptWrittenProtocols = GetReplayScriptWrittenProtocols();

        foreach (var driver in realProjectData.Module.Drivers)
        {
            driver.IsEnabled = ReferenceEquals(driver, replayDriver)
                               || driver.Protocols.Any(scriptWrittenProtocols.Contains);
        }

        foreach (var protocol in realProjectData.Module.Drivers.SelectMany(driver => driver.Protocols))
        {
            protocol.IsEnabled = ReferenceEquals(protocol, replayProtocol)
                                 || scriptWrittenProtocols.Contains(protocol);
        }

    }

    // Protocols publishing script-computed variables (IScriptWriteAwareProtocol, e.g. Virtual
    // Variables) keep running during replay, so scripts enabled for replay can be tested over a
    // recorded data log. Only those the user has enabled (protocol and its driver); every other
    // live source stays off so it cannot mix with the replayed data.
    private HashSet<IProtocolBase> GetReplayScriptWrittenProtocols()
    {
        return realProjectData.Module.Drivers
            .Where(driver => driver.IsEnabled && IsLiveSourceDriver(driver))
            .SelectMany(driver => driver.Protocols)
            .Where(protocol => protocol.IsEnabled && protocol is IScriptWriteAwareProtocol)
            .ToHashSet();
    }

    // Replayed variables plus the script-computed ones. Membership in the replay protocol says
    // nothing about the data: a data log import adds EVERY project variable to it. A
    // script-computed variable is therefore taken from the recording only when the project logs
    // it ("Log to FileDataLogger"); otherwise the control is bound to the script-written
    // protocol variable, the only one that publishes samples for it during replay.
    private List<IProtocolVariable> GetReplayControlVariables(IProtocolBase replayProtocol)
    {
        var loggedVariableIds = realProjectData.Module.Drivers
            .Where(driver => driver is IProtocolVariableSinkDriver)
            .SelectMany(driver => driver.Protocols)
            .SelectMany(protocol => protocol.Variables)
            .Where(protocolVariable => protocolVariable.IsCommunicated)
            .Select(protocolVariable => protocolVariable.Variable.Id)
            .ToHashSet();

        var scriptComputedVariables = GetReplayScriptWrittenProtocols()
            .SelectMany(protocol => protocol.Variables)
            .Where(protocolVariable => protocolVariable.IsCommunicated
                                       && !loggedVariableIds.Contains(protocolVariable.Variable.Id))
            .ToList();
        var scriptComputedVariableIds = scriptComputedVariables
            .Select(protocolVariable => protocolVariable.Variable.Id)
            .ToHashSet();

        return replayProtocol.Variables
            .Where(protocolVariable => !scriptComputedVariableIds.Contains(protocolVariable.Variable.Id))
            .Concat(scriptComputedVariables)
            .ToList();
    }

    private void RestoreReplayStates()
    {
        if (replayDriverStates != null)
        {
            foreach (var (driver, isEnabled) in replayDriverStates)
            {
                driver.IsEnabled = isEnabled;
            }
        }

        if (replayProtocolStates != null)
        {
            foreach (var (protocol, isEnabled) in replayProtocolStates)
            {
                protocol.IsEnabled = isEnabled;
            }
        }

        replayDriverStates = null;
        replayProtocolStates = null;
    }

    private void RefreshCommunicatedDriversProperties()
    {
        if (propertiesViewModel.SelectedViewModel is CommunicatedDriversPropertiesViewModel driversPropertiesViewModel)
        {
            driversPropertiesViewModel.RefreshDrivers();
        }

        if (propertiesViewModel.SelectedViewModel is DriverPropertiesViewModel driverPropertiesViewModel)
        {
            driverPropertiesViewModel.RefreshDriver();
        }

        if (propertiesViewModel.SelectedViewModel is CommunicatedProtocolsPropertiesViewModel protocolsPropertiesViewModel)
        {
            protocolsPropertiesViewModel.RefreshProtocols();
        }
    }

    private void RefreshProjectConfigurationAppliedProperties()
    {
        RefreshCommunicatedDriversProperties();
        RefreshScriptProperties();
        RebindWorkspaceControlVariables(GetProjectProtocolVariables());
        RefreshVariableWatch();
        solutionExplorerViewModel.ReloadProjectData(realProjectData, preserveWorkspaces: true);

        switch (propertiesViewModel.SelectedViewModel)
        {
            case ProjectPropertiesViewModel projectPropertiesViewModel:
                projectPropertiesViewModel.RefreshProperties();
                break;
            case VariablePropertiesViewModel variablePropertiesViewModel:
                variablePropertiesViewModel.RefreshProperties();
                break;
            case ReadOnlyVariablePropertiesViewModel readOnlyVariablePropertiesViewModel:
                readOnlyVariablePropertiesViewModel.RefreshProperties();
                break;
            case ConversionPropertiesViewModel conversionPropertiesViewModel:
                conversionPropertiesViewModel.RefreshProperties();
                break;
            case PresentationPropertiesViewModel presentationPropertiesViewModel:
                presentationPropertiesViewModel.RefreshProperties();
                break;
            case EventPropertiesViewModel eventPropertiesViewModel:
                eventPropertiesViewModel.RefreshProperties();
                break;
        }
    }

    private void RefreshScriptProperties()
    {
        if (propertiesViewModel.SelectedViewModel is ScriptPropertiesViewModel scriptPropertiesViewModel)
        {
            scriptPropertiesViewModel.RefreshScript();
        }

        foreach (var scriptViewModel in ViewModels.OfType<ScriptViewModel>())
        {
            scriptViewModel.ScriptWrapper.RefreshProperties();
        }
    }

    private void RebindWorkspaceControlVariables(IEnumerable<IProtocolVariable> protocolVariables)
    {
        var protocolVariablesList = protocolVariables.ToList();
        foreach (var workspaceViewModel in ViewModels.OfType<WorkspaceViewModel>())
        {
            workspaceViewModel.BindLoadedControlVariables(protocolVariablesList);
        }
    }

    // Replay seek: every open workspace (same set as RebindWorkspaceControlVariables).
    private void ResetWorkspaceControlTimelines()
    {
        foreach (var workspaceViewModel in ViewModels.OfType<WorkspaceViewModel>())
        {
            workspaceViewModel.ResetControlTimelines();
        }
    }

    private void SetRuntimeStartedState(bool runtimeStarted)
    {
        if (!runtimeStarted)
        {
            StopRuntimeTimeLimit();
        }

        if (IsRuntimeStarted != runtimeStarted)
        {
            IsRuntimeStarted = runtimeStarted;
            OnPropertyChanged(nameof(IsRuntimeStarted));
        }

        foreach (var workspaceViewModel in ViewModels.OfType<WorkspaceViewModel>())
        {
            workspaceViewModel.SetControlsRunState(runtimeStarted);
        }

        (propertiesViewModel?.SelectedViewModel as DriverPropertiesViewModel)?.RefreshDriver();

        foreach (var scriptViewModel in ViewModels.OfType<ScriptViewModel>())
        {
            scriptViewModel.IsRuntimeRunning = runtimeStarted;
        }
    }

    /// <summary>Free/Trial runtime limit (CEO decision 2026-09-12: the QFW SDK evaluation limit
    /// moved into QInsight). One timer per Start; when it elapses the runtime is stopped exactly
    /// like the ribbon Stop and only an Info line is logged - Start is allowed again. Replay is
    /// not limited. Commercial never starts the timer. The tier check is intentionally inline
    /// next to the other license guards (no shared patchable helper).</summary>
    private void StartRuntimeTimeLimit()
    {
        StopRuntimeTimeLimit();
        if (!licenseService.IsRuntimeTimeLimited)
        {
            return;
        }

        var limit = LicensingConstants.FreeTrialRuntimeLimit;
        runtimeTimeLimitTimer = new DispatcherTimer { Interval = limit };
        runtimeTimeLimitTimer.Tick += OnRuntimeTimeLimitElapsed;
        runtimeTimeLimitTimer.Start();
        logger.Log(LogLevel.Info,
            $"{RuntimeTimeLimitTierName} license: measurement runs for {limit.TotalMinutes:0} minutes per start, then QInsight returns to edit mode.");
    }

    private void StopRuntimeTimeLimit()
    {
        if (runtimeTimeLimitTimer is null)
        {
            return;
        }

        runtimeTimeLimitTimer.Stop();
        runtimeTimeLimitTimer.Tick -= OnRuntimeTimeLimitElapsed;
        runtimeTimeLimitTimer = null;
    }

    private async void OnRuntimeTimeLimitElapsed(object? sender, EventArgs e)
    {
        StopRuntimeTimeLimit();
        if (!IsRuntimeStarted || isReplayMode)
        {
            return;
        }

        logger.Log(LogLevel.Info,
            $"{RuntimeTimeLimitTierName} license: the {LicensingConstants.FreeTrialRuntimeLimit.TotalMinutes:0}-minute runtime limit was reached, switching to edit mode. "
            + "Press Start to measure again, or upgrade to a Commercial license for unlimited runtime.");
        try
        {
            await DisconnectAsync(shellRadDocking);
        }
        catch (Exception ex)
        {
            logger.Log(LogLevel.Error, ex.Message);
        }
    }

    private string RuntimeTimeLimitTierName => licenseService.CurrentClaims?.Tier ?? "Free";

    private void RefreshVariableWatch()
    {
        foreach (var variableWatchViewModel in ViewModels.OfType<VariableWatchViewModel>())
        {
            variableWatchViewModel.Refresh(realProjectData);
        }
    }

    private void SubscribeReplayCompleted(IDriverBase replayDriver)
    {
        if (replayDriver is not IReplayDriver replayDriverWithEvents)
        {
            logger.Log(LogLevel.Warn,
                $"Replay driver \"{replayDriver.Specification.Name}\" does not implement IReplayDriver — natural end-of-replay will not be detected.");
            return;
        }

        if (ReferenceEquals(activeReplayDriver, replayDriverWithEvents))
        {
            UpdateReplayProgress(
                activeReplayDriver.CurrentTime,
                activeReplayDriver.Duration,
                activeReplayDriver.IsPaused,
                activeReplayDriver.IsDataLoaded);
            return;
        }

        UnsubscribeReplayCompleted();
        activeReplayDriver = replayDriverWithEvents;
        activeReplayDriver.ReplayCompleted += OnReplayCompleted;
        activeReplayDriver.ReplayProgressChanged += OnReplayProgressChanged;
        UpdateReplayProgress(
            activeReplayDriver.CurrentTime,
            activeReplayDriver.Duration,
            activeReplayDriver.IsPaused,
            activeReplayDriver.IsDataLoaded);
    }

    private void UnsubscribeReplayCompleted()
    {
        if (activeReplayDriver == null)
        {
            return;
        }

        activeReplayDriver.ReplayCompleted -= OnReplayCompleted;
        activeReplayDriver.ReplayProgressChanged -= OnReplayProgressChanged;
        activeReplayDriver = null;
    }

    private async void OnReplayCompleted(object? sender, EventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                logger.Log(LogLevel.Info, "Replay completed — stopping replay session.");
                if (isReplayMode)
                {
                    await StopReplayAsync(shellRadDocking);
                }
            }).Task.Unwrap();
        }
        catch (Exception ex)
        {
            logger.Log(LogLevel.Error, $"Replay completion handling failed: {ex.Message}", ex);
        }
    }

    private async void OnReplayProgressChanged(object? sender, ReplayProgressChangedEventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateReplayProgress(e.CurrentTime, e.Duration, e.IsPaused, e.IsDataLoaded);
                RibbonReplayCommand.OnCanExecuteChanged();
                RibbonExportDataLogCommand.OnCanExecuteChanged();
                RibbonCancelReplayDataLoadCommand.OnCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            logger.Log(LogLevel.Error, $"Replay progress update failed: {ex.Message}", ex);
        }
    }

    private void ToggleReplayPause()
    {
        if (activeReplayDriver == null)
        {
            return;
        }

        if (activeReplayDriver.IsPaused)
        {
            activeReplayDriver.Resume();
        }
        else
        {
            activeReplayDriver.Pause();
        }
    }

    private async Task SeekReplayPositionAsync(double seconds, int requestVersion)
    {
        await Task.Delay(150);
        if (requestVersion != replaySeekRequestVersion
            || activeReplayDriver == null
            || !isReplayMode
            || !activeReplayDriver.IsDataLoaded
            || !activeReplayDriver.IsPaused)
        {
            return;
        }

        try
        {
            // The driver re-sends the whole history up to the new position (every variable
            // gets its value valid there), so controls with a timeline must forget the old
            // position first — queued stale samples included — before any re-sent sample
            // can arrive. Continuation of the Task.Delay above: still the UI thread.
            ResetWorkspaceControlTimelines();

            // The history re-sent up to the new position only restores the variable values and
            // the charts — it is not a run of the data, so value-changed scripts stay silent and
            // continue from the new position when the replay is resumed. The overload guard is
            // reset as well, so nothing observed around the seek delays the first script call.
            var scripting = realProjectData.Module.Scripting;
            scripting.SuppressValueChangedScripts = true;
            try
            {
                await activeReplayDriver.SeekAsync(TimeSpan.FromSeconds(seconds));
            }
            finally
            {
                scripting.SuppressValueChangedScripts = false;
                scripting.ResetScriptOverloadGuard();
            }
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, $"Replay seek failed: {e.Message}", e);
        }
    }

    private void SetReplayControlEnabled(bool isEnabled)
    {
        IsReplayControlEnabled = isEnabled;
        if (!isEnabled)
        {
            if (activeReplayDriver?.IsDataLoading == true)
            {
                UpdateReplayProgress(
                    activeReplayDriver.CurrentTime,
                    activeReplayDriver.Duration,
                    activeReplayDriver.IsPaused,
                    activeReplayDriver.IsDataLoaded);
            }
            else
            {
                // Stop replay: rewind the position only. The imported data log is still loaded in the
                // replay driver, so its duration stays on the timeline until the import is cleared
                // (ClearReplayDataLogImportState) or replaced by a new import.
                UpdateReplayProgress(TimeSpan.Zero, TimeSpan.FromSeconds(ReplayDurationSeconds), false, false);
            }
        }

        RibbonReplayPauseResumeCommand.OnCanExecuteChanged();
    }

    private void UpdateReplayProgress(TimeSpan currentTime, TimeSpan duration, bool isPaused, bool isDataLoaded)
    {
        isUpdatingReplayPositionFromDriver = true;
        try
        {
            ReplayDurationSeconds = duration.TotalSeconds;
            ReplayPositionSeconds = currentTime.TotalSeconds;
            ReplayPauseResumeText = isPaused ? "Resume" : "Pause";
            IsReplaySeekEnabled = IsReplayControlEnabled && isDataLoaded && isPaused;
        }
        finally
        {
            isUpdatingReplayPositionFromDriver = false;
        }
    }

    private static string FormatReplayTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss\.fff")
            : time.ToString(@"mm\:ss\.fff");
    }

    private void SetReplayPositionText(double seconds)
    {
        isUpdatingReplayPositionTextFromPosition = true;
        try
        {
            ReplayPositionText = FormatReplayTime(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            isUpdatingReplayPositionTextFromPosition = false;
        }
    }

    private static bool TryParseReplayTime(string value, out TimeSpan time)
    {
        var replayTimeText = value.Trim();
        var formats = new[]
        {
            @"h\:mm\:ss\.fff",
            @"h\:mm\:ss\.ff",
            @"h\:mm\:ss\.f",
            @"hh\:mm\:ss\.fff",
            @"hh\:mm\:ss\.ff",
            @"hh\:mm\:ss\.f",
            @"h\:mm\:ss",
            @"hh\:mm\:ss",
            @"m\:ss\.fff",
            @"m\:ss\.ff",
            @"m\:ss\.f",
            @"mm\:ss\.fff",
            @"mm\:ss\.ff",
            @"mm\:ss\.f",
            @"m\:ss",
            @"mm\:ss"
        };

        if (TimeSpan.TryParseExact(replayTimeText, formats, CultureInfo.InvariantCulture, out time))
        {
            return true;
        }

        if (double.TryParse(replayTimeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            time = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return false;
    }

    private MemoryStream CreateWorkspaceLayoutStream(RadDocking radDocking)
    {
        var stream = new MemoryStream();
        var previousRequireSerializationTag = requireSerializationTag;

        try
        {
            requireSerializationTag = true;
            radDocking.SaveLayout(stream);
            stream.Position = 0;
            return stream;
        }
        finally
        {
            requireSerializationTag = previousRequireSerializationTag;
        }
    }

    #endregion

	#endregion
    
    #region Can-methods process

    private void ChangeIsProjectMade(bool isMade)
    {
        isProjectMade = isMade;
        NotifyProjectCommandsCanExecuteChanged();
        NotifyRuntimeCommandsCanExecuteChanged();
        UpdateLicenseBadge();
        LogCommunicatedSignalsOverLimit();
    }

    /// <summary>Free-tier limit: make an over-limit project visible right when it is opened —
    /// the greyed-out Connect/Replay buttons alone do not explain themselves.</summary>
    private void LogCommunicatedSignalsOverLimit()
    {
        if (!isProjectMade || CommunicatedSignals.GetLimit(licenseService) is not { } signalLimit)
        {
            return;
        }

        var communicatedCount = CommunicatedSignals.Count(realProjectData?.Module);
        if (communicatedCount > signalLimit)
        {
            logger.Log(LogLevel.Warn,
                $"Project has {communicatedCount} communicated signals, the Free license limit is {signalLimit} — "
                + "runtime and replay are disabled. Uncheck 'Communicated' on some variables in "
                + "Project Configuration -> Variables.");
        }
    }

    private void SetRuntimeCommandStates(bool runtimeStarted, bool replayStarted = false)
    {
        canUseHomeRibbon = !runtimeStarted;
        canConnectRuntime = !runtimeStarted;
        canDisconnectRuntime = runtimeStarted && !replayStarted;
        canImportDataLog = !runtimeStarted;
        canExportDataLog = !runtimeStarted;
        canReplay = !runtimeStarted;
        canStopReplay = replayStarted;

        RibbonNewProjectCommand.OnCanExecuteChanged();
        RibbonOpenProjectCommand.OnCanExecuteChanged();
        RibbonSaveProjectCommand.OnCanExecuteChanged();
        RibbonSaveProjectAsCommand.OnCanExecuteChanged();
        RibbonCloseProjectCommand.OnCanExecuteChanged();
        RibbonProjectPasswordCommand.OnCanExecuteChanged();
        RibbonAddWorkspaceCommand.OnCanExecuteChanged();
        RibbonRemoveWorkspaceCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationProjectCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationDriversCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationVariablesCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationConversionsCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationPresentationsCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationEventsCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationScriptsCommand.OnCanExecuteChanged();
        RibbonConnectCommand.OnCanExecuteChanged();
        RibbonDisconnectCommand.OnCanExecuteChanged();
        RibbonImportDataLogCommand.OnCanExecuteChanged();
        RibbonCancelReplayDataLoadCommand.OnCanExecuteChanged();
        RibbonExportDataLogCommand.OnCanExecuteChanged();
        RibbonReplayCommand.OnCanExecuteChanged();
        RibbonStopReplayCommand.OnCanExecuteChanged();
        RibbonReplayPauseResumeCommand.OnCanExecuteChanged();
        RibbonThemePreferencesCommand.OnCanExecuteChanged();
        RibbonGeneralPreferencesCommand.OnCanExecuteChanged();
    }

    private void NotifyLicenseDependentCommands()
    {
        RibbonNewProjectCommand.OnCanExecuteChanged();
        RibbonOpenProjectCommand.OnCanExecuteChanged();
        RibbonConnectCommand.OnCanExecuteChanged();
        RibbonReplayCommand.OnCanExecuteChanged();
    }

    private void NotifyRuntimeCommandsCanExecuteChanged()
    {
        RibbonConnectCommand.OnCanExecuteChanged();
        RibbonImportDataLogCommand.OnCanExecuteChanged();
        RibbonCancelReplayDataLoadCommand.OnCanExecuteChanged();
        RibbonExportDataLogCommand.OnCanExecuteChanged();
        RibbonReplayCommand.OnCanExecuteChanged();
        RibbonThemePreferencesCommand.OnCanExecuteChanged();
        RibbonGeneralPreferencesCommand.OnCanExecuteChanged();
    }

    private void NotifyProjectCommandsCanExecuteChanged()
    {
        RibbonSaveProjectCommand.OnCanExecuteChanged();
        RibbonSaveProjectAsCommand.OnCanExecuteChanged();
        RibbonCloseProjectCommand.OnCanExecuteChanged();
        RibbonProjectPasswordCommand.OnCanExecuteChanged();
        RibbonAddWorkspaceCommand.OnCanExecuteChanged();
        RibbonRemoveWorkspaceCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationProjectCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationDriversCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationVariablesCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationConversionsCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationPresentationsCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationEventsCommand.OnCanExecuteChanged();
        RibbonProjectConfigurationScriptsCommand.OnCanExecuteChanged();
        RibbonThemePreferencesCommand.OnCanExecuteChanged();
    }

    private bool CanUseProjectCommand()
    {
        return canUseHomeRibbon && isProjectMade && realProjectData?.Module != null;
    }

    private bool CanConnectRuntime()
    {
        return canConnectRuntime && isProjectMade && realProjectData?.Module != null
               && licenseService.IsRuntimeAllowed
               && !IsCommunicatedSignalsOverLimit();
    }

    /// <summary>Free-tier communicated-signal limit for command greying only; the blocking
    /// checks in ConnectAsync/ReplayAsync are separate inline comparisons on purpose.</summary>
    private bool IsCommunicatedSignalsOverLimit()
    {
        return CommunicatedSignals.GetLimit(licenseService) is { } signalLimit
               && CommunicatedSignals.Count(realProjectData?.Module) > signalLimit;
    }

    private bool CanImportDataLog()
    {
        return canImportDataLog && isProjectMade && realProjectData?.Module != null;
    }

    private bool CanOpenThemePreferences()
    {
        return canUseHomeRibbon && !isProjectMade;
    }

    private bool CanOpenGeneralPreferences()
    {
        return canUseHomeRibbon;
    }

    private bool CanCancelReplayDataLoad()
    {
        return activeReplayDriver?.IsDataLoading == true;
    }

    private bool CanExportDataLog()
    {
        return canExportDataLog
               && isProjectMade
               && isReplayDataLogImported
               && !string.IsNullOrWhiteSpace(replayDataLogFilePath)
               && File.Exists(replayDataLogFilePath)
               && FindReplayDriver() is IDataLogCsvExportDriver;
    }

    private bool CanStartReplay()
    {
        if (!canReplay ||
            !licenseService.IsRuntimeAllowed ||
            IsCommunicatedSignalsOverLimit() ||
            !isProjectMade ||
            !isReplayDataLogImported ||
            string.IsNullOrWhiteSpace(replayDataLogFilePath) ||
            !File.Exists(replayDataLogFilePath) ||
            realProjectData?.Module == null)
        {
            return false;
        }

        var replayDriver = FindReplayDriver();
        return replayDriver != null && FindReplayProtocol(replayDriver) != null;
    }

    private void SetReplayDataLogImportState(string filePath)
    {
        replayDataLogFilePath = Path.GetFullPath(filePath);
        isReplayDataLogImported = true;
        RibbonExportDataLogCommand.OnCanExecuteChanged();
    }

    // Called whenever the current project goes away (Close, New, Open over an open project).
    // The replay timeline belongs to the replay driver of that project, so the ribbon is
    // detached from it and the timeline cleared; otherwise the duration of the previous
    // project's data log would stay on screen while Replay already asks for a new Import.
    private void ClearReplayDataLogImportState()
    {
        replayDataLogFilePath = null;
        isReplayDataLogImported = false;
        UnsubscribeReplayCompleted();
        UpdateReplayProgress(TimeSpan.Zero, TimeSpan.Zero, isPaused: false, isDataLoaded: false);
        RibbonExportDataLogCommand.OnCanExecuteChanged();
        RibbonCancelReplayDataLoadCommand.OnCanExecuteChanged();
    }

    #endregion


    // Independent license seal check (defense in depth; intentionally a standalone
    // copy, not a shared helper). true = licensed or indeterminate (fail-open),
    // false = confidently unlicensed.
    [System.Reflection.Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
    private static bool TemplateSealValid()
    {
        try
        {
            const string pem =
                "-----BEGIN PUBLIC KEY-----\n" +
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOACOwsviai2bRt16xogoH6vXPVtV\n" +
                "ziUZNXothbQLrl6y3K3GZfh49fajb50QT1zJ9XGjhJOc6SRNACtipO8eiA==\n" +
                "-----END PUBLIC KEY-----";
            const string product = "QInsight";
            const string prefix = "QLIC1";

            static byte[] Dec(string v)
            {
                var s = v.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            }

            static string? Field(JsonElement o, string n)
            {
                if (o.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in o.EnumerateObject())
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                return null;
            }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) return true;
            var path = Path.Combine(baseDir, "Qenex", "QInsight", "license.json");
            if (!File.Exists(path)) return false;

            string? token;
            using (var sd = JsonDocument.Parse(File.ReadAllText(path)))
                token = Field(sd.RootElement, "Token");
            if (string.IsNullOrWhiteSpace(token)) return false;

            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != prefix) return true;

            var payload = Dec(parts[1]);
            var signature = Dec(parts[2]);

            using (var key = ECDsa.Create())
            {
                key.ImportFromPem(pem);
                if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return false;
            }

            using (var pd = JsonDocument.Parse(payload))
            {
                var pr = Field(pd.RootElement, "Product");
                if (pr is not null && !string.Equals(pr, product, StringComparison.Ordinal)) return false;

                var fp = Field(pd.RootElement, "MachineFingerprint");
                if (fp is not null)
                {
                    string src;
                    try
                    {
                        using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                        var g = rk?.GetValue("MachineGuid") as string;
                        src = string.IsNullOrWhiteSpace(g) ? Environment.MachineName : g;
                    }
                    catch { src = Environment.MachineName; }

                    var mine = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(src)));
                    if (!string.Equals(fp, mine, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }
        catch
        {
            return true; // fail-open: never block a legitimate user on a probe error
        }
    }

    // Independent license seal check (defense in depth; intentionally a standalone
    // copy, not a shared helper). true = licensed or indeterminate (fail-open),
    // false = confidently unlicensed.
    [System.Reflection.Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
    private static bool ArchiveSealValid()
    {
        try
        {
            const string pem =
                "-----BEGIN PUBLIC KEY-----\n" +
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOACOwsviai2bRt16xogoH6vXPVtV\n" +
                "ziUZNXothbQLrl6y3K3GZfh49fajb50QT1zJ9XGjhJOc6SRNACtipO8eiA==\n" +
                "-----END PUBLIC KEY-----";
            const string product = "QInsight";
            const string prefix = "QLIC1";

            static byte[] Dec(string v)
            {
                var s = v.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            }

            static string? Field(JsonElement o, string n)
            {
                if (o.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in o.EnumerateObject())
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                return null;
            }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) return true;
            var path = Path.Combine(baseDir, "Qenex", "QInsight", "license.json");
            if (!File.Exists(path)) return false;

            string? token;
            using (var sd = JsonDocument.Parse(File.ReadAllText(path)))
                token = Field(sd.RootElement, "Token");
            if (string.IsNullOrWhiteSpace(token)) return false;

            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != prefix) return true;

            var payload = Dec(parts[1]);
            var signature = Dec(parts[2]);

            using (var key = ECDsa.Create())
            {
                key.ImportFromPem(pem);
                if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return false;
            }

            using (var pd = JsonDocument.Parse(payload))
            {
                var pr = Field(pd.RootElement, "Product");
                if (pr is not null && !string.Equals(pr, product, StringComparison.Ordinal)) return false;

                var fp = Field(pd.RootElement, "MachineFingerprint");
                if (fp is not null)
                {
                    string src;
                    try
                    {
                        using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                        var g = rk?.GetValue("MachineGuid") as string;
                        src = string.IsNullOrWhiteSpace(g) ? Environment.MachineName : g;
                    }
                    catch { src = Environment.MachineName; }

                    var mine = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(src)));
                    if (!string.Equals(fp, mine, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }
        catch
        {
            return true; // fail-open: never block a legitimate user on a probe error
        }
    }

    // Independent license seal check (defense in depth; intentionally a standalone
    // copy, not a shared helper). true = licensed or indeterminate (fail-open),
    // false = confidently unlicensed.
    [System.Reflection.Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
    private static bool RuntimeSealValid()
    {
        try
        {
            const string pem =
                "-----BEGIN PUBLIC KEY-----\n" +
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOACOwsviai2bRt16xogoH6vXPVtV\n" +
                "ziUZNXothbQLrl6y3K3GZfh49fajb50QT1zJ9XGjhJOc6SRNACtipO8eiA==\n" +
                "-----END PUBLIC KEY-----";
            const string product = "QInsight";
            const string prefix = "QLIC1";

            static byte[] Dec(string v)
            {
                var s = v.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            }

            static string? Field(JsonElement o, string n)
            {
                if (o.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in o.EnumerateObject())
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                return null;
            }

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) return true;
            var path = Path.Combine(baseDir, "Qenex", "QInsight", "license.json");
            if (!File.Exists(path)) return false;

            string? token;
            using (var sd = JsonDocument.Parse(File.ReadAllText(path)))
                token = Field(sd.RootElement, "Token");
            if (string.IsNullOrWhiteSpace(token)) return false;

            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != prefix) return true;

            var payload = Dec(parts[1]);
            var signature = Dec(parts[2]);

            using (var key = ECDsa.Create())
            {
                key.ImportFromPem(pem);
                if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return false;
            }

            using (var pd = JsonDocument.Parse(payload))
            {
                var pr = Field(pd.RootElement, "Product");
                if (pr is not null && !string.Equals(pr, product, StringComparison.Ordinal)) return false;

                var fp = Field(pd.RootElement, "MachineFingerprint");
                if (fp is not null)
                {
                    string src;
                    try
                    {
                        using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                        var g = rk?.GetValue("MachineGuid") as string;
                        src = string.IsNullOrWhiteSpace(g) ? Environment.MachineName : g;
                    }
                    catch { src = Environment.MachineName; }

                    var mine = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(src)));
                    if (!string.Equals(fp, mine, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }
        catch
        {
            return true; // fail-open: never block a legitimate user on a probe error
        }
    }
}
