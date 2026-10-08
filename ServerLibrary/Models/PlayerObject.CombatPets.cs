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
        public int ActiveCount => Pets.Count;
        public int ActiveMax => (Class == MirClass.Wizard || Class == MirClass.Taoist) ? CombatPetSettings.MaxActive : 0;

        public bool HasPendingCombatPet(MonsterInfo monster) => Config.PersistCombatPets && Character != null &&
            Character.CombatPets.Any(record => record.Monster == monster && !Pets.Any(pet => pet.SavedCombatPet == record));

        public void RestoreCombatPets()
        {
            if (!Config.PersistCombatPets || Character == null || Dead || Observer || CurrentCell == null) return;
            int limit = Class == MirClass.Wizard || Class == MirClass.Taoist ? CombatPetSettings.MaxActive : 0;
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

        #region Pet Storage UI (/pety)

        public void PetCommand(string input)
        {
            if (Class != MirClass.Wizard && Class != MirClass.Taoist)
            { Connection?.ReceiveChat("Tylko Wizard/Taoist ma pety bojowe.", MessageType.System); return; }
            if (Character == null || SEnvir.Session == null) return;
            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { PetCommandList(); return; }
            switch (parts[0].ToLowerInvariant())
            {
                case "list": PetCommandList(); return;
                case "in": PetCommandIn(parts); return;
                case "out": PetCommandOut(parts); return;
                case "del": PetCommandDelete(parts); return;
                default:
                    Connection?.ReceiveChat("Użycie: /pety | /pety in <n> | /pety out <n> | /pety del <n>", MessageType.System);
                    return;
            }
        }

        private void PetCommandList()
        {
            int slot = 0;
            var records = Character.CombatPets.OrderBy(r => r.Index).ToList();
            if (records.Count == 0)
            { Connection?.ReceiveChat("Schowek petów: pusto. (0/20)", MessageType.System); return; }
            foreach (var rec in records)
            {
                slot++;
                MonsterObject live = Pets.FirstOrDefault(p => p.SavedCombatPet == rec);
                string state = live != null && !live.Dead ? "AKTYWNY" : "schowek";
                string name = rec.Monster?.MonsterName ?? "???";
                string lvl = live != null ? $"Lv.{live.CombatPetLevel}" : $"Lv.{rec.CombatLevel}";
                string exp = live != null ? (live.CombatPetLevel >= CombatPetSettings.MaxLevel ? "MAX" :
                    $"{100m * live.CombatPetExperience / CombatPetSettings.ExperienceRequired(live.CombatPetLevel):0}%")
                    : (rec.CombatLevel >= CombatPetSettings.MaxLevel ? "MAX" :
                    $"{100m * rec.Experience / CombatPetSettings.ExperienceRequired(rec.CombatLevel):0}%");
                Connection?.ReceiveChat($"[{slot}] {name} — {lvl} {exp} — {state}", MessageType.System);
            }
            Connection?.ReceiveChat($"Schowek: {records.Count}/{CombatPetSettings.MaxCount} | Aktywni: {Pets.Count}/{CombatPetSettings.MaxActive}", MessageType.System);
        }

        private void PetCommandIn(string[] parts)
        {
            if (parts.Length < 2) { PetCommandList(); return; }
            int n;
            if (!int.TryParse(parts[1], out n) || n < 1) { Connection?.ReceiveChat("Podaj numer slotu.", MessageType.System); return; }
            var records = Character.CombatPets.OrderBy(r => r.Index).ToList();
            if (n > records.Count) { Connection?.ReceiveChat("Brak slotu.", MessageType.System); return; }
            var record = records[n - 1];
            MonsterObject live = Pets.FirstOrDefault(p => p.SavedCombatPet == record);
            if (live != null && !live.Dead)
            { Connection?.ReceiveChat("Już aktywny.", MessageType.System); return; }
            RestoreCombatPets();
            live = Pets.FirstOrDefault(p => p.SavedCombatPet == record);
            if (live != null)
            { Connection?.ReceiveChat($"{record.Monster?.MonsterName} przywrócony do walki.", MessageType.System); return; }
            if (Pets.Count >= CombatPetSettings.MaxActive)
            { Connection?.ReceiveChat("Maks. 4 aktywnych — najpierw /pety out <n>.", MessageType.System); return; }
            Connection?.ReceiveChat("Brak bezpiecznego miejsca — spróbuj na mapie.", MessageType.System);
        }

        private void PetCommandOut(string[] parts)
        {
            if (parts.Length < 2) { PetCommandList(); return; }
            int n;
            if (!int.TryParse(parts[1], out n) || n < 1) { Connection?.ReceiveChat("Podaj numer slotu.", MessageType.System); return; }
            var records = Character.CombatPets.OrderBy(r => r.Index).ToList();
            if (n > records.Count) { Connection?.ReceiveChat("Brak slotu.", MessageType.System); return; }
            var record = records[n - 1];
            MonsterObject live = Pets.FirstOrDefault(p => p.SavedCombatPet == record);
            if (live == null || live.Dead)
            { Connection?.ReceiveChat("Nie jest aktywny.", MessageType.System); return; }
            SaveCombatPet(live);
            live.PreserveCombatPetOnDespawn = true;
            live.Despawn();
            Connection?.ReceiveChat($"{record.Monster?.MonsterName} schowany do schowka.", MessageType.System);
        }

        private void PetCommandDelete(string[] parts)
        {
            if (parts.Length < 2) { PetCommandList(); return; }
            int n;
            if (!int.TryParse(parts[1], out n) || n < 1) { Connection?.ReceiveChat("Podaj numer slotu.", MessageType.System); return; }
            var list = Character.CombatPets.OrderBy(r => r.Index).ToList();
            if (n > list.Count) { Connection?.ReceiveChat("Brak slotu.", MessageType.System); return; }
            var record = list[n - 1];
            MonsterObject live = Pets.FirstOrDefault(p => p.SavedCombatPet == record);
            if (live != null) { SaveCombatPet(live); live.PreserveCombatPetOnDespawn = true; live.Despawn(); }
            record.Delete();
            Connection?.ReceiveChat($"{record.Monster?.MonsterName} trwale usunięty z schowka. (pozostało: {Character.CombatPets.Count}/{CombatPetSettings.MaxCount})", MessageType.System);
        }

        #endregion
    }
}
