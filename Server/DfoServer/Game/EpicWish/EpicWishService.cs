using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Game.EpicWish;

internal interface IEpicWishStore
{
    bool SaveEpicWish(AccountOwners.Lease owner, Character expected, EpicWishList wishes);
}

internal sealed class EpicWishService(IEpicWishStore store, Func<int, bool> eligible)
{
    public bool Eligible(EpicWishList wishes)
    {
        wishes.Validate();
        return wishes.Items.All(id => id == 0 || eligible(id));
    }

    public bool Save(AccountOwners.Lease owner, Character expected, EpicWishList wishes) =>
        store.SaveEpicWish(owner, expected, wishes);
}
