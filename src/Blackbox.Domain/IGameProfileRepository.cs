namespace Blackbox.Domain;

public interface IGameProfileRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(GameProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(string executablePath, CancellationToken cancellationToken = default);
}
