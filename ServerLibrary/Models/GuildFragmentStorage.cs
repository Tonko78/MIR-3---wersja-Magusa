using Library;
using Server.DBModels;
using System;
using System.Collections.Generic;

namespace Server.Models
{
    public static class GuildFragmentStorage
    {
        public static bool Eligible(UserItem item) =>
            item?.Info?.ItemType == ItemType.ItemPart && item.Info.ItemEffect == ItemEffect.ItemPart &&
            item.Info.CanTrade && item.Count > 0 && item.Info.StackSize > 0 &&
            (item.Flags & (UserItemFlags.Bound | UserItemFlags.Locked | UserItemFlags.Marriage |
                           UserItemFlags.QuestItem | UserItemFlags.Expirable)) == 0 &&
            item.Socket == null && item.Sockets.Count == 0;

        public static bool Matches(UserItem a, UserItem b) =>
            Eligible(a) && Eligible(b) && a.Info == b.Info && a.Flags == b.Flags &&
            a.Level == b.Level && a.Experience == b.Experience &&
            a.CurrentDurability == b.CurrentDurability && a.MaxDurability == b.MaxDurability &&
            a.Stats.Compare(b.Stats);

        // Plan before changing any stack. Count is bounded by the authoritative source stack.
        public static List<(int Slot, long Count)> PlanDeposit(GuildInfo guild, UserItem source, long count)
        {
            if (!Eligible(source) || count <= 0 || count > source.Count) return null;
            var plan = new List<(int, long)>();
            long remaining = count;
            for (int pass = 0; pass < 2; pass++)
                for (int slot = 0; slot < Math.Min(guild.FragmentStorageSize, guild.FragmentStorage.Length); slot++)
                {
                    UserItem target = guild.FragmentStorage[slot];
                    long room;
                    if (pass == 0)
                    {
                        if (target == null || !Matches(source, target)) continue;
                        room = Math.Max(0, target.Info.StackSize - target.Count);
                    }
                    else
                    {
                        if (target != null) continue;
                        room = source.Info.StackSize;
                    }
                    long amount = Math.Min(room, remaining);
                    if (amount <= 0) continue;
                    plan.Add((slot, amount));
                    remaining -= amount;
                    if (remaining == 0) return plan;
                }
            return null;
        }

        public static List<(int Slot, long Count)> PlanAssembly(GuildInfo guild, int targetIndex, long required)
        {
            if (targetIndex <= 0 || required <= 0) return null;
            var plan = new List<(int, long)>();
            for (int slot = 0; slot < guild.FragmentStorage.Length; slot++)
            {
                UserItem item = guild.FragmentStorage[slot];
                if (!Eligible(item) || item.Stats[Stat.ItemIndex] != targetIndex) continue;
                long count = Math.Min(required, item.Count);
                plan.Add((slot, count));
                required -= count;
                if (required == 0) return plan;
            }
            return null;
        }

        // Call only from the simulation thread, under FragmentGate after all validation.
        // Allocate the result before consuming materials so allocation failure cannot eat fragments.
        public static UserItem CommitAssembly(GuildInfo guild, List<(int Slot, long Count)> plan,
                                               int destination, Func<UserItem> createResult)
        {
            UserItem result = createResult();
            if (result == null) return null;
            result.Guild = guild;
            result.Slot = destination;
            guild.Storage[destination] = result;
            foreach (var entry in plan)
            {
                UserItem part = guild.FragmentStorage[entry.Slot];
                part.Count -= entry.Count;
                if (part.Count != 0) continue;
                guild.FragmentStorage[entry.Slot] = null;
                part.Delete();
            }
            return result;
        }
    }
}