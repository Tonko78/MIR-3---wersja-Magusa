using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using System;
using System.Linq;

namespace Server.Models
{
    public partial class PlayerObject
    {
        public void SaveCombatPet(MonsterObject pet)
        {
            if (!Config.PersistCombatPets || Character == null || SEnvir.Session == null ||
                pet.PetOwner != this || !pet.IsCombatPet || pet.Dead || pet.CurrentHP <= 0 || pet.Node == null) return;
            var record = pet.SavedCombatPet;
            if (record == null)
            {
                record = SEnvir.Session.GetCollection<UserCombatPet>().CreateNewObject();
                record.Character = Character;
                pet.SavedCombatPet = record;
            }
            record.Monster = pet.MonsterInfo;
            record.SourceMagic = pet.CombatPetMagic.Info.Magic;
            record.Health = pet.CurrentHP;
            record.CombatLevel = pet.CombatPetLevel;
            record.Experience = pet.CombatPetExperience;
            record.SummonLevel = pet.SummonLevel;
            record.RemainingTime = pet.TameTime == DateTime.MaxValue ? TimeSpan.MaxValue :
                (pet.TameTime > SEnvir.Now ? pet.TameTime - SEnvir.Now : TimeSpan.Zero);
            record.FaithMagicIndex = pet.Magics.FirstOrDefault(x => x.Info?.Magic == MagicType.StrengthOfFaith)?.Index ?? 0;
            record.RecoveryMagicIndex = pet.Magics.FirstOrDefault(x => x.Info?.Magic == MagicType.DemonicRecovery)?.Index ?? 0;
        }

        public void SaveCombatPets()
        {
            if (Character == null) return;
            foreach (MonsterObject pet in Pets.ToArray()) SaveCombatPet(pet);
        }

        // A saved pet awaiting a safe spawn cell still occupies its original slot.
        public int CombatPetSlotCount => Pets.Count + (Config.PersistCombatPets && Character != null ?
            Character.CombatPets.Count(record => !Pets.Any(pet => pet.SavedCombatPet == record)) : 0);

        public bool HasPendingCombatPet(MonsterInfo monster) => Config.PersistCombatPets && Character != null &&
            Character.CombatPets.Any(record => record.Monster == monster && !Pets.Any(pet => pet.SavedCombatPet == record));

        public void RestoreCombatPets()
        {
            if (!Config.PersistCombatPets || Character == null || Dead || Observer || CurrentCell == null) return;
            int limit = Class == MirClass.Wizard || Class == MirClass.Taoist ? CombatPetSettings.MaxCount : 0;
            foreach (UserCombatPet record in Character.CombatPets.ToArray())
            {
                if (Pets.Any(x => x.SavedCombatPet == record)) continue;
                UserMagic magic = Character.Magics.FirstOrDefault(x => x.Info?.Magic == record.SourceMagic);
                bool wizardPet = record.SourceMagic == MagicType.ElectricShock;
                bool valid = record.Monster != null && magic != null && record.Health > 0 &&
                    record.RemainingTime > TimeSpan.Zero && CombatPetSettings.IsSupported(record.SourceMagic) &&
                    (wizardPet ? Class == MirClass.Wizard && record.Monster.CanTame && !record.Monster.IsBoss :
                        Class == MirClass.Taoist && MatchesSummon(record));
                if (!valid) { record.Delete(); continue; }
                if (Pets.Count >= limit) break;
                Cell cell = FindCombatPetCell();
                if (cell == null) break;
                MonsterObject pet = MonsterObject.GetMonster(record.Monster);
                if (pet == null) { record.Delete(); continue; }
                pet.PetOwner = this;
                pet.Magics.Add(magic);
                AddSavedPetMagic(pet, record.FaithMagicIndex, MagicType.StrengthOfFaith);
                AddSavedPetMagic(pet, record.RecoveryMagicIndex, MagicType.DemonicRecovery);
                pet.SummonLevel = Math.Clamp(record.SummonLevel, 0, Globals.MagicMaxLevel * 2);
                pet.CombatPetLevel = Math.Clamp(record.CombatLevel, 0, CombatPetSettings.MaxLevel);
                pet.CombatPetExperience = pet.CombatPetLevel >= CombatPetSettings.MaxLevel ? 0 :
                    Math.Clamp(record.Experience, 0, CombatPetSettings.ExperienceRequired(pet.CombatPetLevel) - 1);
                pet.TameTime = record.RemainingTime == TimeSpan.MaxValue ? DateTime.MaxValue :
                    SEnvir.Now.AddTicks(Math.Min(record.RemainingTime.Ticks, DateTime.MaxValue.Ticks - SEnvir.Now.Ticks));
                Pets.Add(pet);
                if (!pet.Spawn(CurrentMap, cell.Location))
                {
                    Pets.Remove(pet);
                    pet.PetOwner = null;
                    continue;
                }
                pet.SavedCombatPet = record;
                pet.SetHP(Math.Min(record.Health, pet.Stats[Stat.Health]));
                Connection?.ReceiveChat($"Pet {pet.MonsterInfo.MonsterName}: przywrócono poziom {pet.CombatPetLevel}, EXP {pet.CombatPetExperience}.",
                    MessageType.System);
            }
        }

        private static bool MatchesSummon(UserCombatPet record) => record.SourceMagic switch
        {
            MagicType.SummonSkeleton => record.Monster.Flag == MonsterFlag.Skeleton,
            MagicType.SummonShinsu => record.Monster.Flag == MonsterFlag.Shinsu,
            MagicType.SummonJinSkeleton => record.Monster.Flag == MonsterFlag.JinSkeleton,
            MagicType.SummonDemonicCreature => record.Monster.Flag == MonsterFlag.InfernalSoldier,
            MagicType.SummonDead => record.Monster.Flag == MonsterFlag.UndeadSoul,
            _ => false
        };

        private void AddSavedPetMagic(MonsterObject pet, int index, MagicType type)
        {
            if (type == MagicType.StrengthOfFaith && !Buffs.Any(x => x.Type == BuffType.StrengthOfFaith)) return;
            UserMagic magic = Character.Magics.FirstOrDefault(x => x.Index == index && x.Info?.Magic == type);
            if (magic != null) pet.Magics.Add(magic);
        }

        private Cell FindCombatPetCell()
        {
            var cell = CurrentMap.GetCell(Functions.Move(CurrentLocation, Direction, -1));
            if (cell != null && cell.Movements == null) return cell;
            if (CurrentCell?.Movements == null) return CurrentCell;
            return CurrentMap.GetCells(CurrentLocation, 0, 2).FirstOrDefault(x => x.Movements == null);
        }

        public void RecallCombatPets()
        {
            if (CurrentCell == null || !Spawned || Dead) return;
            foreach (MonsterObject pet in Pets.ToArray())
                if (pet.IsCombatPet && !pet.Dead && pet.Node != null && pet.CurrentMap != CurrentMap)
                    pet.PetRecall();
            RestoreCombatPets();
        }
    }
}
