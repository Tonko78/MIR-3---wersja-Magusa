using System;
using System.Linq;
using Library;
using Server.Envir;
using S = Library.Network.ServerPackets;
namespace Server.Models
{
    public partial class PlayerObject
    {
        private static DateTime nextRecyclePurge;
        private DateTime nextRecycleState;
        private int lastRecycleCount = -1, lastRecycleSeconds = -1;
        public static void PurgeExpiredRecycledItems(DateTime utcNow)
        {
            if (utcNow < nextRecyclePurge || SEnvir.UserItemList == null) return;
            nextRecyclePurge = utcNow.AddSeconds(1);
            foreach (var item in SEnvir.UserItemList.Binding.Where(x => x.RecycleExpiresUtc != DateTime.MinValue && x.RecycleExpiresUtc <= utcNow).ToArray())
                item.Delete();
        }
        private void ProcessRecycleState()
        {
            if (DateTime.UtcNow < nextRecycleState) return;
            nextRecycleState = DateTime.UtcNow.AddSeconds(1);
            SendRecycleState();
        }
        public void SendRecycleState()
        {
            if (Character == null) return;
            var pending = Character.RecycledItems.Where(x => x.RecycleExpiresUtc > DateTime.UtcNow).ToArray();
            int seconds = pending.Length == 0 ? 0 : Math.Max(0,(int)Math.Ceiling((pending.Max(x=>x.RecycleExpiresUtc)-DateTime.UtcNow).TotalSeconds));
            if (pending.Length == lastRecycleCount && seconds == lastRecycleSeconds) return;
            lastRecycleCount = pending.Length; lastRecycleSeconds = seconds;
            Enqueue(new S.ItemRecycleState { Count = pending.Length, SecondsRemaining = seconds });
        }
        public void RecoverDeletedItem()
        {
            if (Character == null || Dead || Observer) return;
            var item = Character.RecycledItems.Where(x=>x.RecycleExpiresUtc > DateTime.UtcNow).OrderByDescending(x=>x.RecycleExpiresUtc).FirstOrDefault();
            if (item == null) { SendRecycleState(); return; }
            int slot = item.RecycleOriginalSlot >= 0 && item.RecycleOriginalSlot < Inventory.Length && Inventory[item.RecycleOriginalSlot] == null ? item.RecycleOriginalSlot : Array.FindIndex(Inventory,x=>x==null);
            if (slot < 0 || !CanGainItems(true, new ItemCheck(item,item.Count,item.Flags,item.ExpireTime)))
            { Connection?.ReceiveChat("Nie można cofnąć: zwolnij miejsce lub zmniejsz obciążenie plecaka przed upływem 45 sekund.", MessageType.System); return; }
            item.RecycleOwner = null; item.RecycleExpiresUtc = DateTime.MinValue; item.RecycleOriginalSlot = 0;
            item.Character = Character; item.Slot = slot; Inventory[slot] = item;
            Enqueue(new S.ItemRecovered { Item = item.ToClientInfo() });
            RefreshWeight(); SendRecycleState();
        }
    }
}
