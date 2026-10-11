using System.Text.Json.Serialization;

namespace GenshinVideoHelper.Core.Settings;

/// <summary>Physical screen pixels for the complete PiP frame, including its bar and border.</summary>
public sealed record PipPlacement(int Left, int Top, int Width, int Height)
{
    [JsonIgnore]
    public bool IsValid => Width is > 0 and <= 32768 && Height is > 0 and <= 32768 &&
        (long)Left + Width <= int.MaxValue && (long)Top + Height <= int.MaxValue;
}
