using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>Momento de la partida, en tres tramos: antes de 14 min, hasta 25 min y después.</summary>
public enum GamePhase { Early, Mid, Late }

public static class GamePhases
{
    public const double MidFrom = 14 * 60, LateFrom = 25 * 60;

    public static GamePhase From(double gameSeconds) =>
        gameSeconds < MidFrom ? GamePhase.Early : gameSeconds < LateFrom ? GamePhase.Mid : GamePhase.Late;

    public static string Label(GamePhase phase) => phase switch
    {
        GamePhase.Early => L("Inicio de partida", "Early game"),
        GamePhase.Mid => L("Mitad de partida", "Mid game"),
        _ => L("Final de partida", "Late game"),
    };
}
