using Library;
using Server.Envir;
using System;

namespace Server.Models
{
    public static class CombatPetSettings
    {
        public static int MaxCount => Math.Clamp(Config.CombatPetMaxCount, 1, 20);
        public static int MaxLevel => Math.Clamp(Config.CombatPetMaxLevel, 0, 20);
        public static int StatBonusPerLevel => Math.Clamp(Config.CombatPetStatBonusPerLevel, 0, 50);
        public static long ExperienceRequired(int level) =>
            (long)Math.Clamp(Config.CombatPetExperiencePerLevel, 1, 1000000) * (Math.Clamp(level, 0, 20) + 1);

        public static bool IsSupported(MagicType type) => type == MagicType.ElectricShock ||
            type == MagicType.SummonSkeleton || type == MagicType.SummonShinsu ||
            type == MagicType.SummonJinSkeleton || type == MagicType.SummonDemonicCreature ||
            type == MagicType.SummonDead;

        public static DateTime TameDeadline()
        {
            if (Config.ElectricShockPetDurationHours <= 0) return DateTime.MaxValue;
            return SEnvir.Now.AddHours(Math.Clamp(Config.ElectricShockPetDurationHours, 1, 8760));
        }
    }
}
