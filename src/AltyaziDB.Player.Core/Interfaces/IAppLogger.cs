namespace AltyaziDB.Player.Core.Interfaces;

public interface IAppLogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}
