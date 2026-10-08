using Library;
using Server.DBModels;
using Server.Envir;
using System;
using System.Linq;
using C = Library.Network.ClientPackets;
using S = Library.Network.ServerPackets;

namespace Server.Models
{
    public partial class PlayerObject
    {
        private void SendGuildFragmentState(GuildInfo guild, string message = null, bool broadcast = false)
        {
            var packet = new S.GuildFragmentState
            {
                Capacity = guild.FragmentStorageSize,
                Revision = guild.FragmentRevision,
                Items = guild.FragmentStorage.Where(x => x != null).Select(x => x.ToClientInfo()).ToList(),
                Message = message,
                ObserverPacket = false
            };
            if (!broadcast) { Enqueue(packet); return; }
            foreach (GuildMemberInfo member in guild.Members)
                member.Account.Connection?.Player?.Enqueue(packet);
        }

        public void GuildFragmentOperation(C.GuildFragmentOperation p)
        {
            GuildMemberInfo member = Character.Account.GuildMember;
            if (member?.Guild == null) return;
            GuildInfo guild = member.Guild;
            // Receive threads only enqueue; mutations remain synchronous in the simulation loop.
            lock (guild.FragmentGate)
            {
                if (Dead || Observer || TradePartner != null || !InSafeZone)
                {
                    SendGuildFragmentState(guild, "Operacja wymaga bezpiecznej strefy, zywej postaci i zakonczenia handlu.");
                    return;
                }
                GuildPermission permission;
                switch (p.Action)
                {
                    case GuildFragmentAction.Deposit: permission = GuildPermission.FragmentDeposit; break;
                    case GuildFragmentAction.Withdraw: permission = GuildPermission.FragmentWithdraw; break;
                    case GuildFragmentAction.Assemble: permission = GuildPermission.FragmentAssemble; break;
                    case GuildFragmentAction.Expand: permission = GuildPermission.Leader; break;
                    default: SendGuildFragmentState(guild, "Nieprawidlowa operacja."); return;
                }
                if ((member.Permission & permission) != permission)
                {
                    SendGuildFragmentState(guild, "Brak uprawnienia do tej operacji.");
                    return;
                }
                if (p.Revision != guild.FragmentRevision)
                {
                    SendGuildFragmentState(guild, "Magazyn zmienil sie. Sprawdz stan i ponow operacje.");
                    return;
                }

                bool success = false;
                string message;
                switch (p.Action)
                {
                    case GuildFragmentAction.Deposit: success = DepositGuildFragments(guild, p, out message); break;
                    case GuildFragmentAction.Withdraw: success = WithdrawGuildFragments(guild, p, out message); break;
                    case GuildFragmentAction.Assemble: success = AssembleGuildFragments(guild, p, out message); break;
                    default:
                        message = "Nie mozna powiekszyc magazynu: limit lub brak funduszy.";
                        if (guild.FragmentStorageSize < GuildFragmentSettings.MaxCapacity && guild.GuildFunds >= Globals.GuildStorageCost)
                        {
                            guild.GuildFunds -= Globals.GuildStorageCost;
                            guild.DailyGrowth -= Globals.GuildStorageCost;
                            guild.FragmentStorageSize++;
                            foreach (GuildMemberInfo m in guild.Members)
                                m.Account.Connection?.Player?.Enqueue(guild.GetUpdatePacket());
                            message = "Dodano miejsce w magazynie fragmentow.";
                            success = true;
                        }
                        break;
                }
                if (success)
                {
                    guild.FragmentRevision++;
                    SEnvir.Log($"[GuildFragments] Guild={guild.Index} Player={Character.Index} Action={p.Action} Item={p.ItemIndex} Count={p.Count} Revision={guild.FragmentRevision}");
                }
                SendGuildFragmentState(guild, message, success);

            }
        }

        private bool DepositGuildFragments(GuildInfo guild, C.GuildFragmentOperation p, out string message)
        {
            message = "Nie mozna wplacic: sprawdz fragment, ilosc i wolne miejsca.";
            UserItem[] source;
            switch (p.SourceGrid)
            {
                case GridType.Inventory: source = Inventory; break;
                case GridType.Storage: source = Storage; break;
                case GridType.PartsStorage: source = PartsStorage; break;
                default: return false;
            }
            if (p.Slot < 0 || p.Slot >= source.Length) return false;
            if (p.SourceGrid == GridType.Storage && p.Slot >= Character.Account.StorageSize) return false;
            UserItem item = source[p.Slot];
            if (item == null || item.Index != p.ItemIndex) return false;
            var plan = GuildFragmentStorage.PlanDeposit(guild, item, p.Count);
            if (plan == null) return false;

            // Prepare new records before touching the source or existing guild stacks.
            var fresh = plan.Where(x => guild.FragmentStorage[x.Slot] == null)
                .Select(x => (x.Slot, Item: SEnvir.CreateFreshItem(item), x.Count)).ToList();
            foreach (var entry in fresh)
            {
                entry.Item.Count = entry.Count;
                entry.Item.Guild = guild;
                entry.Item.Slot = GuildFragmentSettings.SlotOffset + entry.Slot;
                guild.FragmentStorage[entry.Slot] = entry.Item;
            }
            foreach (var entry in plan)
                if (!fresh.Any(x => x.Slot == entry.Slot))
                    guild.FragmentStorage[entry.Slot].Count += entry.Count;
            item.Count -= p.Count;
            long remaining = item.Count;
            if (remaining == 0)
            {
                source[p.Slot] = null;
                RemoveItem(item);
                item.Delete();
            }
            Enqueue(new S.ItemChanged { Link = new CellLinkInfo { GridType = p.SourceGrid, Slot = p.Slot, Count = remaining }, Success = true });
            RefreshWeight();
            message = "Wplacono fragmenty.";
            return true;
        }

        private bool WithdrawGuildFragments(GuildInfo guild, C.GuildFragmentOperation p, out string message)
        {
            message = "Nie mozna wyplacic: sprawdz ilosc oraz miejsce i udzwig plecaka.";
            if (p.Slot < 0 || p.Slot >= guild.FragmentStorageSize || p.Slot >= guild.FragmentStorage.Length) return false;
            UserItem item = guild.FragmentStorage[p.Slot];
            if (!GuildFragmentStorage.Eligible(item) || item.Index != p.ItemIndex || p.Count <= 0 || p.Count > item.Count) return false;
            if (!CanGainItems(true, new ItemCheck(item, p.Count, item.Flags, item.ExpireTime))) return false;
            UserItem gain = SEnvir.CreateFreshItem(item);
            gain.Count = p.Count;
            item.Count -= p.Count;
            if (item.Count == 0)
            {
                guild.FragmentStorage[p.Slot] = null;
                item.Delete();
            }
            GainItem(gain);
            message = "Wyplacono fragmenty do plecaka.";
            return true;
        }

        private bool AssembleGuildFragments(GuildInfo guild, C.GuildFragmentOperation p, out string message)
        {
            message = "Nie mozna zlozyc: brak czesci, nieprawidlowa receptura lub pelny magazyn gildii.";
            if (p.Count != 1 || p.Slot < 0 || p.Slot >= guild.FragmentStorage.Length) return false;
            UserItem selected = guild.FragmentStorage[p.Slot];
            if (!GuildFragmentStorage.Eligible(selected) || selected.Index != p.ItemIndex) return false;
            int targetIndex = selected.Stats[Stat.ItemIndex];
            var target = SEnvir.ItemInfoList.Binding.FirstOrDefault(x => x.Index == targetIndex);
            if (target == null || target.PartCount <= 0 || target.ItemType == ItemType.ItemPart ||
                !target.CanTrade || SEnvir.IsCurrencyItem(target) || target.ItemEffect == ItemEffect.Experience) return false;
            var plan = GuildFragmentStorage.PlanAssembly(guild, target.Index, target.PartCount);
            if (plan == null) return false;
            int slot = Array.FindIndex(guild.Storage, 0, Math.Min(guild.StorageSize, guild.Storage.Length), x => x == null);
            if (slot < 0) return false;
            UserItem result = GuildFragmentStorage.CommitAssembly(guild, plan, slot, () => SEnvir.CreateDropItem(target, 2));
            if (result == null) return false;
            var packet = new S.GuildNewItem { Slot = slot, Item = result.ToClientInfo(), ObserverPacket = false };
            foreach (GuildMemberInfo m in guild.Members)
                m.Account.Connection?.Player?.Enqueue(packet);
            SEnvir.Log($"[GuildFragmentsCraft] Guild={guild.Index} Player={Character.Index} Recipe={target.Index} Materials={target.PartCount} Result={result.Index}");
            message = $"Zlozono {target.ItemName}. Przedmiot jest w magazynie gildii.";
            return true;
        }
    }
}