using Library;
using Library.SystemModels;
using MirDB;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using S = Library.Network.ServerPackets;

namespace Server.DBModels
{
    [UserObject]
    public sealed class GuildInfo : DBObject
    {
        public string GuildName
        {
            get { return _GuildName; }
            set
            {
                if (_GuildName == value) return;

                var oldValue = _GuildName;
                _GuildName = value;

                OnChanged(oldValue, value, "GuildName");
            }
        }
        private string _GuildName;

        public int MemberLimit
        {
            get { return _MemberLimit; }
            set
            {
                if (_MemberLimit == value) return;

                var oldValue = _MemberLimit;
                _MemberLimit = value;

                OnChanged(oldValue, value, "MemberLimit");
            }
        }
        private int _MemberLimit;

        public int StorageSize
        {
            get { return _StorageSize; }
            set
            {
                if (_StorageSize == value) return;

                var oldValue = _StorageSize;
                _StorageSize = value;

                OnChanged(oldValue, value, "StorageSize");
            }
        }
        private int _StorageSize;

        public long GuildFunds
        {
            get { return _GuildFunds; }
            set
            {
                if (_GuildFunds == value) return;

                var oldValue = _GuildFunds;
                _GuildFunds = value;

                OnChanged(oldValue, value, "GuildFunds");
            }
        }
        private long _GuildFunds;

        public int GuildLevel
        {
            get { return _GuildLevel; }
            set
            {
                if (_GuildLevel == value) return;

                var oldValue = _GuildLevel;
                _GuildLevel = value;

                OnChanged(oldValue, value, "GuildLevel");
            }
        }
        private int _GuildLevel;

        public string GuildNotice
        {
            get { return _GuildNotice; }
            set
            {
                if (_GuildNotice == value) return;

                var oldValue = _GuildNotice;
                _GuildNotice = value;

                OnChanged(oldValue, value, "GuildNotice");
            }
        }
        private string _GuildNotice;

        public decimal GuildTax
        {
            get { return _GuildTax; }
            set
            {
                if (_GuildTax == value) return;

                var oldValue = _GuildTax;
                _GuildTax = value;

                OnChanged(oldValue, value, "GuildTax");
            }
        }
        private decimal _GuildTax;

        public long TotalContribution
        {
            get { return _TotalContribution; }
            set
            {
                if (_TotalContribution == value) return;

                var oldValue = _TotalContribution;
                _TotalContribution = value;

                OnChanged(oldValue, value, "TotalContribution");
            }
        }
        private long _TotalContribution;

        public long DailyContribution
        {
            get { return _DailyContribution; }
            set
            {
                if (_DailyContribution == value) return;

                var oldValue = _DailyContribution;
                _DailyContribution = value;

                OnChanged(oldValue, value, "DailyContribution");
            }
        }
        private long _DailyContribution;

        public long DailyGrowth
        {
            get { return _DailyGrowth; }
            set
            {
                if (_DailyGrowth == value) return;

                var oldValue = _DailyGrowth;
                _DailyGrowth = value;

                OnChanged(oldValue, value, "DailyGrowth");
            }
        }
        private long _DailyGrowth;

        public string DefaultRank
        {
            get { return _DefaultRank; }
            set
            {
                if (_DefaultRank == value) return;

                var oldValue = _DefaultRank;
                _DefaultRank = value;

                OnChanged(oldValue, value, "DefaultRank");
            }
        }
        private string _DefaultRank;

        public GuildPermission DefaultPermission
        {
            get { return _DefaultPermission; }
            set
            {
                if (_DefaultPermission == value) return;

                var oldValue = _DefaultPermission;
                _DefaultPermission = value;

                OnChanged(oldValue, value, "DefaultPermission");
            }
        }
        private GuildPermission _DefaultPermission;

        public bool StarterGuild
        {
            get { return _StarterGuild; }
            set
            {
                if (_StarterGuild == value) return;

                var oldValue = _StarterGuild;
                _StarterGuild = value;

                OnChanged(oldValue, value, "StarterGuild");
            }
        }
        private bool _StarterGuild;



        [Association("Conquest", true)]
        public UserConquest Conquest
        {
            get { return _Conquest; }
            set
            {
                if (_Conquest == value) return;

                var oldValue = _Conquest;
                _Conquest = value;

                OnChanged(oldValue, value, "Conquest");
            }
        }
        private UserConquest _Conquest;

        public CastleInfo Castle
        {
            get { return _Castle; }
            set
            {
                if (_Castle == value) return;

                var oldValue = _Castle;
                _Castle = value;

                OnChanged(oldValue, value, "Castle");
            }
        }
        private CastleInfo _Castle;

        public Color Colour
        {
            get { return _Colour; }
            set
            {
                if (_Colour == value) return;

                var oldValue = _Colour;
                _Colour = value;

                OnChanged(oldValue, value, "Colour");
            }
        }
        private Color _Colour;

        public int Flag
        {
            get { return _Flag; }
            set
            {
                if (_Flag == value) return;

                var oldValue = _Colour;
                _Flag = value;

                OnChanged(oldValue, value, "Flag");
            }
        }
        private int _Flag;

        public int FragmentStorageSize
        {
            get => _FragmentStorageSize;
            set
            {
                if (_FragmentStorageSize == value) return;
                int old = _FragmentStorageSize;
                _FragmentStorageSize = value;
                OnChanged(old, value, nameof(FragmentStorageSize));
            }
        }
        private int _FragmentStorageSize;

        public readonly object FragmentGate = new object();
        public long FragmentRevision;
        public UserItem[] FragmentStorage = new UserItem[GuildFragmentSettings.MaxCapacity];
        public UserItem[] Storage = new UserItem[1000];

        [Association("Members", true)]
        public DBBindingList<GuildMemberInfo> Members { get; set; }

        [Association("Items", true)]
        public DBBindingList<UserItem> Items { get; set; }


        public ClientGuildInfo ToClientInfo()
        {
            return new ClientGuildInfo
            {
                GuildName = GuildName,

                DailyGrowth = DailyGrowth,
                GuildFunds = GuildFunds,
                TotalContribution = TotalContribution,
                DailyContribution = DailyContribution,

                MemberLimit = MemberLimit,
                StorageLimit = StorageSize,

                Notice = GuildNotice,

                DefaultPermission = DefaultPermission,
                DefaultRank = DefaultRank,

                Colour = Colour,
                Flag = Flag,

                Tax = (int)(GuildTax * 100),

                Members = Members.Select(x => x.ToClientInfo()).ToList(),

                Storage = Items.Where(x => x.Slot >= 0 && x.Slot < GuildFragmentSettings.SlotOffset).Select(x => x.ToClientInfo()).ToList(),
                FragmentStorage = FragmentStorage.Where(x => x != null).Select(x => x.ToClientInfo()).ToList(),
                FragmentStorageLimit = FragmentStorageSize,
                FragmentRevision = FragmentRevision,
            };
        }

        protected override void OnLoaded()
        {
            base.OnLoaded();

            // Older Users.db has no capacity column. Allocate the initial capacity on first load.
            if (FragmentStorageSize <= 0) FragmentStorageSize = GuildFragmentSettings.InitialCapacity;
            foreach (UserItem item in Items)
            {
                int fragmentSlot = item.Slot - GuildFragmentSettings.SlotOffset;
                if (fragmentSlot >= 0 && fragmentSlot < FragmentStorage.Length)
                {
                    FragmentStorage[fragmentSlot] = item;
                    FragmentStorageSize = Math.Max(FragmentStorageSize, fragmentSlot + 1);
                    continue;
                }
                if (item.Slot < 0 || item.Slot >= Storage.Length)
                {
                    SEnvir.Log(string.Format("[BAD ITEM] Guild: {0}, Slot: {1}", GuildName, item.Slot));
                    continue;
                }

                Storage[item.Slot] = item;
            }
        }

        protected override void OnCreated()
        {
            base.OnCreated();

            FragmentStorageSize = GuildFragmentSettings.InitialCapacity;
            DefaultRank = "New Member";
            DefaultPermission = GuildPermission.None;

            Colour = Color.FromArgb(SEnvir.Random.Next(255), SEnvir.Random.Next(255), SEnvir.Random.Next(255));
            Flag = SEnvir.Random.Next(9);
        }

        protected override void OnDeleted()
        {
            Castle = null;

            base.OnDeleted();
        }

        public long CalculateGuildTax(UserItem item)
        {
            if (GuildTax <= 0) return 0;

            if (item == null || item.Info != SEnvir.GoldInfo) return 0;

            long amount = (long)Math.Ceiling(item.Count * GuildTax);

            return amount;
        }

        public S.GuildUpdate GetUpdatePacket()
        {
            return new S.GuildUpdate
            {
                DailyGrowth = DailyGrowth,
                GuildFunds = GuildFunds,
                TotalContribution = TotalContribution,
                DailyContribution = DailyContribution,

                MemberLimit = MemberLimit,
                StorageLimit = StorageSize,

                GuildLevel = GuildLevel,

                Tax = (int)(GuildTax * 100),

                DefaultPermission = DefaultPermission,
                DefaultRank = DefaultRank,

                Colour = Colour,
                Flag = Flag,

                Members = new List<ClientGuildMemberInfo>(),

                ObserverPacket = false,
            };
        }

        public override string ToString()
        {
            return GuildName;
        }
    }
}
