namespace BorsdataMcp;

public sealed class ScreeningOptions
{
    public int DefaultPageSize { get; set; } = 50;
    public int MaxPageSize { get; set; } = 200;
    public TimeSpan SnapshotTtl { get; set; } = TimeSpan.FromMinutes(15);
}
