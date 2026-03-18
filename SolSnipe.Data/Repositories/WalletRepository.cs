using LiteDB;
using SolSnipe.Core.Interfaces;
using SolSnipe.Core.Models;

namespace SolSnipe.Data.Repositories;

public class WalletRepository : IWalletRepository
{
    private readonly ILiteCollection<TrackedWallet> _col;

    public WalletRepository(SolSnipeDb db)
    {
        _col = db.GetCollection<TrackedWallet>("wallets");
        _col.EnsureIndex(x => x.Address, unique: true);
        _col.EnsureIndex(x => x.IsActive);
    }

    public void Upsert(TrackedWallet wallet) => _col.Upsert(wallet);

    public void UpsertMany(IEnumerable<TrackedWallet> wallets)
    {
        foreach (var w in wallets) _col.Upsert(w);
    }

    public List<TrackedWallet> GetActive() =>
        _col.Find(x => x.IsActive).OrderByDescending(x => x.Score).ToList();

    public List<TrackedWallet> GetAll() =>
        _col.FindAll().OrderByDescending(x => x.Score).ToList();

    public TrackedWallet? GetByAddress(string address) =>
        _col.FindOne(x => x.Address == address);

    public void Deactivate(string address)
    {
        var wallet = _col.FindOne(x => x.Address == address);
        if (wallet is null) return;
        wallet.IsActive = false;
        _col.Update(wallet);
    }

    public int Count() => _col.Count();
}