using System.Windows.Media.Imaging;

namespace TabTty.App;

internal static class WindowIcons
{
    private static readonly IconBitmapDecoder AppIcon = Load("TabTty.ico");

    public static BitmapFrame App => AppIcon.Frames[0];
    public static BitmapFrame Welcome { get; } = AppIcon.Frames.MaxBy(frame => frame.PixelWidth)!;
    public static BitmapFrame Vault { get; } = Load("tttykey.ico").Frames[0];

    private static IconBitmapDecoder Load(string fileName) =>
        new(new Uri($"pack://application:,,,/TabTty;component/Assets/{fileName}", UriKind.Absolute),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
}
