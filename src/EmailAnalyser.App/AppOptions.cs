namespace EmailAnalyser.App;

/// <summary>
/// Command line options of the view.
/// <c>--mask</c> replaces message text with placeholders (for screen sharing and screenshots),
/// <c>--no-monitor</c> leaves continuous monitoring off, and <c>--screenshots DIR</c> captures
/// the view in a few states into DIR and exits. Screenshots are always masked and never monitor.
/// </summary>
public sealed record AppOptions(bool Mask, bool Monitor, string? ScreenshotDirectory)
{
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var mask = false;
        var monitor = true;
        string? screenshots = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--mask" or "--peita":
                    mask = true;
                    break;
                case "--no-monitor":
                    monitor = false;
                    break;
                case "--screenshots" when i + 1 < args.Count:
                    screenshots = args[++i];
                    break;
            }
        }

        return screenshots is null
            ? new AppOptions(mask, monitor, null)
            : new AppOptions(Mask: true, Monitor: false, screenshots);
    }
}
