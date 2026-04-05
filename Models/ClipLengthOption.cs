namespace Clipper.Models;

public sealed class ClipLengthOption
{
    public string Display { get; }
    public int Seconds { get; }

    public ClipLengthOption(string display, int seconds)
    {
        Display = display;
        Seconds = seconds;
    }

    public override string ToString() => Display;
}
