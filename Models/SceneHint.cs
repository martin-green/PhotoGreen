namespace PhotoGreen.Models;

public enum SceneType
{
    Unknown,
    Portrait,
    Landscape,
    Macro,
    Night,
    Indoor,
    Action,
    Architecture,
    Street
}

public record SceneHint
{
    public SceneType SceneType { get; init; } = SceneType.Unknown;
    public double ExposureBias { get; init; }
    public double ContrastBias { get; init; }
    public double TemperatureBias { get; init; }
    public double SaturationBias { get; init; }
    public double ShadowBias { get; init; }

    public static SceneHint FromSceneType(SceneType type) => type switch
    {
        SceneType.Portrait => new SceneHint
        {
            SceneType = type,
            TemperatureBias = 200,
            ContrastBias = -10,
            SaturationBias = -5,
            ShadowBias = 10
        },
        SceneType.Landscape => new SceneHint
        {
            SceneType = type,
            TemperatureBias = -100,
            ContrastBias = 15,
            SaturationBias = 15
        },
        SceneType.Night => new SceneHint
        {
            SceneType = type,
            ExposureBias = 0.3,
            ShadowBias = 25,
            ContrastBias = -10
        },
        _ => new SceneHint { SceneType = type }
    };
}
