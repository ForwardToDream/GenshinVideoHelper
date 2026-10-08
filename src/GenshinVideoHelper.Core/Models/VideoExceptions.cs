namespace GenshinVideoHelper.Core.Models;

public sealed class VideoNotReadyException(string message) : InvalidOperationException(message);
