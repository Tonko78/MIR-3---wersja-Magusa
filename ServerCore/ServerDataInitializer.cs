using Library;
using Library.SystemModels;
using MirDB;
using Server.DBModels;
using System.Linq;
using System.Reflection;

namespace Server
{
    internal static class ServerDataInitializer
    {
        public static void EnsureDefaultCurrencies()
        {
            var session = new Session(SessionMode.System)
            {
                BackUpDelay = 0
            };

            session.Initialize(
                Assembly.GetAssembly(typeof(ItemInfo)),
                Assembly.GetAssembly(typeof(AccountInfo))
            );

            bool needSave = false;

            var gold = session.GetCollection<CurrencyInfo>().Binding.FirstOrDefault(x => x.Type == CurrencyType.Gold);
            var goldItem = session.GetCollection<ItemInfo>().Binding.FirstOrDefault(x => x.ItemName == "Gold");

            if (goldItem == null)
            {
                goldItem = session.GetCollection<ItemInfo>().CreateNewObject();
                goldItem.ItemName = "Gold";
                goldItem.ItemType = ItemType.Currency;
                goldItem.StackSize = 25000;
                goldItem.Image = 121;
                goldItem.SellRate = 0;
                goldItem.CanDrop = true;
                needSave = true;
            }

            if (gold == null)
            {
                gold = session.GetCollection<CurrencyInfo>().CreateNewObject();
                gold.Name = "Gold";
                gold.Type = CurrencyType.Gold;
                gold.Category = CurrencyCategory.Basic;
                gold.DropItem = goldItem;
                goldItem.ItemEffect = ItemEffect.None;
                goldItem.ItemType = ItemType.Currency;
                needSave = true;
            }

            if (string.IsNullOrEmpty(gold.Abbreviation))
            {
                gold.Abbreviation = "Gold";
                needSave = true;
            }

            if (gold.Images.Count == 0)
            {
                AddImage(session, gold, 120, 0);
                AddImage(session, gold, 121, 100);
                AddImage(session, gold, 122, 200);
                AddImage(session, gold, 123, 500);
                AddImage(session, gold, 124, 1000);
                AddImage(session, gold, 125, 1000000);
                AddImage(session, gold, 126, 5000000);
                AddImage(session, gold, 127, 10000000);
                needSave = true;
            }

            EnsureCurrency(session, CurrencyType.GameGold, "Game Gold", "GG", CurrencyCategory.Other, ref needSave);
            EnsureCurrency(session, CurrencyType.HuntGold, "Hunt Gold", "HG", CurrencyCategory.Other, ref needSave);

            var famePoint = EnsureCurrency(session, CurrencyType.FP, "Fame Point", "FP", CurrencyCategory.Player, ref needSave);
            EnsureCurrencyItem(session, famePoint, "Fame Point", 4010, false, ref needSave);

            var contributionPoint = EnsureCurrency(session, CurrencyType.CP, "Contribution Point", "CP", CurrencyCategory.Player, ref needSave);
            EnsureCurrencyItem(session, contributionPoint, "Contribution Point", 4012, false, ref needSave);

            foreach (var currency in session.GetCollection<CurrencyInfo>().Binding)
                if (currency.DropItem != null)
                    currency.DropItem.ItemType = ItemType.Currency;

            if (needSave)
                session.Save(true);
        }

        private static CurrencyInfo EnsureCurrency(Session session, CurrencyType type, string name, string abbreviation, CurrencyCategory category, ref bool needSave)
        {
            var currency = session.GetCollection<CurrencyInfo>().Binding.FirstOrDefault(x => x.Type == type);
            if (currency == null)
            {
                currency = session.GetCollection<CurrencyInfo>().CreateNewObject();
                currency.Name = name;
                currency.Type = type;
                currency.Category = category;
                needSave = true;
            }

            if (string.IsNullOrEmpty(currency.Abbreviation))
            {
                currency.Abbreviation = abbreviation;
                needSave = true;
            }

            return currency;
        }

        private static void EnsureCurrencyItem(Session session, CurrencyInfo currency, string itemName, int image, bool canDrop, ref bool needSave)
        {
            var item = session.GetCollection<ItemInfo>().Binding.FirstOrDefault(x => x.ItemName == itemName);
            if (item == null)
            {
                item = session.GetCollection<ItemInfo>().CreateNewObject();
                item.ItemName = itemName;
                item.ItemType = ItemType.Currency;
                item.StackSize = 25000;
                item.Image = image;
                item.CanDrop = canDrop;
                needSave = true;
            }

            if (currency.DropItem == null)
            {
                currency.DropItem = item;
                needSave = true;
            }
        }

        private static void AddImage(Session session, CurrencyInfo currency, int imageIndex, long amount)
        {
            var image = session.GetCollection<CurrencyInfoImage>().CreateNewObject();
            image.Image = imageIndex;
            image.Amount = amount;
            currency.Images.Add(image);
        }
    }
}
