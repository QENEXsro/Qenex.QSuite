using System.IO;
using System.Windows;
using Qenex.QInsight.AppConfig;
using Qenex.QInsight.EventAggregatorMsgs;
using Qenex.QInsight.Models.Project;
using Qenex.QInsight.ViewModels.ModelWrappers;
using Qenex.QInsight.ViewModels.SolutionExplorerWrappers;
using Qenex.QInsight.Views;
using Qenex.QLibs.QUI;
using Qenex.QLibs.QUI.TelerikDocking;
using Qenex.QLibs.QUI.Wpf;
using Qenex.QSuite.Common.PluginManager;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Protocols.Protocol;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.ValuePresentation;
using Qenex.QSuite.Variables.VariableEvents;
using Telerik.Windows.Controls;

namespace Qenex.QInsight.ViewModels;

public partial class ShellWindowModel
{
    private void SubscribeEventAggregatorMessages()
    {
        eventAggregator.SubscribeAction<SolutionExplorerClickedItemMsg>(OnSolutionExplorerClickedMsg);
        eventAggregator.SubscribeAction<SolutionExplorerDoubleClickedItemMsg>(OnSolutionExplorerDoubleClickedMsg);
        eventAggregator.SubscribeAction<RemoveWorkspaceFromSolutionExplorerMsg>(RemoveWorkspace);
        eventAggregator.SubscribeAction<ScriptsRemovedMsg>(RemoveScriptDocuments);
        eventAggregator.SubscribeAction<ProjectConfigurationAppliedMsg>(_ => RefreshProjectConfigurationAppliedProperties());
        eventAggregator.SubscribeAction<WorkspaceControlSelectedMsg>(OnWorkspaceControlSelected);
        eventAggregator.SubscribeAction<RunManualScriptMsg>(msg => _ = RunManualScriptAsync(msg.Script));
        eventAggregator.SubscribeAction<StopManualScriptMsg>(msg => StopManualScript(msg.Script));
    }

    private void StopManualScript(ScriptWrapper scriptWrapper)
    {
        try
        {
            var scripting = realProjectData?.Module.Scripting;
            if (scripting?.SharedScope == null)
            {
                logger.Log(LogLevel.Warn, $"Manual script \"{scriptWrapper.FileName}\" cannot be stopped because no measurement or replay is running.");
                return;
            }

            var script = scripting.Scripts.FirstOrDefault(s => ReferenceEquals(s, scriptWrapper.Script))
                         ?? scripting.Scripts.FirstOrDefault(s => s.FileName == scriptWrapper.FileName);
            if (script == null)
            {
                logger.Log(LogLevel.Warn, $"Manual script \"{scriptWrapper.FileName}\" was not found among the project scripts.");
                return;
            }

            scripting.StopManualScript(script);
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, $"Stopping manual script \"{scriptWrapper.FileName}\" failed: {e.Message}");
        }
    }

    private async Task RunManualScriptAsync(ScriptWrapper scriptWrapper)
    {
        try
        {
            var scripting = realProjectData?.Module.Scripting;
            if (scripting?.SharedScope == null)
            {
                logger.Log(LogLevel.Warn, $"Manual script \"{scriptWrapper.FileName}\" can run only while measurement or replay is running.");
                RadWindow.Alert(new DialogParameters
                {
                    Header = "Run Script",
                    Content = "Scripts can be run only while measurement or replay is running.",
                    Owner = Application.Current.MainWindow,
                    DialogStartupLocation = WindowStartupLocation.CenterOwner
                });
                return;
            }

            var script = scripting.Scripts.FirstOrDefault(s => ReferenceEquals(s, scriptWrapper.Script))
                         ?? scripting.Scripts.FirstOrDefault(s => s.FileName == scriptWrapper.FileName);
            if (script == null)
            {
                logger.Log(LogLevel.Warn, $"Manual script \"{scriptWrapper.FileName}\" was not found among the project scripts.");
                return;
            }

            await scripting.ExecuteManualScriptAsync(script);
        }
        catch (Exception e)
        {
            logger.Log(LogLevel.Error, $"Manual script \"{scriptWrapper.FileName}\" failed: {e.Message}");
        }
    }

    private void OnWorkspaceControlSelected(WorkspaceControlSelectedMsg msg)
    {
        var vm = ViewModels.FirstOrDefault(viewModel => viewModel.Name.Equals("PropertiesViewModel"));
        if (vm is not PropertiesViewModel propVm)
        {
            return;
        }

        if (msg.Control == null)
        {
            // Deselecting in the diagram clears only a control view; a selection made elsewhere
            // (Solution Explorer) keeps its properties.
            if (propVm.SelectedViewModel is ControlPropertiesViewModel)
            {
                propVm.SelectedViewModel = new EmptyPropertiesViewModel(eventAggregator);
            }

            return;
        }

        propVm.SelectedViewModel = new ControlPropertiesViewModel(eventAggregator, msg.WorkspaceName, msg.Control);
    }
    private void OnSolutionExplorerClickedMsg(SolutionExplorerClickedItemMsg msg)
    {
        var vm = ViewModels.FirstOrDefault(vm => vm.Name.Equals("PropertiesViewModel"));
        if (vm is not PropertiesViewModel propVm) return;
        
        switch (msg.Item)
        {
            case ProjectSeWrapper projectSeWrapper:
            {
                propVm.SelectedViewModel = new ProjectPropertiesViewModel(
                    eventAggregator,
                    projectSeWrapper,
                    () => isEditProjectEnabled,
                    value => isEditProjectEnabled = value);
                break;
            }
            case WorkspaceSeWrapper workspaceWrapper:
            {
                if (ViewModels.FirstOrDefault(viewModelBase => viewModelBase is IWorkspaceViewModel ws && ws.Name.Equals(workspaceWrapper.Name)) is IWorkspaceViewModel workspaceVm)
                {
                    propVm.SelectedViewModel = new WorkspacePropertiesViewModel(eventAggregator, workspaceVm);//, workspaceWrapper.Update);
                    ActivateOpenWorkspaceDocument(workspaceVm);
                }
                break;
            }
            case ScriptSeWrapper scriptSeWrapper:
            {
                propVm.SelectedViewModel = new ScriptPropertiesViewModel(eventAggregator, scriptSeWrapper.ScriptWrapper);
                var scriptViewModel = ViewModels
                    .OfType<ScriptViewModel>()
                    .FirstOrDefault(viewModel => ReferenceEquals(viewModel.ScriptWrapper.Script, scriptSeWrapper.ScriptWrapper.Script));
                if (scriptViewModel != null)
                {
                    ActivateOpenWorkspaceDocument(scriptViewModel);
                }

                break;
            }
            case DriverSeWrapper driverSeWrapper:
            {
                propVm.SelectedViewModel = new DriverPropertiesViewModel(eventAggregator, driverSeWrapper.Driver, () => IsRuntimeStarted);
                break;
            }
            case ProtocolSeWrapper protocolSeWrapper:
            {
                propVm.SelectedViewModel = new ProtocolPropertiesViewModel(eventAggregator, protocolSeWrapper.Protocol);
                break;
            }
            case VariableSeWrapper variableSeWrapper:
            {
                propVm.SelectedViewModel = new VariablePropertiesViewModel(
                    eventAggregator,
                    variableSeWrapper,
                    realProjectData.Module.Presentations,
                    realProjectData.Module.VarEvents,
                    () => isEditVariableEnabled);
                break;
            }
            case ConversionSeWrapper conversionSeWrapper:
            {
                propVm.SelectedViewModel = new ConversionPropertiesViewModel(
                    eventAggregator,
                    conversionSeWrapper,
                    () => isEditConversionEnabled);
                break;
            }
            case PresentationSeWrapper presentationSeWrapper:
            {
                propVm.SelectedViewModel = new PresentationPropertiesViewModel(
                    eventAggregator,
                    presentationSeWrapper,
                    realProjectData.Module.Conversions,
                    () => isEditPresentationEnabled);
                break;
            }
            case IVariableEventSeWrapper eventSeWrapper:
            {
                propVm.SelectedViewModel = new EventPropertiesViewModel(
                    eventAggregator,
                    eventSeWrapper,
                    () => isEditEventEnabled);
                break;
            }
            case ProtocolVariableWrapper protocolVariableWrapper:
            {
                propVm.SelectedViewModel = new ReadOnlyVariablePropertiesViewModel(
                    eventAggregator,
                    protocolVariableWrapper.ProtocolVariable);
                break;
            }
            case NodeSeWrapper { TypeOfNode: NodeSeWrapper.NodeType.Drivers } nodeWrapper
                when nodeWrapper.CustomTags?.TryGetValue("Drivers", out var drivers) == true
                     && drivers is IEnumerable<IDriverBase> communicatedDrivers:
            {
                propVm.SelectedViewModel = new CommunicatedDriversPropertiesViewModel(eventAggregator, communicatedDrivers);
                break;
            }
            case NodeSeWrapper { TypeOfNode: NodeSeWrapper.NodeType.Protocols } nodeWrapper
                when nodeWrapper.CustomTags?.TryGetValue("Protocols", out var protocols) == true
                     && protocols is IEnumerable<IProtocolBase> communicatedProtocols:
            {
                propVm.SelectedViewModel = new CommunicatedProtocolsPropertiesViewModel(eventAggregator, communicatedProtocols);
                break;
            }
            case NodeSeWrapper { TypeOfNode: NodeSeWrapper.NodeType.Variables } nodeWrapper
                when nodeWrapper.CustomTags?.TryGetValue("Variables", out var variables) == true
                     && variables is IList<IVariableBase> moduleVariables:
            {
                propVm.SelectedViewModel = new VariablesPropertiesViewModel(
                    eventAggregator,
                    moduleVariables,
                    () => isEditVariableEnabled,
                    value =>
                    {
                        isEditVariableEnabled = value;
                        if (propertiesViewModel.SelectedViewModel is VariablePropertiesViewModel variablePropertiesViewModel)
                        {
                            variablePropertiesViewModel.RefreshReadOnly();
                        }
                    });
                break;
            }
            case NodeSeWrapper { TypeOfNode: NodeSeWrapper.NodeType.Conversions } nodeWrapper
                when nodeWrapper.CustomTags?.TryGetValue("Conversions", out var conversions) == true
                     && conversions is IList<IValConversion> moduleConversions:
            {
                propVm.SelectedViewModel = new ConversionsPropertiesViewModel(
                    eventAggregator,
                    moduleConversions,
                    () => isEditConversionEnabled,
                    value =>
                    {
                        isEditConversionEnabled = value;
                        if (propertiesViewModel.SelectedViewModel is ConversionPropertiesViewModel conversionPropertiesViewModel)
                        {
                            conversionPropertiesViewModel.RefreshReadOnly();
                        }
                    });
                break;
            }
            case NodeSeWrapper { TypeOfNode: NodeSeWrapper.NodeType.Presentations } nodeWrapper
                when nodeWrapper.CustomTags?.TryGetValue("Presentations", out var presentations) == true
                     && presentations is IList<IPresentation> modulePresentations:
            {
                propVm.SelectedViewModel = new PresentationsPropertiesViewModel(
                    eventAggregator,
                    modulePresentations,
                    () => isEditPresentationEnabled,
                    value =>
                    {
                        isEditPresentationEnabled = value;
                        if (propertiesViewModel.SelectedViewModel is PresentationPropertiesViewModel presentationPropertiesViewModel)
                        {
                            presentationPropertiesViewModel.RefreshReadOnly();
                        }
                    });
                break;
            }
            case NodeSeWrapper { TypeOfNode: NodeSeWrapper.NodeType.Events } nodeWrapper
                when nodeWrapper.CustomTags?.TryGetValue("Events", out var events) == true
                     && events is IList<IVarEvent> moduleEvents:
            {
                propVm.SelectedViewModel = new EventsPropertiesViewModel(
                    eventAggregator,
                    moduleEvents,
                    () => isEditEventEnabled,
                    value =>
                    {
                        isEditEventEnabled = value;
                        if (propertiesViewModel.SelectedViewModel is EventPropertiesViewModel eventPropertiesViewModel)
                        {
                            eventPropertiesViewModel.RefreshReadOnly();
                        }
                    });
                break;
            }
            default:
            {
                propVm.SelectedViewModel = new EmptyPropertiesViewModel(eventAggregator);
                break;
            }
        }
    }

    private void ActivateOpenWorkspaceDocument(IWorkspaceViewModel workspaceViewModel)
    {
        if (workspaceViewModel.IsHidden)
        {
            return;
        }

        var pane = FindDockingPaneForViewModel(shellRadDocking, workspaceViewModel);
        if (pane == null)
        {
            return;
        }

        shellRadDocking.ActivePane = pane;
        pane.IsActive = true;
        pane.Focus();
    }

    private void OnSolutionExplorerDoubleClickedMsg(SolutionExplorerDoubleClickedItemMsg msg)
    {
        switch (msg.Item)
        {
            case WorkspaceSeWrapper workspaceWrapper:
            {
                var workspaceVm = ViewModels.FirstOrDefault(vm => vm is IWorkspaceViewModel ws && ws.WinTitle.Equals(workspaceWrapper.Label));
                if (workspaceVm != null)
                {
                    workspaceVm.IsHidden = !workspaceVm.IsHidden;
                }
                break;
            }
            case ScriptSeWrapper scriptSeWrapper:
            {
                var foundScriptViewModel = ViewModels.FirstOrDefault(vm => vm is IWorkspaceViewModel ws && ws.WinTitle.Equals(scriptSeWrapper.Label));
                if (foundScriptViewModel != null)
                {
                    foundScriptViewModel.IsHidden = !foundScriptViewModel.IsHidden;
                }
                else
                {
                    var targetGroup = GetTargetDocumentPaneGroup(shellRadDocking);
                    var scriptViewModel = new ScriptViewModel(eventAggregator, scriptSeWrapper.ScriptWrapper)
                    {
                        IsRuntimeRunning = IsRuntimeStarted
                    };
                    ViewModels.Add(scriptViewModel);
                    _ = MoveNewWorkspaceDocumentToTargetGroupAsync(shellRadDocking, scriptViewModel, targetGroup);
                }
                break;
            }
        }
    }
    
    private void RemoveWorkspace(RemoveWorkspaceFromSolutionExplorerMsg msg)
    {
        var radGroup = shellRadDocking.SplitItems.OfType<RadPaneGroup>().FirstOrDefault(i => i.Name.Contains("WorkspacePaneGroup"));
        
        if (radGroup != null)
        {
            var panetoRemove = radGroup.Items.OfType<QRadDocumentPane>().FirstOrDefault(p => p.Name == msg.Name);
            if (panetoRemove != null)
            {
                panetoRemove.RemoveFromParent();
            }
        }
    }

    private void RemoveScriptDocuments(ScriptsRemovedMsg msg)
    {
        var removedScripts = msg.Scripts.ToHashSet();
        foreach (var scriptViewModel in ViewModels
                     .OfType<ScriptViewModel>()
                     .Where(viewModel => removedScripts.Contains(viewModel.ScriptWrapper.Script))
                     .ToList())
        {
            if (ReferenceEquals(propertiesViewModel.SelectedViewModel is ScriptPropertiesViewModel scriptProperties
                    ? scriptProperties.ScriptWrapper.Script
                    : null, scriptViewModel.ScriptWrapper.Script))
            {
                SetDefaultPropertiesView();
            }

            var pane = FindDockingPaneForViewModel(shellRadDocking, scriptViewModel);
            ViewModels.Remove(scriptViewModel);
            pane?.RemoveFromParent();
        }
    }
}
