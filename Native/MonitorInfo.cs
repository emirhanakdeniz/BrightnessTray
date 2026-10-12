namespace BrightnessTray.Native;

public sealed class MonitorInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required uint MinBrightness { get; init; }
    public required uint CurrentBrightness { get; init; }
    public required uint MaxBrightness { get; init; }

    public int CurrentPercent =>
        MaxBrightness > MinBrightness
            ? (int)Math.Round((double)(Math.Clamp(CurrentBrightness, MinBrightness, MaxBrightness) - MinBrightness)
                * 100 / (MaxBrightness - MinBrightness))
            : 0;

    public override string ToString() =>
        $"{Name} [{Id}] - {CurrentPercent}% (Min: {MinBrightness}, Current: {CurrentBrightness}, Max: {MaxBrightness})";
}
