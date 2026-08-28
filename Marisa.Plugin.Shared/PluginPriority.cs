namespace Marisa.Plugin.Shared;

public static class PluginPriority
{
    // Confirmation codes must be consumed even when the sender is blacklisted. Otherwise
    // BlackList can swallow a wrong-sender attempt before the one-time proof is burned.
    public const int DivingFishConfirmation = 12;
    public const int BlackList = 11;
    public const int WordCloud = 10;
    public const int Dialog = 9;
    public const int Command = 8;
    public const int MaiMaiDx = 4;
    public const int Osu = 3;
    public const int Chunithm = 2;
    public const int Arcaea = 1;
    public const int Game = -1;
}
