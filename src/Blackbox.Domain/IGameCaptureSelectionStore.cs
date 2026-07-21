namespace Blackbox.Domain;

public interface IGameCaptureSelectionStore
{
    GameCaptureSelection? Current { get; }
    void Save(GameCaptureSelection selection);
    void Clear();
}
