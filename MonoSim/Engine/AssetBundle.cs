namespace MonoSim;

public sealed record AssetBundle
{
    public string[] PropertyIds { get; init; } = [];
    public string[] CardIds { get; init; } = [];
}
