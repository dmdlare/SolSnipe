using LiteDB;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;

namespace SolSnipe.Data.Repositories;

public class PositionRepository : IPositionRepository
{
    private readonly ILiteCollection<Position> _col;

    public PositionRepository(SolSnipeDb db)
    {
        _col = db.GetCollection<Position>("positions");
        _col.EnsureIndex(x => x.TokenMint);
        _col.EnsureIndex(x => x.Status);
    }

    public void Upsert(Position position) => _col.Upsert(position);

    public List<Position> GetOpen() =>
        _col.Find(x => x.Status == PositionStatus.Open).ToList();

    public List<Position> GetClosed(int limit = 50) =>
        _col.Find(x => x.Status == PositionStatus.Closed)
            .OrderByDescending(x => x.ClosedAt)
            .Take(limit)
            .ToList();

    public Position? GetByMint(string tokenMint) =>
        _col.FindOne(x => x.TokenMint == tokenMint && x.Status == PositionStatus.Open);

    public bool HasOpenPosition(string tokenMint) =>
        _col.Exists(x => x.TokenMint == tokenMint && x.Status == PositionStatus.Open);
}