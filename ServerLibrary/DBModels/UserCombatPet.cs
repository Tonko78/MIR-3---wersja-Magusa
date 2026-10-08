using Library;
using Library.SystemModels;
using MirDB;
using System;

namespace Server.DBModels
{
    [UserObject]
    public sealed class UserCombatPet : DBObject
    {
        [Association("CombatPets")]
        public CharacterInfo Character
        {
            get => _Character;
            set
            {
                if (_Character == value) return;
                var oldValue = _Character;
                _Character = value;
                OnChanged(oldValue, value, nameof(Character));
            }
        }
        private CharacterInfo _Character;

        public MonsterInfo Monster
        {
            get => _Monster;
            set
            {
                if (_Monster == value) return;
                var oldValue = _Monster;
                _Monster = value;
                OnChanged(oldValue, value, nameof(Monster));
            }
        }
        private MonsterInfo _Monster;

        public MagicType SourceMagic
        {
            get => _SourceMagic;
            set
            {
                if (_SourceMagic == value) return;
                var oldValue = _SourceMagic;
                _SourceMagic = value;
                OnChanged(oldValue, value, nameof(SourceMagic));
            }
        }
        private MagicType _SourceMagic;

        public int Health
        {
            get => _Health;
            set
            {
                if (_Health == value) return;
                var oldValue = _Health;
                _Health = value;
                OnChanged(oldValue, value, nameof(Health));
            }
        }
        private int _Health;

        public int CombatLevel
        {
            get => _CombatLevel;
            set
            {
                if (_CombatLevel == value) return;
                var oldValue = _CombatLevel;
                _CombatLevel = value;
                OnChanged(oldValue, value, nameof(CombatLevel));
            }
        }
        private int _CombatLevel;

        public long Experience
        {
            get => _Experience;
            set
            {
                if (_Experience == value) return;
                var oldValue = _Experience;
                _Experience = value;
                OnChanged(oldValue, value, nameof(Experience));
            }
        }
        private long _Experience;

        public int SummonLevel
        {
            get => _SummonLevel;
            set
            {
                if (_SummonLevel == value) return;
                var oldValue = _SummonLevel;
                _SummonLevel = value;
                OnChanged(oldValue, value, nameof(SummonLevel));
            }
        }
        private int _SummonLevel;

        public TimeSpan RemainingTime
        {
            get => _RemainingTime;
            set
            {
                if (_RemainingTime == value) return;
                var oldValue = _RemainingTime;
                _RemainingTime = value;
                OnChanged(oldValue, value, nameof(RemainingTime));
            }
        }
        private TimeSpan _RemainingTime;

        public int FaithMagicIndex
        {
            get => _FaithMagicIndex;
            set
            {
                if (_FaithMagicIndex == value) return;
                var oldValue = _FaithMagicIndex;
                _FaithMagicIndex = value;
                OnChanged(oldValue, value, nameof(FaithMagicIndex));
            }
        }
        private int _FaithMagicIndex;

        public int RecoveryMagicIndex
        {
            get => _RecoveryMagicIndex;
            set
            {
                if (_RecoveryMagicIndex == value) return;
                var oldValue = _RecoveryMagicIndex;
                _RecoveryMagicIndex = value;
                OnChanged(oldValue, value, nameof(RecoveryMagicIndex));
            }
        }
        private int _RecoveryMagicIndex;
    }
}

