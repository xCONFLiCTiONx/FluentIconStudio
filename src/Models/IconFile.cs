using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace FluentIconStudio.Models;

public sealed class IconFile : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public string Style { get; init; } = "SVG";
    public string SearchText => $"{Name} {Style}";

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public override string ToString() => Name;
}
