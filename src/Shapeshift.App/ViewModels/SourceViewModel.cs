using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;

namespace Shapeshift.App.ViewModels;

/// <summary>Шаг «Источник»: какой ISO выбран и что в нём.</summary>
public sealed partial class SourceViewModel(EngineClient engine) : ObservableObject
{
    CancellationTokenSource? _loading;

    /// <summary>Источник — скачивание из Windows Update, а не свой ISO.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    public partial bool IsDownloadMode { get; set; }

    ImageSummary? _isoImage;

    /// <summary>Выпуск UUP, если источник — «Скачать» и выбор сделан.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    public partial UupSourceDraft? Uup { get; private set; }

    /// <summary>Есть из чего собирать: выбранный ISO или выбранный выпуск UUP.</summary>
    public bool HasSource => IsDownloadMode ? Uup is not null : IsoPath is not null;

    /// <summary>Источник — свой ISO: образ — прочитанный из него.</summary>
    public void ShowIso()
    {
        Uup = null;
        Image = _isoImage;
    }

    /// <summary>Источник — выпуск UUP: образ — каким он получится из выбранного.</summary>
    public void ShowDownload(ImageSummary? image, UupSourceDraft? uup)
    {
        Uup = uup;
        Image = image;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(HasImage), nameof(HasSource))]
    public partial string? IsoPath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(Summary), nameof(Editions), nameof(ShowDropZone))]
    public partial ImageSummary? Image { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDropZone), nameof(HasImage), nameof(CanChoose))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public string FileName => IsoPath is null ? "" : Path.GetFileName(IsoPath);

    public bool HasImage => Image is not null && !IsLoading;

    public bool HasError => ErrorMessage is not null;

    /// <summary>Большая зона «перетащите сюда» — пока образ не выбран; потом её заменяет карточка образа.</summary>
    public bool ShowDropZone => !IsLoading && Image is null;

    public bool CanChoose => !IsLoading;

    /// <summary>Одна строка: выпуск · архитектура · язык.</summary>
    public string Summary => Image is null
        ? ""
        : string.Join("  ·  ", new[] { Image.ReleaseName, Image.Architecture, Image.Language }.Where(s => !string.IsNullOrEmpty(s)));

    public IReadOnlyList<EditionSummary> Editions => Image?.Editions ?? [];

    public async Task LoadAsync(string isoPath)
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();

        IsoPath = isoPath;
        _isoImage = null;
        if (!IsDownloadMode)
            Image = null;
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var result = await engine.InspectAsync(isoPath, loading.Token);
            if (loading.IsCancellationRequested)
                return;

            if (result.Image is { } image)
            {
                _isoImage = image;
                if (!IsDownloadMode)
                    Image = image;
            }
            else
                ErrorMessage = DescribeError(result.ErrorCode, result.ErrorMessage);
        }
        catch (OperationCanceledException)
        {
            // Выбрали другой файл, пока читался этот.
        }
        finally
        {
            if (loading == _loading)
                IsLoading = false;
        }
    }

    // Окно показывает свой текст по коду причины; текст движка (английский) — только если код незнаком.
    static string DescribeError(string? code, string? engineMessage)
    {
        var key = code switch
        {
            "not-found" => "ImageError_NotFound",
            "not-disc-image" => "ImageError_NotDiscImage",
            "not-windows-installer" => "ImageError_NotWindowsInstaller",
            "corrupt" => "ImageError_Corrupt",
            "server" => "ImageError_Server",
            "arm64" => "ImageError_Arm64",
            "x86" => "ImageError_X86",
            "unsupported-windows" => "ImageError_UnsupportedWindows",
            "too-old" => "ImageError_TooOld",
            _ => null,
        };
        return key is not null ? Text.Get(key) : Text.Format("ImageError_Other", engineMessage ?? code ?? "");
    }
}
