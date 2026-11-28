using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace RobloxAfkManager.App.Controls;

// Binds the avatar PNG bytes a view-model exposes to an image: {Binding AvatarPng, Converter={StaticResource AvatarImage}}.
// Each byte array is decoded once; invalid or missing bytes give no image, so the initial letter underneath shows instead.
public sealed class AvatarImageConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<byte[], object> Decoded = new();
    private static readonly object None = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes) return null;
        var image = Decoded.GetValue(bytes, b => (object?)AvatarImages.Decode(b) ?? None);
        return ReferenceEquals(image, None) ? null : image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
