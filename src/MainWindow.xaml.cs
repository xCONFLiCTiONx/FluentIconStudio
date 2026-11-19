using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Threading.Tasks;
using FluentIconStudio.Models;
using FluentIconStudio.Services;
using Microsoft.Win32;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Threading;

namespace FluentIconStudio;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<IconFile> _visibleIcons = [];
    private List<IconFile> _allIcons = [];
    private string? _currentFolder;
    private string? _activeSvgPath;
    private string _originalSvgText = string.Empty;
    private readonly ObservableCollection<ExportPreset> _exportPresets = [];
    private bool _loadingPresets;
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _thumbnailLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _thumbnailSlots = new(4, 4);
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounceTimer;
    private bool _isDownloadingPack;
    private SKColor? _activeIconColor = SKColors.White;
    private int _currentPage = 1;
    private const int PageSize = 500;
    private int _totalMatches;

    private static string ExportPresetsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentIconStudio", "export-presets.json");

    private sealed record ExportPreset(string Name, int Size, string Format, int Quality, string Background, bool Transparent);

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowBounds();
        ExportPresetBox.ItemsSource = _exportPresets;
        ExportPresetBox.DisplayMemberPath = "Name";
        LoadExportPresets();
        IconList.ItemsSource = _visibleIcons;

        _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilters();
        };

        if (FluentIconDownloader.IsPackDownloaded())
            _currentFolder = FluentIconDownloader.PackDirectory;
        else
            _currentFolder = Path.Combine(AppContext.BaseDirectory, "Icons");

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await LoadCatalogAsync();
    }

    private static string WindowSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentIconStudio", "window.json");

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableImmersiveDarkMode();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void EnableImmersiveDarkMode()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int useDarkMode = 1;

            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 20H1+ and Windows 11)
            // DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19 (Windows 10 1903/1909)
            if (DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref useDarkMode, sizeof(int));
            }

            // DWMWA_CAPTION_COLOR = 35 (Windows 11 caption color: #111318 in BGR format 0x00181311)
            int captionColor = 0x00181311;
            DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
        }
        catch
        {
            // Fail gracefully on OS versions without DWM dark mode support
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(WindowSettingsPath))
                return;

            var saved = JsonSerializer.Deserialize<SavedWindowBounds>(File.ReadAllText(WindowSettingsPath));
            if (saved is null || saved.Width < MinWidth || saved.Height < MinHeight)
                return;

            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(saved.Width, workArea.Width);
            Height = Math.Min(saved.Height, workArea.Height);
            Left = Math.Clamp(saved.Left, workArea.Left, workArea.Right - Width);
            Top = Math.Clamp(saved.Top, workArea.Top, workArea.Bottom - Height);

            if (saved.WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // A missing or malformed settings file should never prevent startup.
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(WindowSettingsPath)!;
            Directory.CreateDirectory(folder);

            var bounds = RestoreBounds;
            double left = WindowState == WindowState.Normal ? Left : bounds.Left;
            double top = WindowState == WindowState.Normal ? Top : bounds.Top;
            double width = WindowState == WindowState.Normal ? Width : bounds.Width;
            double height = WindowState == WindowState.Normal ? Height : bounds.Height;

            var saved = new SavedWindowBounds(
                left,
                top,
                width,
                height,
                WindowState == WindowState.Minimized ? WindowState.Normal : WindowState);

            File.WriteAllText(WindowSettingsPath, JsonSerializer.Serialize(saved));
        }
        catch
        {
            // Window position persistence is best-effort and must not block closing.
        }
    }

    private sealed record SavedWindowBounds(double Left, double Top, double Width, double Height, WindowState WindowState);

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder containing SVG icons",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            _currentFolder = dialog.FolderName;
            await LoadCatalogAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _thumbnailCache.Clear();
        _thumbnailLoading.Clear();
        await LoadCatalogAsync();
    }

    // Render actual SVG artwork into each catalog tile on demand instead of showing a placeholder glyph.
    private async void IconThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not IconFile icon)
            return;

        if (image.Parent is Border thumbnailContainer)
        {
            thumbnailContainer.Background = _activeIconColor == SKColors.Black
                ? (System.Windows.Media.Brush)FindResource("LightCheckerboardBrush")
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x17, 0x1D));
        }

        if (_thumbnailCache.TryGetValue(icon.FullPath, out var cached))
        {
            icon.Thumbnail = cached;
            return;
        }

        if (!_thumbnailLoading.Add(icon.FullPath))
            return;

        try
        {
            await _thumbnailSlots.WaitAsync();
            try
            {
                if (!File.Exists(icon.FullPath))
                    return;

                var bytes = await SvgExportService.RenderPngAsync(icon.FullPath, 64, _activeIconColor);
                if (bytes is not null)
                {
                    var bitmap = LoadBitmap(bytes);
                    _thumbnailCache[icon.FullPath] = bitmap;
                    icon.Thumbnail = bitmap;
                }
            }
            finally
            {
                _thumbnailSlots.Release();
            }
        }
        catch (Exception ex)
        {
            // Surface rendering failures instead of silently leaving a blank thumbnail.
            StatusText.Text = $"Thumbnail failed for {icon.Name}: {ex.Message}";
        }
        finally
        {
            _thumbnailLoading.Remove(icon.FullPath);
        }
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer?.Start();
    }

    private void StyleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _searchDebounceTimer?.Stop();
        ApplyFilters();
    }

    private async Task LoadCatalogAsync()
    {
        if (LoadingOverlay is not null)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            if (CatalogLoadingText is not null)
                CatalogLoadingText.Text = "Loading icon library...";
            if (CatalogLoadingSubtext is not null)
                CatalogLoadingSubtext.Text = "Indexing SVG icons...";
        }

        StatusText.Text = "Loading icon catalog...";
        var folder = _currentFolder ?? string.Empty;

        var icons = await Task.Run(() => IconCatalog.Scan(folder).ToList());
        await Task.Run(() => FastCatalogIndex.Index(icons));

        _allIcons = icons;
        ApplyFilters();

        StatusText.Text = _allIcons.Count == 0
            ? "No SVGs found. Choose the folder where your Fluent icon SVG files are stored."
            : $"Loaded {_allIcons.Count:N0} SVG icons from {_currentFolder}";

        if (LoadingOverlay is not null)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyFilters(bool append = false)
    {
        var query = SearchBox?.Text ?? string.Empty;
        var style = (StyleFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All styles";

        if (!append)
        {
            _currentPage = 1;
        }

        int offset = (_currentPage - 1) * PageSize;
        var (matches, totalMatchCount) = FastCatalogIndex.Search(query, style, offset, PageSize);
        _totalMatches = totalMatchCount;

        if (!append)
        {
            _visibleIcons.Clear();
        }

        foreach (var item in matches)
        {
            _visibleIcons.Add(item);
        }

        UpdatePaginationUI();
    }

    private void UpdatePaginationUI()
    {
        if (PageInfoText is null)
            return;

        int totalPages = Math.Max(1, (int)Math.Ceiling((double)_totalMatches / PageSize));
        int startItem = _totalMatches == 0 ? 0 : ((_currentPage - 1) * PageSize) + 1;
        int endItem = Math.Min(_totalMatches, _visibleIcons.Count + ((_currentPage - 1) * PageSize));

        PageInfoText.Text = $"Page {_currentPage} of {totalPages:N0} ({startItem:N0}–{endItem:N0} of {_totalMatches:N0})";
        if (CountText is not null)
            CountText.Text = $"{_totalMatches:N0} icons";

        if (PrevPageBtn is not null)
            PrevPageBtn.IsEnabled = _currentPage > 1;
        if (NextPageBtn is not null)
            NextPageBtn.IsEnabled = _currentPage < totalPages;
        if (LoadMoreBtn is not null)
            LoadMoreBtn.IsEnabled = endItem < _totalMatches;
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            ApplyFilters(append: false);
            IconList?.ScrollIntoView(_visibleIcons.FirstOrDefault());
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        int totalPages = Math.Max(1, (int)Math.Ceiling((double)_totalMatches / PageSize));
        if (_currentPage < totalPages)
        {
            _currentPage++;
            ApplyFilters(append: false);
            IconList?.ScrollIntoView(_visibleIcons.FirstOrDefault());
        }
    }

    private void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        int totalPages = Math.Max(1, (int)Math.Ceiling((double)_totalMatches / PageSize));
        if (_currentPage < totalPages)
        {
            _currentPage++;
            ApplyFilters(append: true);
        }
    }

    private void IconColor_White_Click(object sender, RoutedEventArgs e)
    {
        _activeIconColor = SKColors.White;
        if (BackgroundColorBox is not null)
            BackgroundColorBox.Text = "#000000";

        if (TransparentCheck is not null)
            TransparentCheck.IsChecked = true;

        if (PreviewBorder is not null)
        {
            PreviewBorder.Background = (System.Windows.Media.Brush)FindResource("CheckerboardBrush");
            PreviewPlaceholder.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
        }

        ApplyColorModeToActiveSvg("#FFFFFF", "#15171D");
        UpdateIconColorButtons();
        RefreshIconRendering();
    }

    private void IconColor_Black_Click(object sender, RoutedEventArgs e)
    {
        _activeIconColor = SKColors.Black;
        if (BackgroundColorBox is not null)
            BackgroundColorBox.Text = "#FFFFFF";

        if (TransparentCheck is not null)
            TransparentCheck.IsChecked = true;

        if (PreviewBorder is not null)
        {
            PreviewBorder.Background = (System.Windows.Media.Brush)FindResource("LightCheckerboardBrush");
            PreviewPlaceholder.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
        }

        ApplyColorModeToActiveSvg("#000000", "#FFFFFF");
        UpdateIconColorButtons();
        RefreshIconRendering();
    }

    private void IconColor_Original_Click(object sender, RoutedEventArgs e)
    {
        _activeIconColor = null;
        if (PreviewBorder is not null)
        {
            PreviewBorder.Background = (System.Windows.Media.Brush)FindResource("CheckerboardBrush");
            PreviewPlaceholder.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
        }

        if (IconList.SelectedItem is IconFile icon && !string.IsNullOrWhiteSpace(_originalSvgText))
        {
            var originalXml = SetSvgDimensionsInXml(_originalSvgText, 512, 512);
            SvgSourceBox.Text = originalXml;
            LoadSvgPropertyFields(originalXml);
            SaveActiveWorkingSvg(icon, originalXml);
        }

        UpdateIconColorButtons();
        RefreshIconRendering();
    }

    private void PreviewZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreviewScaleTransform is null || PreviewZoomText is null)
            return;

        double scale = PreviewZoomSlider.Value;
        PreviewScaleTransform.ScaleX = scale;
        PreviewScaleTransform.ScaleY = scale;
        PreviewZoomText.Text = $"{(int)Math.Round(scale * 100)}%";
    }

    private void ResetPreviewZoom_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewZoomSlider is not null)
            PreviewZoomSlider.Value = 1.0;
    }

    private void PreviewBorder_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };

        DependencyObject? current = sender as DependencyObject;
        while (current is not null)
        {
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            if (current is ScrollViewer outerScrollViewer && outerScrollViewer != sender)
            {
                outerScrollViewer.RaiseEvent(eventArg);
                break;
            }
        }
    }

    private async void ApplyColorModeToActiveSvg(string targetColorHex, string backgroundHex)
    {
        if (IconList.SelectedItem is IconFile icon && !string.IsNullOrWhiteSpace(SvgSourceBox.Text))
        {
            var recoloredXml = SvgExportService.RecolorSvgXml(SvgSourceBox.Text, targetColorHex, backgroundHex);
            SvgSourceBox.Text = recoloredXml;
            LoadSvgPropertyFields(recoloredXml);
            await SaveActiveWorkingSvgAsync(icon, recoloredXml);
        }
    }

    private async Task SaveActiveWorkingSvgAsync(IconFile icon, string svgXml)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluentIconStudio", "WorkingCopy");
            Directory.CreateDirectory(directory);
            var workingPath = Path.Combine(directory, SafeName(icon.Name) + "-512.svg");
            await File.WriteAllTextAsync(workingPath, svgXml);
            _activeSvgPath = workingPath;
        }
        catch { }
    }

    private void SaveActiveWorkingSvg(IconFile icon, string svgXml)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluentIconStudio", "WorkingCopy");
            Directory.CreateDirectory(directory);
            var workingPath = Path.Combine(directory, SafeName(icon.Name) + "-512.svg");
            File.WriteAllText(workingPath, svgXml);
            _activeSvgPath = workingPath;
        }
        catch { }
    }

    private void UpdateIconColorButtons()
    {
        if (IconWhiteBtn is null || IconBlackBtn is null)
            return;

        IconWhiteBtn.Background = _activeIconColor == SKColors.White ? (System.Windows.Media.Brush)FindResource("Accent") : (System.Windows.Media.Brush)FindResource("ControlBg");
        IconBlackBtn.Background = _activeIconColor == SKColors.Black ? (System.Windows.Media.Brush)FindResource("Accent") : (System.Windows.Media.Brush)FindResource("ControlBg");
        }

    private async void RefreshIconRendering()
    {
        _thumbnailCache.Clear();
        _thumbnailLoading.Clear();
        ApplyFilters();

        if (IconList.SelectedItem is IconFile icon)
        {
            var bytes = await SvgExportService.RenderPngAsync(GetActiveSvgPath(icon), 256, _activeIconColor);
            if (bytes is not null)
            {
                PreviewImage.Source = LoadBitmap(bytes);
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SetBgColor_White_Click(object sender, RoutedEventArgs e)
    {
        if (BackgroundColorBox is not null)
            BackgroundColorBox.Text = "#FFFFFF";
    }

    private void SetBgColor_Black_Click(object sender, RoutedEventArgs e)
    {
        if (BackgroundColorBox is not null)
            BackgroundColorBox.Text = "#000000";
    }

    private void PreviewBg_Checker_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewBorder is not null)
        {
            PreviewBorder.Background = (System.Windows.Media.Brush)FindResource("CheckerboardBrush");
            PreviewPlaceholder.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
        }
    }

    private void BackgroundColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // BackgroundColorBox controls raster export background color only.
        // It does not override the viewer's transparent checkerboard mesh canvas.
    }

    private async void DownloadPack_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloadingPack)
            return;

        _isDownloadingPack = true;
        DownloadPackButton.IsEnabled = false;

        if (LoadingOverlay is not null)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            if (CatalogLoadingText is not null)
                CatalogLoadingText.Text = "Downloading Fluent Icon Pack...";
            if (CatalogLoadingSubtext is not null)
                CatalogLoadingSubtext.Text = "Fetching package archive from GitHub...";
        }

        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
            if (CatalogLoadingSubtext is not null)
                CatalogLoadingSubtext.Text = msg;
        });

        try
        {
            await FluentIconDownloader.DownloadAndExtractPackAsync(progress);
            _currentFolder = FluentIconDownloader.PackDirectory;
            _thumbnailCache.Clear();
            _thumbnailLoading.Clear();
            await LoadCatalogAsync();
            StatusText.Text = $"Successfully downloaded and loaded {_allIcons.Count:N0} Fluent System Icons from GitHub!";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            _isDownloadingPack = false;
            DownloadPackButton.IsEnabled = true;
            if (LoadingOverlay is not null)
                LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private static string SetSvgDimensionsInXml(string sourceXml, int newWidth, int newHeight)
    {
        try
        {
            var doc = XDocument.Parse(sourceXml, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                return sourceXml;

            var origWidth = (string?)root.Attribute("width");
            var origHeight = (string?)root.Attribute("height");
            var origViewBox = (string?)root.Attribute("viewBox");

            if (string.IsNullOrWhiteSpace(origViewBox))
            {
                if (!string.IsNullOrWhiteSpace(origWidth) && !string.IsNullOrWhiteSpace(origHeight))
                    root.SetAttributeValue("viewBox", $"0 0 {origWidth} {origHeight}");
                else
                    root.SetAttributeValue("viewBox", $"0 0 {newWidth} {newHeight}");
            }

            root.SetAttributeValue("width", newWidth);
            root.SetAttributeValue("height", newHeight);

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return sourceXml;
        }
    }

    private async void IconList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
            return;

        SelectedName.Text = icon.Name;
        SelectedPath.Text = icon.FullPath;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        _activeSvgPath = icon.FullPath;

        try
        {
            _originalSvgText = await File.ReadAllTextAsync(icon.FullPath);
            if (!ReferenceEquals(IconList.SelectedItem, icon))
                return;

            var updatedSvgXml = SetSvgDimensionsInXml(_originalSvgText, 512, 512);

            if (_activeIconColor == SKColors.White)
            {
                updatedSvgXml = SvgExportService.RecolorSvgXml(updatedSvgXml, "#FFFFFF", "#15171D");
            }
            else if (_activeIconColor == SKColors.Black)
            {
                updatedSvgXml = SvgExportService.RecolorSvgXml(updatedSvgXml, "#000000", "#FFFFFF");
            }

            SvgSourceBox.Text = updatedSvgXml;
            LoadSvgPropertyFields(updatedSvgXml);

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluentIconStudio", "WorkingCopy");
            Directory.CreateDirectory(directory);
            var workingPath = Path.Combine(directory, SafeName(icon.Name) + "-512.svg");
            await File.WriteAllTextAsync(workingPath, updatedSvgXml);
            _activeSvgPath = workingPath;

            var bytes = await SvgExportService.RenderPngAsync(workingPath, 256);
            if (bytes is not null)
            {
                PreviewImage.Source = LoadBitmap(bytes);
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Selected {icon.Name} · {icon.Style}";
            }
            else
            {
                PreviewImage.Source = null;
                PreviewPlaceholder.Text = "Preview unavailable (invalid SVG artwork)";
                PreviewPlaceholder.Visibility = Visibility.Visible;
                StatusText.Text = $"Selected {icon.Name} · Invalid SVG artwork";
            }
        }
        catch (Exception ex)
        {
            PreviewImage.Source = null;
            PreviewPlaceholder.Text = "Preview unavailable";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            StatusText.Text = $"Preview error: {ex.Message}";
        }
    }

    private string GetActiveSvgPath(IconFile icon) =>
        _activeSvgPath is not null && IconList.SelectedItem is IconFile selected &&
        ReferenceEquals(selected, icon) ? _activeSvgPath : icon.FullPath;

    private void LoadSvgPropertyFields(string source)
    {
        try
        {
            var root = XDocument.Parse(source).Root;
            if (root is null)
                return;

            SvgWidthBox.Text = (string?)root.Attribute("width") ?? "512";
            SvgHeightBox.Text = (string?)root.Attribute("height") ?? "512";
            SvgViewBoxBox.Text = (string?)root.Attribute("viewBox") ?? string.Empty;
            SvgFillBox.Text = GetLastSvgAttributeValue(root, "fill");
            SvgStrokeBox.Text = GetLastSvgAttributeValue(root, "stroke");

            if (ExportSizeBox is not null)
                ExportSizeBox.Text = "512";
        }
        catch
        {
            SvgWidthBox.Text = "512";
            SvgHeightBox.Text = "512";
            SvgViewBoxBox.Text = "0 0 512 512";
            SvgFillBox.Text = SvgStrokeBox.Text = string.Empty;
        }
    }

    private void SvgSourceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SvgSourceBox is not null && !string.IsNullOrWhiteSpace(SvgSourceBox.Text))
        {
            LoadSvgPropertyFields(SvgSourceBox.Text);
        }
    }

    private static string GetLastSvgAttributeValue(XElement root, string name)
    {
        string lastValue = string.Empty;
        foreach (var element in root.DescendantsAndSelf())
        {
            var attr = element.Attribute(name);
            if (attr is not null && !string.IsNullOrWhiteSpace(attr.Value))
            {
                lastValue = attr.Value.Trim();
            }

            var styleAttr = element.Attribute("style");
            if (styleAttr is not null && !string.IsNullOrWhiteSpace(styleAttr.Value))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    styleAttr.Value,
                    $@"(?:^|;)\s*{name}\s*:\s*([^;]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    lastValue = match.Groups[1].Value.Trim();
                }
            }
        }
        return lastValue;
    }

    private void ApplySvgProperties_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var document = XDocument.Parse(SvgSourceBox.Text, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The document root must be an SVG element.");

            SetSvgAttribute(root, "width", SvgWidthBox.Text);
            SetSvgAttribute(root, "height", SvgHeightBox.Text);
            SetSvgAttribute(root, "viewBox", SvgViewBoxBox.Text);
            SetLastSvgAttribute(root, "fill", SvgFillBox.Text);
            SetLastSvgAttribute(root, "stroke", SvgStrokeBox.Text);

            SvgSourceBox.Text = document.ToString(SaveOptions.DisableFormatting);
            LoadSvgPropertyFields(SvgSourceBox.Text);
            StatusText.Text = "SVG properties applied to source. Click Apply & Preview to render them.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not apply SVG properties: {ex.Message}";
        }
    }

    private static void SetLastSvgAttribute(XElement root, string name, string value)
    {
        XElement? targetAttrElement = null;
        XAttribute? targetAttr = null;
        XElement? targetStyleElement = null;
        bool lastWasStyle = false;

        foreach (var element in root.DescendantsAndSelf())
        {
            var attr = element.Attribute(name);
            if (attr is not null && !string.IsNullOrWhiteSpace(attr.Value))
            {
                targetAttrElement = element;
                targetAttr = attr;
                lastWasStyle = false;
            }

            var styleAttr = element.Attribute("style");
            if (styleAttr is not null && !string.IsNullOrWhiteSpace(styleAttr.Value))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(styleAttr.Value, $@"(?:^|;)\s*{name}\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    targetStyleElement = element;
                    lastWasStyle = true;
                }
            }
        }

        if (lastWasStyle && targetStyleElement is not null)
        {
            var styleAttr = targetStyleElement.Attribute("style");
            if (styleAttr is not null)
            {
                var pattern = $@"(?:^|;)\s*{name}\s*:\s*[^;]+";
                if (string.IsNullOrWhiteSpace(value))
                {
                    styleAttr.Value = System.Text.RegularExpressions.Regex.Replace(styleAttr.Value, pattern, "");
                }
                else
                {
                    styleAttr.Value = System.Text.RegularExpressions.Regex.Replace(styleAttr.Value, pattern, $"{name}:{value.Trim()}");
                }
            }
        }
        else if (targetAttrElement is not null && targetAttr is not null)
        {
            if (string.IsNullOrWhiteSpace(value))
                targetAttr.Remove();
            else
                targetAttr.Value = value.Trim();
        }
        else
        {
            SetSvgAttribute(root, name, value);
        }
    }

    private static void SetSvgAttribute(XElement root, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            root.Attribute(name)?.Remove();
        else
            root.SetAttributeValue(name, value.Trim());
    }

    private async void ApplySvg_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
        {
            StatusText.Text = "Select an SVG icon first.";
            return;
        }

        var edited = SvgSourceBox.Text;
        try
        {
            var parsed = XDocument.Parse(edited, LoadOptions.PreserveWhitespace);
            if (parsed.Root is null || !parsed.Root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The document root must be an SVG element.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Invalid SVG source: {ex.Message}";
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluentIconStudio", "WorkingCopy");
            Directory.CreateDirectory(directory);
            var workingPath = Path.Combine(directory, SafeName(icon.Name) + "-" + Guid.NewGuid().ToString("N") + ".svg");
            await File.WriteAllTextAsync(workingPath, edited);

            // Render before accepting the edited source, so malformed SVG does not replace the current preview.
            var bytes = await SvgExportService.RenderPngAsync(workingPath, 256, _activeIconColor);
            if (bytes is null)
            {
                StatusText.Text = "SVG edit could not be rendered. Check the SVG markup.";
                return;
            }

            if (!ReferenceEquals(IconList.SelectedItem, icon))
                return;

            _activeSvgPath = workingPath;
            PreviewImage.Source = LoadBitmap(bytes);
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            LoadSvgPropertyFields(edited);
            StatusText.Text = $"Applied edits to {icon.Name}. The original file has not been changed.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"SVG edit preview failed: {ex.Message}";
        }
    }

    private void WordWrapCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (SvgSourceBox is null)
            return;

        bool isWrapped = WordWrapCheck.IsChecked == true;
        SvgSourceBox.TextWrapping = isWrapped ? TextWrapping.Wrap : TextWrapping.NoWrap;
        SvgSourceBox.HorizontalScrollBarVisibility = isWrapped ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void FormatSvgXml_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SvgSourceBox.Text))
            return;

        try
        {
            var doc = XDocument.Parse(SvgSourceBox.Text);
            SvgSourceBox.Text = doc.ToString();
            LoadSvgPropertyFields(SvgSourceBox.Text);
            StatusText.Text = "Formatted SVG XML.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not format SVG XML: {ex.Message}";
        }
    }

    private async void ResetSvg_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
            return;

        try
        {
            _originalSvgText = await File.ReadAllTextAsync(icon.FullPath);
            var updatedSvgXml = SetSvgDimensionsInXml(_originalSvgText, 512, 512);

            SvgSourceBox.Text = updatedSvgXml;
            LoadSvgPropertyFields(updatedSvgXml);

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluentIconStudio", "WorkingCopy");
            Directory.CreateDirectory(directory);
            var workingPath = Path.Combine(directory, SafeName(icon.Name) + "-512.svg");
            await File.WriteAllTextAsync(workingPath, updatedSvgXml);
            _activeSvgPath = workingPath;

            var bytes = await SvgExportService.RenderPngAsync(workingPath, 256, _activeIconColor);
            if (bytes is not null)
            {
                PreviewImage.Source = LoadBitmap(bytes);
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewImage.Source = null;
                PreviewPlaceholder.Text = "Preview unavailable (invalid SVG artwork)";
                PreviewPlaceholder.Visibility = Visibility.Visible;
            }
            StatusText.Text = $"Reset {icon.Name} to default 512×512 dimensions.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Reset failed: {ex.Message}";
        }
    }

    private async void ExportIco_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
        {
            StatusText.Text = "Select an SVG icon first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Windows icon (*.ico)|*.ico",
            FileName = SafeName(icon.Name) + ".ico",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            double zoom = PreviewZoomSlider?.Value ?? 1.0;
            int[] sizes = [16, 24, 32, 48, 64, 128, 256];
            var images = new List<(int Size, byte[] Png)>();
            foreach (var size in sizes)
            {
                var png = await SvgExportService.RenderPngAsync(GetActiveSvgPath(icon), size, _activeIconColor, null, zoom);
                if (png is null)
                    throw new InvalidDataException($"The SVG artwork could not be rendered at {size}px.");
                images.Add((size, png));
            }

            await IcoWriter.WriteAsync(dialog.FileName, images);
            var outputDirectory = Path.GetDirectoryName(dialog.FileName) ?? Environment.CurrentDirectory;
            var masterPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(dialog.FileName) + "-512.png");
            var masterPng = await SvgExportService.RenderPngAsync(GetActiveSvgPath(icon), 512, _activeIconColor, null, zoom);
            if (masterPng is null)
                throw new InvalidDataException("The SVG artwork could not be rendered for 512px master PNG.");
            await File.WriteAllBytesAsync(masterPath, masterPng);

            StatusText.Text = $"Exported {Path.GetFileName(dialog.FileName)} and 512px master PNG";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private void LoadExportPresets()
    {
        _loadingPresets = true;
        try
        {
            if (File.Exists(ExportPresetsPath))
            {
                var saved = JsonSerializer.Deserialize<List<ExportPreset>>(File.ReadAllText(ExportPresetsPath));
                if (saved is not null)
                {
                    foreach (var preset in saved.Where(IsValidPreset))
                        _exportPresets.Add(preset);
                }
            }

            if (_exportPresets.Count == 0)
            {
                _exportPresets.Add(new ExportPreset("PNG · 512 transparent", 512, "PNG", 100, "#FFFFFF", true));
                _exportPresets.Add(new ExportPreset("WebP · 256 transparent", 256, "WebP", 90, "#FFFFFF", true));
                _exportPresets.Add(new ExportPreset("JPEG · 1024 white", 1024, "JPEG", 90, "#FFFFFF", false));
            }

            ExportPresetBox.SelectedIndex = 0;
        }
        catch
        {
            _exportPresets.Clear();
            _exportPresets.Add(new ExportPreset("PNG · 512 transparent", 512, "PNG", 100, "#FFFFFF", true));
            _exportPresets.Add(new ExportPreset("WebP · 256 transparent", 256, "WebP", 90, "#FFFFFF", true));
            _exportPresets.Add(new ExportPreset("JPEG · 1024 white", 1024, "JPEG", 90, "#FFFFFF", false));
            ExportPresetBox.SelectedIndex = 0;
        }
        finally
        {
            _loadingPresets = false;
        }

        if (ExportPresetBox.SelectedItem is ExportPreset selected)
            ApplyPreset(selected);
    }

    private static bool IsValidPreset(ExportPreset preset) =>
        !string.IsNullOrWhiteSpace(preset.Name) &&
        preset.Size is >= 1 and <= 8192 &&
        preset.Quality is >= 1 and <= 100 &&
        preset.Format is "PNG" or "WebP" or "JPEG" &&
        !string.IsNullOrWhiteSpace(preset.Background);

    private void ExportPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingPresets && ExportPresetBox.SelectedItem is ExportPreset preset)
            ApplyPreset(preset);
    }

    private void ApplyPreset(ExportPreset preset)
    {
        ExportSizeBox.Text = preset.Size.ToString();
        QualitySlider.Value = preset.Quality;
        BackgroundColorBox.Text = preset.Background;
        ExportFormatBox.SelectedIndex = preset.Format switch
        {
            "WebP" => 1,
            "JPEG" => 2,
            _ => 0
        };
        TransparentCheck.IsChecked = preset.Transparent && preset.Format != "JPEG";
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = GetExportOptions();
            var name = PromptForPresetName();
            if (string.IsNullOrWhiteSpace(name))
                return;

            var preset = new ExportPreset(name.Trim(), options.Size,
                options.Format switch
                {
                    SKEncodedImageFormat.Webp => "WebP",
                    SKEncodedImageFormat.Jpeg => "JPEG",
                    _ => "PNG"
                },
                options.Quality, BackgroundColorBox.Text.Trim(), options.Transparent);

            var existing = _exportPresets.FirstOrDefault(x =>
                x.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                _exportPresets.Remove(existing);
            _exportPresets.Add(preset);
            ExportPresetBox.SelectedItem = preset;

            Directory.CreateDirectory(Path.GetDirectoryName(ExportPresetsPath)!);
            File.WriteAllText(ExportPresetsPath,
                JsonSerializer.Serialize(_exportPresets.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = $"Saved export preset: {preset.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save preset: {ex.Message}";
        }
    }

    private string? PromptForPresetName()
    {
        var dialog = new Window
        {
            Title = "Save export preset",
            Owner = this,
            Width = 390,
            Height = 155,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("PanelBg"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextMain")
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Preset name", Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { Text = "My export preset", Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 4, 10, 4) };
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        input.Focus();
        input.SelectAll();
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityValueText is not null)
            QualityValueText.Text = $"{(int)Math.Round(QualitySlider.Value)}%";
    }

    private void ExportFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransparentCheck is null || ExportFormatBox is null)
            return;

        var format = GetSelectedFormat();
        TransparentCheck.IsEnabled = format != SKEncodedImageFormat.Jpeg;
        if (format == SKEncodedImageFormat.Jpeg)
            TransparentCheck.IsChecked = false;
    }

    private SKEncodedImageFormat GetSelectedFormat()
    {
        var label = (ExportFormatBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "PNG";
        return label switch
        {
            "WebP" => SKEncodedImageFormat.Webp,
            "JPEG" => SKEncodedImageFormat.Jpeg,
            _ => SKEncodedImageFormat.Png
        };
    }

    private (int Size, SKEncodedImageFormat Format, int Quality, SKColor Background, bool Transparent) GetExportOptions()
    {
        if (!int.TryParse(ExportSizeBox.Text.Trim(), out var size) || size is < 1 or > 8192)
            throw new InvalidDataException("Image size must be a whole number between 1 and 8192.");
        if (!BackgroundColorBox.Text.Trim().StartsWith('#') ||
            BackgroundColorBox.Text.Trim().Length is not (7 or 9))
            throw new InvalidDataException("Background color must be #RRGGBB or #AARRGGBB.");

        SKColor background;
        try
        {
            background = SKColor.Parse(BackgroundColorBox.Text.Trim());
        }
        catch
        {
            throw new InvalidDataException("Background color is not a valid hex color. Use #RRGGBB or #AARRGGBB.");
        }

        var format = GetSelectedFormat();
        return (size, format, (int)Math.Round(QualitySlider.Value), background,
            TransparentCheck.IsChecked == true && format != SKEncodedImageFormat.Jpeg);
    }

    private async void ExportImage_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
        {
            StatusText.Text = "Select an SVG icon first.";
            return;
        }

        try
        {
            double zoom = PreviewZoomSlider?.Value ?? 1.0;
            var options = GetExportOptions();
            var extension = options.Format switch
            {
                SKEncodedImageFormat.Webp => ".webp",
                SKEncodedImageFormat.Jpeg => ".jpg",
                _ => ".png"
            };
            var dialog = new SaveFileDialog
            {
                Filter = $"{options.Format} image (*{extension})|*{extension}",
                FileName = $"{SafeName(icon.Name)}-{options.Size}{extension}",
                DefaultExt = extension,
                AddExtension = true
            };
            if (dialog.ShowDialog() != true)
                return;

            var bytes = await SvgExportService.RenderImageAsync(
                GetActiveSvgPath(icon), options.Size, options.Format, options.Quality,
                options.Background, options.Transparent, _activeIconColor, zoom);
            if (bytes is null)
            {
                StatusText.Text = "The selected SVG artwork is invalid or cannot be rendered.";
                return;
            }
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            StatusText.Text = $"Exported {Path.GetFileName(dialog.FileName)} · {options.Size}×{options.Size}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Image export failed: {ex.Message}";
        }
    }

    private async void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        if (_visibleIcons.Count == 0)
        {
            StatusText.Text = "No icons match the current search and style filters.";
            return;
        }

        try
        {
            double zoom = PreviewZoomSlider?.Value ?? 1.0;
            var options = GetExportOptions();
            var extension = options.Format switch
            {
                SKEncodedImageFormat.Webp => ".webp",
                SKEncodedImageFormat.Jpeg => ".jpg",
                _ => ".png"
            };
            var dialog = new SaveFileDialog
            {
                Title = "Save batch icon pack",
                Filter = "ZIP archive (*.zip)|*.zip",
                FileName = $"FluentIconStudio-{options.Format}-{options.Size}px.zip",
                DefaultExt = ".zip",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true)
                return;

            var icons = _visibleIcons.ToArray();
            var succeeded = 0;
            var failures = new List<string>();
            var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var icon in icons)
                {
                    try
                    {
                        var source = GetActiveSvgPath(icon);
                        var bytes = await SvgExportService.RenderImageAsync(
                            source, options.Size, options.Format, options.Quality,
                            options.Background, options.Transparent, _activeIconColor, zoom);
                        if (bytes is null)
                            throw new InvalidDataException("SVG artwork is invalid or has empty bounds.");
                        var baseName = SafeName(icon.Name);
                        if (string.IsNullOrWhiteSpace(baseName))
                            baseName = "icon";
                        var entryName = baseName;
                        var suffix = 2;
                        while (!usedEntryNames.Add(entryName + extension))
                            entryName = $"{baseName}-{suffix++}";
                        var entry = archive.CreateEntry(entryName + extension, CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await entryStream.WriteAsync(bytes);
                        succeeded++;
                        StatusText.Text = $"Batch export: {succeeded}/{icons.Length} · {icon.Name}";
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{icon.Name}: {ex.Message}");
                    }
                }

                var manifest = new
                {
                    application = "FluentIconStudio",
                    createdUtc = DateTimeOffset.UtcNow,
                    format = options.Format.ToString(),
                    width = options.Size,
                    height = options.Size,
                    quality = options.Quality,
                    background = BackgroundColorBox.Text.Trim(),
                    transparent = options.Transparent,
                    sourceCount = icons.Length,
                    exportedCount = succeeded,
                    failedCount = failures.Count,
                    errors = failures
                };
                var manifestEntry = archive.CreateEntry("export-manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });
            }

            StatusText.Text = $"Batch ZIP complete · {succeeded} exported · {failures.Count} failed";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Batch export failed: {ex.Message}";
        }
    }

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
        {
            StatusText.Text = "Select an SVG icon first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = SafeName(icon.Name) + "-512.png",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            double zoom = PreviewZoomSlider?.Value ?? 1.0;
            var bytes = await SvgExportService.RenderPngAsync(GetActiveSvgPath(icon), 512, _activeIconColor, null, zoom);
            if (bytes is null)
            {
                StatusText.Text = "The selected SVG artwork is invalid or cannot be rendered.";
                return;
            }
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            StatusText.Text = $"Exported {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private void ExportSvg_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
        {
            StatusText.Text = "Select an SVG icon first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "SVG image (*.svg)|*.svg",
            FileName = SafeName(icon.Name) + ".svg",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.Copy(GetActiveSvgPath(icon), dialog.FileName, true);
            StatusText.Text = $"Exported {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void ExportAndroidPack_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
        {
            StatusText.Text = "Select an SVG icon first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Android app icon pack",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"{SafeName(icon.Name)}-android-res.zip",
            DefaultExt = ".zip",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            StatusText.Text = "Exporting Android icon pack...";
            double zoom = PreviewZoomSlider?.Value ?? 1.0;
            var activePath = GetActiveSvgPath(icon);
            var options = GetExportOptions();

            var mipmaps = new (string Density, int Size)[]
            {
                ("mipmap-mdpi", 48),
                ("mipmap-hdpi", 72),
                ("mipmap-xhdpi", 96),
                ("mipmap-xxhdpi", 144),
                ("mipmap-xxxhdpi", 192)
            };

            await using (var fileStream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                foreach (var (density, size) in mipmaps)
                {
                    var bytes = await SvgExportService.RenderImageAsync(
                        activePath, size, SKEncodedImageFormat.Png, 100, options.Background, options.Transparent, _activeIconColor, zoom);
                    if (bytes is null)
                        continue;

                    var entryLauncher = archive.CreateEntry($"res/{density}/ic_launcher.png", CompressionLevel.Optimal);
                    await using (var stream = entryLauncher.Open())
                        await stream.WriteAsync(bytes);

                    var entryRound = archive.CreateEntry($"res/{density}/ic_launcher_round.png", CompressionLevel.Optimal);
                    await using (var stream = entryRound.Open())
                        await stream.WriteAsync(bytes);

                    var entryFg = archive.CreateEntry($"res/{density}/ic_launcher_foreground.png", CompressionLevel.Optimal);
                    await using (var stream = entryFg.Open())
                        await stream.WriteAsync(bytes);
                }

                var webMasterBytes = await SvgExportService.RenderImageAsync(
                    activePath, 512, SKEncodedImageFormat.Png, 100, options.Background, options.Transparent, _activeIconColor, zoom);
                if (webMasterBytes is not null)
                {
                    var entryWeb = archive.CreateEntry("ic_launcher-web.png", CompressionLevel.Optimal);
                    await using (var stream = entryWeb.Open())
                        await stream.WriteAsync(webMasterBytes);
                }

                var adaptiveXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<adaptive-icon xmlns:android=""http://schemas.android.com/apk/res/android"">
    <background android:drawable=""@color/ic_launcher_background""/>
    <foreground android:drawable=""@mipmap/ic_launcher_foreground""/>
</adaptive-icon>";
                var entryXml = archive.CreateEntry("res/mipmap-anydpi-v26/ic_launcher.xml", CompressionLevel.Optimal);
                await using (var stream = entryXml.Open())
                await using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    await writer.WriteAsync(adaptiveXml);

                var entryXmlRound = archive.CreateEntry("res/mipmap-anydpi-v26/ic_launcher_round.xml", CompressionLevel.Optimal);
                await using (var stream = entryXmlRound.Open())
                await using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    await writer.WriteAsync(adaptiveXml);
            }

            StatusText.Text = $"Exported Android icon pack: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Android pack export failed: {ex.Message}";
        }
    }

    private async void ExportChromePack_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconFile icon)
        {
            StatusText.Text = "Select an SVG icon first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Chrome extension & PWA icon pack",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"{SafeName(icon.Name)}-chrome-extension.zip",
            DefaultExt = ".zip",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            StatusText.Text = "Exporting Chrome extension icon pack...";
            double zoom = PreviewZoomSlider?.Value ?? 1.0;
            var activePath = GetActiveSvgPath(icon);
            var options = GetExportOptions();

            int[] sizes = [16, 32, 48, 128, 512];

            await using (var fileStream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                foreach (var size in sizes)
                {
                    var bytes = await SvgExportService.RenderImageAsync(
                        activePath, size, SKEncodedImageFormat.Png, 100, options.Background, options.Transparent, _activeIconColor, zoom);
                    if (bytes is null)
                        continue;

                    var entry = archive.CreateEntry($"icons/icon{size}.png", CompressionLevel.Optimal);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(bytes);
                }

                var manifestJson = JsonSerializer.Serialize(new
                {
                    manifest_version = 3,
                    name = icon.Name,
                    version = "1.0.0",
                    icons = new Dictionary<string, string>
                    {
                        ["16"] = "icons/icon16.png",
                        ["32"] = "icons/icon32.png",
                        ["48"] = "icons/icon48.png",
                        ["128"] = "icons/icon128.png",
                        ["512"] = "icons/icon512.png"
                    }
                }, new JsonSerializerOptions { WriteIndented = true });

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var stream = manifestEntry.Open())
                await using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    await writer.WriteAsync(manifestJson);
            }

            StatusText.Text = $"Exported Chrome extension icon pack: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Chrome pack export failed: {ex.Message}";
        }
    }

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return string.Join("-", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
