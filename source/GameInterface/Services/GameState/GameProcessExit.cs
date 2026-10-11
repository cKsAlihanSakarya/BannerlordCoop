namespace GameInterface.Services.GameState;

/// <summary>Ends the game process the way the main menu's Exit does, so it exits cleanly.</summary>
public interface IGameProcessExit
{
    /// <summary>Must run on the game thread.</summary>
    void Quit();
}

/// <inheritdoc cref="IGameProcessExit"/>
public class GameProcessExit : IGameProcessExit
{
    public void Quit() => TaleWorlds.Engine.Utilities.QuitGame();
}
