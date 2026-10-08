using Library;
using Server.DBModels;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Models
{
    public partial class MonsterObject
    {
        public string CombatPetDisplayName => !IsCombatPet ? null :
            CombatPetLevel >= CombatPetSettings.MaxLevel ? $"{MonsterInfo.MonsterName} [Lv. {CombatPetLevel} | MAX]" :
            $"{MonsterInfo.MonsterName} [Lv. {CombatPetLevel} | EXP {(100m * CombatPetExperience / CombatPetSettings.ExperienceRequired(CombatPetLevel)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%]";
        public System.Drawing.Color CombatPetNameColour => CombatPetLevel switch
        {
            >= 12 => System.Drawing.Color.Gold, >= 8 => System.Drawing.Color.Orchid,
            >= 7 => System.Drawing.Color.LightSkyBlue, >= 1 => System.Drawing.Color.LightGreen,
            _ => System.Drawing.Color.White
        };
        public void BroadcastCombatPetProgress()
        {
            Broadcast(new Library.Network.ServerPackets.ObjectPetOwnerChanged
            { ObjectID = ObjectID, PetOwner = PetOwner?.Name, CustomName = CombatPetDisplayName });
            ProcessNameColour();
            Broadcast(new Library.Network.ServerPackets.ObjectNameColour { ObjectID = ObjectID, Colour = NameColour });
        }
        public int CombatPetLevel { get; set; }
        public long CombatPetExperience { get; set; }
        public UserCombatPet SavedCombatPet { get; set; }
        public bool PreserveCombatPetOnDespawn { get; set; }

        private readonly Dictionary<MonsterObject, PlayerObject> combatPetAttackers = new();

        public UserMagic CombatPetMagic => Magics.FirstOrDefault(x =>
            x.Info != null && CombatPetSettings.IsSupported(x.Info.Magic));

        public bool IsCombatPet => PetOwner != null && CombatPetMagic != null;

        public void ForgetCombatPet()
        {
            SavedCombatPet?.Delete();
            SavedCombatPet = null;
        }

        public void ResetCombatPetProgress()
        {
            ForgetCombatPet();
            CombatPetLevel = 0;
            CombatPetExperience = 0;
            combatPetAttackers.Clear();
        }

        private void ApplyCombatPetStats()
        {
            if (!IsCombatPet) return;
            int bonus = (CombatPetLevel - 6) * CombatPetSettings.StatBonusPerLevel;
            foreach (Stat stat in new[] { Stat.Health, Stat.MinAC, Stat.MaxAC, Stat.MinMR, Stat.MaxMR,
                Stat.MinDC, Stat.MaxDC, Stat.MinMC, Stat.MaxMC, Stat.MinSC, Stat.MaxSC, Stat.Accuracy, Stat.Agility })
                Stats[stat] = (int)Math.Clamp(Stats[stat] + (long)Stats[stat] * bonus / 100, 0, int.MaxValue);
        }

        private void RecordCombatPetHit(MapObject attacker, int damage)
        {
            if (damage <= 0 || PetOwner != null || Experience <= 0) return;
            if (attacker is MonsterObject pet && pet.IsCombatPet && !pet.Dead)
                combatPetAttackers[pet] = pet.PetOwner;
        }

        private void RewardCombatPets()
        {
            if (PetOwner == null && Experience > 0 && Config.CombatPetExperienceEnabled)
            {
                var participants = combatPetAttackers.Where(x =>
                    x.Key.IsCombatPet && !x.Key.Dead && x.Key.Node != null &&
                    x.Key.PetOwner == x.Value && !x.Value.Dead && x.Value.Node != null &&
                    x.Key.CurrentMap == CurrentMap && EXPOwner == x.Value).Select(x => x.Key).ToArray();
                foreach (MonsterObject pet in participants)
                {
                    int referenceLevel = Math.Clamp(Math.Max(pet.PetOwner.Level, pet.MonsterInfo.Level), 1, 1000000);
                    long points = (long)Math.Clamp(Config.CombatPetExperiencePerKill, 1, 1000000) *
                        Math.Clamp(MonsterInfo.Level, 1, referenceLevel * 2) / referenceLevel;
                    pet.GainCombatPetExperience(Math.Max(1, points / participants.Length));
                }
            }
            combatPetAttackers.Clear();
        }

        public void GainCombatPetExperience(long amount)
        {
            if (!Config.CombatPetExperienceEnabled || !IsCombatPet || Dead || amount <= 0 ||
                CombatPetLevel >= CombatPetSettings.MaxLevel) return;
            CombatPetExperience = Math.Min(Math.Max(0, CombatPetExperience), long.MaxValue - amount) + amount;
            int previousLevel = CombatPetLevel;
            while (CombatPetLevel < CombatPetSettings.MaxLevel &&
                   CombatPetExperience >= CombatPetSettings.ExperienceRequired(CombatPetLevel))
            {
                CombatPetExperience -= CombatPetSettings.ExperienceRequired(CombatPetLevel);
                CombatPetLevel++;
            }
            if (CombatPetLevel >= CombatPetSettings.MaxLevel) CombatPetExperience = 0;
            if (CombatPetLevel != previousLevel)
            {
                RefreshStats();
                // Increasing maximum HP never heals the pet for free.
                PetOwner.Connection?.ReceiveChat(
                    $"Pet {MonsterInfo.MonsterName}: poziom walki {CombatPetLevel}/{CombatPetSettings.MaxLevel}.",
                    MessageType.System);
            }
            PetOwner.SaveCombatPet(this);
            BroadcastCombatPetProgress();
        }
    }
}
