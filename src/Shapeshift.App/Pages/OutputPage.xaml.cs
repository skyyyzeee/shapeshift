using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using Shapeshift.App.ViewModels;

namespace Shapeshift.App.Pages;

public sealed partial class OutputPage : Page
{
    public OutputPage() => InitializeComponent();

    public OutputViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (OutputViewModel)e.Parameter;
        base.OnNavigatedTo(e);
        ViewModel.SuggestOutputPath();
        if (ViewModel.State == BuildState.Ready)
            _ = ViewModel.RefreshPlanAsync();
    }

    async void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedFileName = Path.GetFileName(ViewModel.OutputPath) ?? "shapeshift.iso",
        };
        picker.FileTypeChoices.Add("ISO", [".iso"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        ViewModel.OutputPath = file.Path;
        await ViewModel.RefreshPlanAsync();
    }

    async void Build_Click(object sender, RoutedEventArgs e) => await ViewModel.BuildAsync();

    async void Cancel_Click(object sender, RoutedEventArgs e) => await ViewModel.CancelAsync();

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.OutputPath is { } path && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    async void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedFileName = $"shapeshift-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };
        picker.FileTypeChoices.Add("Log", [".log", ".txt"]);
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
            await File.WriteAllLinesAsync(file.Path, ViewModel.Log);
    }

    async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = "shapeshift-profile.json" };
        picker.FileTypeChoices.Add(Services.Text.Get("ProfileFileType"), [".json"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        await ViewModel.SaveProfileAsync(file.Path);
        ShowFileMessage(InfoBarSeverity.Success, Services.Text.Get("ProfileSaved"), file.Path);
    }

    async void ExportAnswerFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = "autounattend.xml" };
        picker.FileTypeChoices.Add(Services.Text.Get("AnswerFileType"), [".xml"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        var error = await ViewModel.ExportAnswerFileAsync(file.Path);
        if (error is null)
            ShowFileMessage(InfoBarSeverity.Success, Services.Text.Get("AnswerFileExported"), file.Path);
        else
            ShowFileMessage(InfoBarSeverity.Error, Services.Text.Get("AnswerFileExportFailed"), error);
    }

    void ShowFileMessage(InfoBarSeverity severity, string title, string message)
    {
        FileMessage.Severity = severity;
        FileMessage.Title = title;
        FileMessage.Message = message;
        FileMessage.IsOpen = true;
    }

    async void BackToSummary_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Reset();
        await ViewModel.RefreshPlanAsync();
    }
}
