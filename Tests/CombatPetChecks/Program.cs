using Library;
using Library.Network;
using Library.SystemModels;
using MirDB;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using Server.Models.Magics;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using S = Library.Network.ServerPackets;

try
{
    int checks = 0;
    void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    var assemblies = new[] { typeof(MonsterInfo).Assembly, typeof(CharacterInfo).Assembly };
    string root = Path.GetFullPath(args.Length > 0 ? args[0] :
        Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(),
            "CombatPetChecks-" + Guid.NewGuid().ToString("N"))) + Path.DirectorySeparatorChar;
    Session Load(string path)
    {
        var session = new Session(SessionMode.Both, path, path + "backup/") { BackUp = false };
        session.Initialize(assemblies);
        var getter = typeof(Session).GetMethods().Single(x => x.Name == "GetCollection" && x.IsGenericMethod);
        foreach (var field in typeof(SEnvir).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(DBCollection<>))
                field.SetValue(null, getter.MakeGenericMethod(field.FieldType.GetGenericArguments()).Invoke(session, null));
        SEnvir.Session = session;
        return session;
    }
    Config.PersistCombatPets = Config.CombatPetExperienceEnabled = true;
    Config.ElectricShockPetDurationHours = 0;
    Config.CombatPetMaxCount = 4;
    Config.CombatPetMaxLevel = 15;
    Config.CombatPetExperiencePerKill = 100;
    Config.CombatPetExperiencePerLevel = 1000;
    Config.CombatPetStatBonusPerLevel = 5;
    SEnvir.Now = new DateTime(2026, 10, 6, 12, 0, 0);
    SEnvir.Random = new Random(73);
    var session = Load(root);
    Map MapFixture(string name)
    {
        var info = SEnvir.MapInfoList.CreateNewObject(); info.Description = name;
        var map = new Map(info);
        typeof(Map).GetProperty(nameof(Map.Width)).SetValue(map, 7);
        typeof(Map).GetProperty(nameof(Map.Height)).SetValue(map, 7);
        var cells = new Cell[7, 7]; typeof(Map).GetProperty(nameof(Map.Cells)).SetValue(map, cells);
        map.OrderedObjects = Enumerable.Range(0, 7).Select(_ => new HashSet<MapObject>()).ToArray();
        for (int x = 0; x < 7; x++) for (int y = 0; y < 7; y++)
            map.ValidCells.Add(cells[x, y] = new Cell(new Point(x, y)) { Map = map });
        return map;
    }
    var firstMap = MapFixture("First"); var secondMap = MapFixture("Second");
    HeadlessPlayer PlayerFixture(string name, MirClass kind, Map map)
    {
        var account = session.GetCollection<AccountInfo>().CreateNewObject(); account.Password = [];
        var character = session.GetCollection<CharacterInfo>().CreateNewObject();
        character.Account = account; character.CharacterName = name; character.Class = kind;
        character.Level = 50; character.PetMode = PetMode.None; character.AttackMode = AttackMode.All;
        var connection = (SConnection)RuntimeHelpers.GetUninitializedObject(typeof(SConnection));
        connection.Connected = true; connection.SendList = new ConcurrentQueue<Packet>(); connection.Observers = [];
        account.Connection = connection;
        var player = new HeadlessPlayer(character, connection);
        player.Stats[Stat.Health] = 1000;
        player.Spawn(map, new Point(3, 3));
        return player;
    }
    UserMagic Learn(PlayerObject player, MagicType type)
    {
        var info = session.GetCollection<MagicInfo>().CreateNewObject(); info.Magic = type; info.Name = type.ToString();
        var magic = session.GetCollection<UserMagic>().CreateNewObject(); magic.Info = info; magic.Character = player.Character;
        magic.Level = Globals.MagicMaxLevel;
        return magic;
    }
    MonsterInfo MonsterDefinition(string name, MonsterFlag flag = MonsterFlag.None)
    {
        var info = session.GetCollection<MonsterInfo>().CreateNewObject();
        info.MonsterName = name; info.Level = 50; info.CanTame = true; info.Experience = 100;
        info.Flag = flag;
        foreach (var pair in new[] { (Stat.Health, 1000), (Stat.MinDC, 20), (Stat.MaxDC, 30) })
        {
            var stat = session.GetCollection<MonsterInfoStat>().CreateNewObject();
            stat.Monster = info; stat.Stat = pair.Item1; stat.Amount = pair.Item2;
        }
        info.StatsChanged();
        return info;
    }
    MonsterObject SpawnPet(PlayerObject owner, MonsterInfo info, UserMagic magic)
    {
        var pet = MonsterObject.GetMonster(info); pet.PetOwner = owner; pet.Magics.Add(magic);
        pet.SummonLevel = magic.Level; pet.TameTime = DateTime.MaxValue;
        owner.Pets.Add(pet); pet.Spawn(owner.CurrentMap, new Point(2, 3));
        return pet;
    }
    var wizard = PlayerFixture("Wizard", MirClass.Wizard, firstMap);
    var shock = Learn(wizard, MagicType.ElectricShock);
    var tameInfo = MonsterDefinition("Tame animal");
    var pet = SpawnPet(wizard, tameInfo, shock);
    pet.SetHP(317); pet.GainCombatPetExperience(950);
    wizard.SaveCombatPets();
    Check(wizard.Character.CombatPets.Count == 1, "Saved pet was not linked to character.");
    Check(pet.SavedCombatPet.Health == 317 && pet.SavedCombatPet.RemainingTime == TimeSpan.MaxValue, "HP or infinite taming was lost.");
    wizard.SaveCombatPets();
    Check(wizard.Character.CombatPets.Count == 1, "Repeated snapshot duplicated a pet.");
    Check(CombatPetSettings.TameDeadline() == DateTime.MaxValue, "Default tame duration is not unlimited.");
    Config.ElectricShockPetDurationHours = 24;
    Check(CombatPetSettings.TameDeadline() == SEnvir.Now.AddHours(24), "Configurable duration is incorrect.");
    Config.ElectricShockPetDurationHours = 0;

    MonsterObject WildVictim()
    {
        var victim = new MonsterObject { MonsterInfo = tameInfo };
        victim.Spawn(pet.CurrentMap, new Point(2, 2));
        return victim;
    }
    var victim = WildVictim(); victim.Attacked(pet, 1000, Element.None, canCrit: false);
    Check(victim.Dead && pet.CombatPetLevel == 1 && pet.CombatPetExperience == 50, "Real combat did not award pet EXP.");
    Check(pet.CurrentHP == 317 && pet.Stats[Stat.Health] == (1000 + 1000 * pet.SummonLevel / 10) * 75 / 100,
        $"Level-up healed pet or did not apply the L1 penalty: HP={pet.CurrentHP}, max={pet.Stats[Stat.Health]}, summon={pet.SummonLevel}.");
    Check(((S.ObjectMonster)pet.GetInfoPacket(wizard)).CustomName.Contains("Lv. 1 | EXP 2.5%"), "Pet spawn packet hides level/experience.");
    pet.ProcessNameColour(); Check(pet.NameColour == Color.LightGreen, "Level one pet colour missing.");
    long afterKill = pet.CombatPetExperience; victim.Die();
    Check(pet.CombatPetExperience == afterKill, "Second death awarded experience twice.");
    var ownedTarget = SpawnPet(wizard, tameInfo, shock);
    ownedTarget.Attacked(pet, 5000, Element.None, canCrit: false);
    Check(pet.CombatPetExperience == afterKill, "Owned victim yielded pet experience.");
    var zeroReward = MonsterDefinition("No EXP"); zeroReward.Experience = 0;
    var dummy = new MonsterObject { MonsterInfo = zeroReward }; dummy.Spawn(firstMap, new Point(2, 2));
    dummy.Attacked(pet, 1000, Element.None, canCrit: false);
    Check(pet.CombatPetExperience == afterKill, "Zero-reward victim yielded pet experience.");
    var secondPet = SpawnPet(wizard, tameInfo, shock);
    var idlePet = SpawnPet(wizard, tameInfo, shock);
    victim = WildVictim(); victim.Attacked(pet, 100, Element.None, canCrit: false);
    victim.Attacked(secondPet, 900, Element.None, canCrit: false);
    Check(pet.CombatPetExperience == afterKill + 50 && secondPet.CombatPetExperience == 50, "Participating pets did not share one reward.");
    Check(idlePet.CombatPetExperience == 0, "Idle pet gained experience without fighting.");
    idlePet.SetHP(0);
    secondPet.SetHP(0);
    pet.GainCombatPetExperience(long.MaxValue);
    Check(pet.CombatPetLevel == 15 && pet.CombatPetExperience == 0 && pet.CombatPetDisplayName.Contains("MAX"),
        "Level cap, MAX label or large experience overflowed.");
    pet.GainCombatPetExperience(long.MaxValue);
    Check(pet.CombatPetLevel == 15 && pet.CombatPetExperience == 0, "Pet advanced beyond L15.");
    pet.CombatPetLevel = 2; pet.CombatPetExperience = 321; pet.RefreshStats(); pet.SetHP(317);
    wizard.PetMode = PetMode.None;
    foreach (Cell cell in secondMap.ValidCells) cell.Movements = [];
    // Portal movement assigns its destination cell directly; normal Teleport rejects portal cells.
    wizard.CurrentCell = secondMap.GetCell(new Point(4, 4));
    Check(wizard.CurrentMap == secondMap, "Player portal-style map change failed.");
    Check(pet.CurrentMap == firstMap && pet.CurrentHP == 317, "Pet used an unsafe portal cell during recall.");
    foreach (Cell cell in secondMap.ValidCells) cell.Movements = null;
    Check(wizard.Teleport(secondMap, new Point(4, 3), false, false), "Movement after blocked recall failed.");
    Check(pet.CurrentMap == secondMap && pet.CurrentHP == 317 && pet.CombatPetLevel == 2 && pet.Target == null,
        "Map change/retry lost or reset a pet in rest mode.");
    var wizardCharacter = wizard.Character; int characterIndex = wizardCharacter.Index;
    wizard.SaveCombatPets();
    pet.PreserveCombatPetOnDespawn = true; pet.Despawn();
    Check(wizardCharacter.CombatPets.Count == 1 && wizard.Pets.Count == 0, "Logout despawn deleted saved state.");
    wizard.RestoreCombatPets(); pet = wizard.Pets.Single();
    Check(pet.CurrentHP == 317 && pet.CombatPetLevel == 2 && pet.CombatPetExperience == 321 && pet.TameTime == DateTime.MaxValue,
        "Relog did not restore HP, level, EXP and unlimited duration.");
    wizard.RestoreCombatPets();
    Check(wizard.Pets.Count == 1, "Repeated restore duplicated live pet.");
    pet.TameTime = SEnvir.Now.AddHours(2); wizard.SaveCombatPets();
    pet.PreserveCombatPetOnDespawn = true; pet.Despawn();
    SEnvir.Now = SEnvir.Now.AddDays(7); wizard.RestoreCombatPets(); pet = wizard.Pets.Single();
    Check(pet.TameTime == SEnvir.Now.AddHours(2), "Offline time consumed active-play timer.");
    wizard.SaveCombatPets();
    session.Save(true);
    pet.PreserveCombatPetOnDespawn = true; pet.Despawn();
    session = Load(root);
    var loadedCharacter = SEnvir.CharacterInfoList.Binding.Single(x => x.Index == characterIndex);
    Check(loadedCharacter.CombatPets.Count == 1 && loadedCharacter.CombatPets[0].CombatLevel == 2 &&
        loadedCharacter.CombatPets[0].Experience == 321 && loadedCharacter.CombatPets[0].Health == 317,
        "MirDB restart did not retain pet state and association.");
    wizard.Character = loadedCharacter;
    wizard.RestoreCombatPets(); pet = wizard.Pets.Single();
    Check(pet.MonsterInfo == loadedCharacter.CombatPets[0].Monster && pet.CurrentHP == 317, "Reloaded monster reference or HP is incorrect.");
    loadedCharacter.Account.Connection = wizard.Connection;
    wizard.StopGame();
    Check(wizard.Character == null && loadedCharacter.CombatPets.Count == 1 && wizard.Pets.Count == 0,
        "Actual StopGame lost persisted pets.");
    SEnvir.MagicTypes.Add(typeof(ElectricShock));
    var relogConnection = (SConnection)RuntimeHelpers.GetUninitializedObject(typeof(SConnection));
    relogConnection.Connected = true; relogConnection.SendList = new ConcurrentQueue<Packet>(); relogConnection.Observers = [];
    loadedCharacter.Account.Connection = relogConnection;
    wizard = new HeadlessPlayer(loadedCharacter, relogConnection);
    wizard.Stats[Stat.Health] = 1000; wizard.Spawn(secondMap, new Point(3, 3)); wizard.RestoreCombatPets();
    pet = wizard.Pets.Single();
    Check(pet.CurrentHP == 317 && pet.CombatPetLevel == 2 && pet.CombatPetExperience == 321,
        "New login instance lost pet state.");
    pet.SetHP(0);
    Check(loadedCharacter.CombatPets.Count == 0 && wizard.Pets.Count == 0, "Dead pet retained saved state.");
    wizard.RestoreCombatPets(); Check(wizard.Pets.Count == 0, "Relog resurrected a dead pet.");

    var tao = PlayerFixture("Taoist", MirClass.Taoist, secondMap);
    var skeletonMagic = Learn(tao, MagicType.SummonSkeleton);
    var skeletonInfo = MonsterDefinition("Skeleton", MonsterFlag.Skeleton);
    var skeleton = SpawnPet(tao, skeletonInfo, skeletonMagic);
    Check(CombatPetSettings.MaxLevel == 15 && CombatPetSettings.StatBonusPerLevel == 5,
        "Combat pet settings do not match the new curve.");
    long totalExperience = 0;
    for (int level = 0; level < 15; level++)
    {
        Check(CombatPetSettings.ExperienceRequired(level) == 1000L * (level + 1),
            $"Incorrect EXP requirement at L{level}.");
        totalExperience += CombatPetSettings.ExperienceRequired(level);
    }
    Check(totalExperience == 120000, "L0 to L15 requires an incorrect EXP total.");
    skeleton.CombatPetLevel = 6; skeleton.RefreshStats();
    int baseHealth = skeleton.Stats[Stat.Health];
    skeleton.CombatPetLevel = 0; skeleton.RefreshStats();
    for (int level = 1; level <= 15; level++)
    {
        skeleton.GainCombatPetExperience(1000L * level);
        Check(skeleton.CombatPetLevel == level && skeleton.CombatPetExperience == 0,
            $"Exact EXP did not advance to L{level}.");
        Check(skeleton.Stats[Stat.Health] == baseHealth + (long)baseHealth * ((level - 6) * 5) / 100,
            $"Incorrect stat curve at L{level}.");
        if (level <= 5) Check(skeleton.Stats[Stat.Health] < baseHealth, $"L{level} is not below base stats.");
        if (level == 6) Check(skeleton.Stats[Stat.Health] == baseHealth, "L6 is not the base stat value.");
        if (level == 7) Check(skeleton.Stats[Stat.Health] > baseHealth, "L7 does not exceed base stats.");
        Color expectedColour = level >= 12 ? Color.Gold : level >= 8 ? Color.Orchid :
            level >= 7 ? Color.LightSkyBlue : Color.LightGreen;
        Check(skeleton.CombatPetNameColour == expectedColour, $"Incorrect colour at L{level}.");
    }
    skeleton.GainCombatPetExperience(long.MaxValue);
    Check(skeleton.CombatPetLevel == 15 && skeleton.CombatPetExperience == 0 &&
        skeleton.CombatPetDisplayName.Contains("MAX"), "Taoist pet exceeded L15 or lacks MAX label.");
    var growthStats = new[] { Stat.Health, Stat.MinAC, Stat.MaxAC, Stat.MinMR, Stat.MaxMR,
        Stat.MinDC, Stat.MaxDC, Stat.MinMC, Stat.MaxMC, Stat.MinSC, Stat.MaxSC, Stat.Accuracy, Stat.Agility };
    var applyStats = typeof(MonsterObject).GetMethod("ApplyCombatPetStats", BindingFlags.Instance | BindingFlags.NonPublic);
    for (int level = 1; level <= 15; level++)
    {
        skeleton.CombatPetLevel = level;
        foreach (Stat stat in growthStats) skeleton.Stats[stat] = 100;
        applyStats.Invoke(skeleton, null);
        foreach (Stat stat in growthStats)
            Check(skeleton.Stats[stat] == 100 + (level - 6) * 5, $"Incorrect {stat} at L{level}.");
    }
    skeleton.CombatPetLevel = 1;
    foreach (Stat stat in growthStats) skeleton.Stats[stat] = -100;
    applyStats.Invoke(skeleton, null);
    foreach (Stat stat in growthStats) Check(skeleton.Stats[stat] == 0, $"Negative {stat} escaped the clamp.");
    skeleton.CombatPetLevel = 0; skeleton.CombatPetExperience = 0; skeleton.RefreshStats();
    Check(skeleton.CombatPetNameColour == Color.White, "Default pet colour is not White.");
    var taoVictim = new MonsterObject { MonsterInfo = tameInfo }; taoVictim.Spawn(tao.CurrentMap, new Point(2, 2));
    taoVictim.Attacked(skeleton, 5000, Element.None, canCrit: false);
    Check(taoVictim.Dead && skeleton.CombatPetExperience == 100, "Taoist real combat did not award EXP.");
    skeleton.GainCombatPetExperience(1400); skeleton.SetHP(250); tao.SaveCombatPets();
    skeleton.PreserveCombatPetOnDespawn = true; skeleton.Despawn(); tao.RestoreCombatPets();
    skeleton = tao.Pets.Single();
    Check(skeleton.CombatPetLevel == 1 && skeleton.CombatPetExperience == 500 && skeleton.CurrentHP == 250,
        "Taoist summon progression did not survive relog.");
    Check(tao.Teleport(firstMap, new Point(4, 4), false, false) && skeleton.CurrentMap == firstMap,
        "Taoist summon did not follow map change.");
    skeleton.UnTame();
    Check(tao.Character.CombatPets.Count == 0 && skeleton.CombatPetLevel == 0 && skeleton.PetOwner == null,
        "Untaming retained owner, growth or saved state.");
    skeleton.Despawn();

    // All Taoist summon handlers are exercised using the same server completion calls.
    foreach (var summon in new[] { (MagicType.SummonSkeleton, MonsterFlag.Skeleton),
        (MagicType.SummonShinsu, MonsterFlag.Shinsu), (MagicType.SummonJinSkeleton, MonsterFlag.JinSkeleton),
        (MagicType.SummonDemonicCreature, MonsterFlag.InfernalSoldier), (MagicType.SummonDead, MonsterFlag.UndeadSoul) })
    {
        var summonMagic = Learn(tao, summon.Item1);
        var summonInfo = MonsterDefinition(summon.Item1.ToString(), summon.Item2);
        MagicObject spell = summon.Item1 switch
        {
            MagicType.SummonSkeleton => new SummonSkeleton(tao, summonMagic),
            MagicType.SummonShinsu => new SummonShinsu(tao, summonMagic),
            MagicType.SummonJinSkeleton => new SummonJinSkeleton(tao, summonMagic),
            MagicType.SummonDemonicCreature => new SummonDemonicCreature(tao, summonMagic),
            _ => new SummonDead(tao, summonMagic)
        };
        if (summon.Item1 == MagicType.SummonDead)
        {
            var corpse = new MonsterObject { MonsterInfo = summonInfo };
            corpse.Spawn(tao.CurrentMap, new Point(2, 2)); corpse.SetHP(0);
            spell.MagicComplete(null, corpse, summonInfo);
        }
        else spell.MagicComplete(null, tao.CurrentMap, new Point(2, 2), summonInfo);
        var summoned = tao.Pets.Single();
        Check(tao.Character.CombatPets.Count == 1 && summoned.TameTime == DateTime.MaxValue,
            $"New summon was not saved immediately or duration was limited: {summon.Item1}.");
        summoned.GainCombatPetExperience(1600); summoned.SetHP(245); tao.SaveCombatPets();
        summoned.PreserveCombatPetOnDespawn = true; summoned.Despawn();
        if (summon.Item1 != MagicType.SummonDead)
        {
            foreach (Cell cell in tao.CurrentMap.ValidCells) cell.Movements = [];
            spell.MagicComplete(null, tao.CurrentMap, new Point(2, 2), summonInfo);
            Check(tao.Pets.Count == 0 && tao.Character.CombatPets.Count == 1 &&
                tao.Character.CombatPets[0].Experience == 600 && tao.Character.CombatPets[0].Health == 245,
                $"Blocked restore duplicated or reset the saved summon: {summon.Item1}.");
            foreach (Cell cell in tao.CurrentMap.ValidCells) cell.Movements = null;
        }
        tao.RestoreCombatPets();
        summoned = tao.Pets.Single();
        Check(summoned.MonsterInfo == summonInfo && summoned.CombatPetLevel == 1 &&
            summoned.CombatPetExperience == 600 && summoned.CurrentHP == 245, $"Summon restore failed: {summon.Item1}.");
        summoned.SetHP(0);
    }

    // A Taoist can keep four existing summon types together; a fifth is rejected.
    foreach (var type in new[] { MagicType.SummonSkeleton, MagicType.SummonShinsu, MagicType.SummonJinSkeleton,
        MagicType.SummonDemonicCreature, MagicType.SummonDead })
    {
        var summonMagic = tao.Character.Magics.First(x => x.Info.Magic == type);
        var summonInfo = SEnvir.MonsterInfoList.Binding.Last(x => x.MonsterName == type.ToString());
        MagicObject spell = type switch
        {
            MagicType.SummonSkeleton => new SummonSkeleton(tao, summonMagic),
            MagicType.SummonShinsu => new SummonShinsu(tao, summonMagic),
            MagicType.SummonJinSkeleton => new SummonJinSkeleton(tao, summonMagic),
            MagicType.SummonDemonicCreature => new SummonDemonicCreature(tao, summonMagic),
            _ => new SummonDead(tao, summonMagic)
        };
        if (type == MagicType.SummonDead)
        {
            var corpse = new MonsterObject { MonsterInfo = summonInfo };
            corpse.Spawn(tao.CurrentMap, new Point(2, 2)); corpse.SetHP(0);
            spell.MagicComplete(null, corpse, summonInfo);
            Check(tao.Pets.Count == 4, "Taoist did not preserve the four-pet cap.");
            spell.MagicComplete(null, corpse, summonInfo);
            Check(tao.Pets.Count == 4 && tao.Character.CombatPets.Count == 4, "Taoist summoned beyond four pets.");
        }
        else spell.MagicComplete(null, tao.CurrentMap, new Point(2, 2), summonInfo);
    }
    foreach (var summoned in tao.Pets.ToArray())
    { summoned.PreserveCombatPetOnDespawn = true; summoned.Despawn(); }
    tao.RestoreCombatPets();
    Check(tao.Pets.Count == 4 && tao.Pets.All(x => x.TameTime == DateTime.MaxValue),
        "Taoist did not restore four unlimited-duration pets.");
    foreach (var summoned in tao.Pets.ToArray()) summoned.SetHP(0);

    // Actual ElectricShock success path, including ownership transfer.
    var challenger = PlayerFixture("Challenger", MirClass.Wizard, firstMap);
    var challengerShock = Learn(challenger, MagicType.ElectricShock);
    shock = wizard.Character.Magics.Single(x => x.Info.Magic == MagicType.ElectricShock);
    wizard.Teleport(firstMap, new Point(3, 3), false, false);
    pet = SpawnPet(wizard, SEnvir.MonsterInfoList.Binding.Single(x => x.MonsterName == "Tame animal"), shock);
    pet.GainCombatPetExperience(1200); wizard.SaveCombatPets();
    SEnvir.Random = new TameSuccessRandom();
    new ElectricShock(challenger, challengerShock).MagicComplete(null, pet);
    Check(pet.PetOwner == challenger && !wizard.Pets.Contains(pet), "ElectricShock did not transfer owner.");
    Check(wizard.Character.CombatPets.Count == 0 && challenger.Character.CombatPets.Count == 1 &&
        pet.CombatPetLevel == 0 && pet.CombatPetExperience == 0 && pet.TameTime == DateTime.MaxValue,
        "Transfer duplicated saved ownership or retained previous owner's growth.");
    pet.SetHP(0);
    Check(challenger.Character.CombatPets.Count == 0, "Death after transfer retained saved pet.");

    // Saved pets that cannot spawn still enforce the Wizard's four-pet limit.
    for (int i = 0; i < 3; i++)
    {
        var pending = SpawnPet(challenger, pet.MonsterInfo, challengerShock);
        challenger.SaveCombatPet(pending); pending.PreserveCombatPetOnDespawn = true; pending.Despawn();
    }
    challenger.RestoreCombatPets();
    var fifthTarget = new MonsterObject { MonsterInfo = pet.MonsterInfo };
    fifthTarget.Spawn(firstMap, new Point(2, 2));
    new ElectricShock(challenger, challengerShock).MagicComplete(null, fifthTarget);
    Check(fifthTarget.PetOwner == challenger && challenger.Pets.Count == 4 && challenger.Character.CombatPets.Count == 4,
        "ElectricShock could not tame the fourth pet.");
    foreach (var pending in challenger.Pets.ToArray())
    { pending.PreserveCombatPetOnDespawn = true; pending.Despawn(); }
    foreach (Cell cell in firstMap.ValidCells) cell.Movements = [];
    var newTarget = new MonsterObject { MonsterInfo = pet.MonsterInfo };
    newTarget.Spawn(firstMap, new Point(2, 2));
    new ElectricShock(challenger, challengerShock).MagicComplete(null, newTarget);
    Check(newTarget.PetOwner == null && challenger.CombatPetSlotCount == 4 && challenger.Pets.Count == 0,
        "Pending saved pets allowed taming beyond the four-pet limit.");
    foreach (Cell cell in firstMap.ValidCells) cell.Movements = null;
    newTarget.Despawn(); challenger.RestoreCombatPets();
    Check(challenger.Pets.Count == 4, "Pending pets did not return after safe cells became available.");
    foreach (var restored in challenger.Pets.ToArray()) restored.SetHP(0);

    var dyingOwner = PlayerFixture("Dying owner", MirClass.Wizard, firstMap);
    var dyingShock = Learn(dyingOwner, MagicType.ElectricShock);
    var livingPet = SpawnPet(dyingOwner, pet.MonsterInfo, dyingShock);
    var dormantPet = SpawnPet(dyingOwner, pet.MonsterInfo, dyingShock);
    dyingOwner.SaveCombatPets(); dormantPet.PreserveCombatPetOnDespawn = true; dormantPet.Despawn();
    firstMap.Info.Fight = FightSetting.Safe;
    dyingOwner.Die();
    Check(livingPet.Dead && dyingOwner.Pets.Count == 0 && dyingOwner.Character.CombatPets.Count == 0,
        "Owner death failed to remove live and dormant saved pets.");

    // Disabled persistence does not create records; disabled progression does not award points.
    pet = SpawnPet(challenger, pet.MonsterInfo, challengerShock);
    Config.PersistCombatPets = false; challenger.SaveCombatPets();
    Check(challenger.Character.CombatPets.Count == 0, "Disabled persistence created records.");
    Config.CombatPetExperienceEnabled = false; pet.GainCombatPetExperience(1000);
    Check(pet.CombatPetExperience == 0 && pet.CombatPetLevel == 0, "Disabled progression awarded experience.");
    Config.PersistCombatPets = Config.CombatPetExperienceEnabled = true;
    challenger.SaveCombatPets(); pet.Despawn();
    Check(challenger.Character.CombatPets.Count == 0, "Ordinary despawn retained a pet record.");
    pet = SpawnPet(challenger, pet.MonsterInfo, challengerShock); challenger.SaveCombatPets();
    challenger.Character.Delete();
    Check(session.GetCollection<UserCombatPet>().Count == 0, "Character deletion did not aggregate-delete pets.");
    session.Save(true);
    if (args.Length > 1)
    {
        session = Load(Path.GetFullPath(args[1]) + Path.DirectorySeparatorChar);
        Check(SEnvir.CharacterInfoList.Count > 0 && SEnvir.CharacterInfoList.Binding.All(x => x.CombatPets != null && x.CombatPets.Count == 0),
            "Old schema without pet records did not load with empty pet lists.");
        int oldItems = SEnvir.UserItemList.Count; int oldCharacters = SEnvir.CharacterInfoList.Count;
        Check(oldItems > 0, "Old-schema fixture has no existing inventory to preserve.");
        session.Save(true);
        session = Load(Path.GetFullPath(args[1]) + Path.DirectorySeparatorChar);
        Check(SEnvir.UserItemList.Count == oldItems && SEnvir.CharacterInfoList.Count == oldCharacters &&
            session.GetCollection<UserCombatPet>().Count == 0, "Old-schema migration lost existing data.");
    }
    var recycler = PlayerFixture("Recycle", MirClass.Wizard, firstMap);
    recycler.Stats[Stat.BagWeight] = 10000;
    var itemInfo = session.GetCollection<ItemInfo>().CreateNewObject();
    itemInfo.ItemName = "Recycle fixture"; itemInfo.ItemType = ItemType.Weapon; itemInfo.StackSize = 1; itemInfo.Weight = 2;
    UserItem RecycleFixture(int slot)
    {
        var x = session.GetCollection<UserItem>().CreateNewObject(); x.Info=itemInfo; x.Count=1; x.CurrentDurability=123; x.MaxDurability=456; x.Character=recycler.Character; x.Slot=slot; recycler.Inventory[slot]=x;
        var added=session.GetCollection<UserItemStat>().CreateNewObject(); added.Item=x; added.Stat=Stat.MaxMC; added.Amount=17; added.StatSource=StatSource.Added; x.StatsChanged(); return x;
    }
    var recycled=RecycleFixture(0); int originalIndex=recycled.Index;
    recycled.Flags=UserItemFlags.Locked;
    recycler.ItemDelete(new Library.Network.ClientPackets.ItemDelete{Grid=GridType.Inventory,Slot=0});
    Check(recycler.Inventory[0]==recycled && recycler.Character.RecycledItems.Count==0,"Locked item bypassed delete protection");
    recycled.Flags=UserItemFlags.None;
    recycler.ItemDelete(new Library.Network.ClientPackets.ItemDelete{Grid=GridType.Inventory,Slot=0});
    Check(recycler.Inventory[0]==null && recycled.Character==null && recycled.RecycleOwner==recycler.Character && recycled.RecycleExpiresUtc>DateTime.UtcNow.AddSeconds(43),"Delete did not preserve item for 45 seconds");
    recycler.RecoverDeletedItem();
    Check(recycler.Inventory[0]==recycled && recycled.Index==originalIndex && recycled.CurrentDurability==123 && recycled.Stats[Stat.MaxMC]==17 && recycled.RecycleOwner==null,"Recovery lost identity, durability or added stats");
    Check(recycler.Connection.SendList.OfType<S.ItemRecovered>().Last().Item.Slot==0,"Recovery packet omitted authoritative slot");
    recycler.RecoverDeletedItem();
    Check(recycler.Inventory.Count(x=>x==recycled)==1,"Repeated undo duplicated item");
    recycler.ItemDelete(new Library.Network.ClientPackets.ItemDelete{Grid=GridType.Inventory,Slot=0});
    var occupying=RecycleFixture(0);
    recycler.RecoverDeletedItem();
    Check(recycler.Inventory[0]==occupying && recycler.Inventory[1]==recycled && recycled.Slot==1,"Recovery overwrote occupied slot instead of using free slot");
    recycler.ItemDelete(new Library.Network.ClientPackets.ItemDelete{Grid=GridType.Inventory,Slot=1});
    recycler.Inventory[0]=null; occupying.Delete();
    var stranger=PlayerFixture("Stranger",MirClass.Taoist,firstMap); stranger.RecoverDeletedItem();
    Check(recycled.RecycleOwner==recycler.Character,"Different player stole recycled item");
    for(int i=0;i<recycler.Inventory.Length;i++) recycler.Inventory[i]=recycled;
    recycler.RecoverDeletedItem(); Check(recycled.RecycleOwner==recycler.Character,"Full inventory consumed recycle record");
    Array.Clear(recycler.Inventory); recycler.Stats[Stat.BagWeight]=0;
    recycler.RecoverDeletedItem(); Check(recycled.RecycleOwner==recycler.Character,"Overweight recovery bypassed weight");
    recycler.Stats[Stat.BagWeight]=10000;
    session.Save(true); var savedRecycle=Load(root).GetCollection<UserItem>().Binding.Single(x=>x.Index==originalIndex);
    Check(savedRecycle.RecycleOwner?.Index==recycler.Character.Index && savedRecycle.CurrentDurability==123 && savedRecycle.Stats[Stat.MaxMC]==17,"Restart lost recycled item's ownership or stats");
    SEnvir.UserItemList=session.GetCollection<UserItem>(); SEnvir.Session=session;
    recycled.RecycleExpiresUtc=DateTime.UtcNow.AddMilliseconds(-1); recycler.RecoverDeletedItem();
    Check(recycler.Inventory.All(x=>x==null),"Expired item was restored");
    PlayerObject.PurgeExpiredRecycledItems(DateTime.UtcNow);
    Check(!session.GetCollection<UserItem>().Binding.Any(x=>x.Index==originalIndex),"Expired item was not purged");
    recycler.Character.Account.Admin=true; recycler.Character.Account.TempAdmin=false;
    recycler.Chat("@SUPERMAN"); Check(recycler.Superman,"Permanent admin cannot execute chat command");
    recycler.Chat("@SUPERMAN"); Check(!recycler.Superman,"Admin chat toggle cannot be reversed");
    Check(!new Server.Envir.Commands.AdminCommandHandler().IsAllowedByPlayer(stranger),"Normal account obtained admin command permissions");
    var previousMaster = Config.MasterPassword; var previousLogin = Config.AllowLogin;
    Config.AllowLogin=true;
    foreach(var disabled in new[]{"", "REDACTED"})
    {
        Config.MasterPassword=disabled;
        recycler.Connection.SendList.Clear();
        SEnvir.Login(new Library.Network.ClientPackets.Login { EMailAddress=recycler.Character.CharacterName, Password=disabled }, recycler.Connection);
        Check(recycler.Connection.SendList.OfType<S.Login>().Last().Result==LoginResult.BadEMail,"Unset/default master password enabled privileged login");
    }
    Config.MasterPassword=previousMaster; Config.AllowLogin=previousLogin;
    Packet.IsClient=true;
    var progressPacket=new S.ObjectPetOwnerChanged{ObjectID=123,PetOwner="Wizard",CustomName="Skeleton [Lv. 2 | EXP 12.3%]"};
    var received=(S.ObjectPetOwnerChanged)Packet.ReceivePacket(progressPacket.GetPacketBytes(),out _);
    Check(received.CustomName==progressPacket.CustomName && received.PetOwner==progressPacket.PetOwner,"Pet progress packet roundtrip failed");
    Console.WriteLine($"PASS: {checks} checks: combat EXP, level cap, no duplicate kills, HP, map travel, relog, timer, MirDB restart, Taoist summons, blocked restore/slot limits, taming transfer, pet/owner death, aggregate deletion and optional old-schema migration.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

sealed class HeadlessPlayer : PlayerObject
{
    public HeadlessPlayer(CharacterInfo character, SConnection connection) : base(character, connection) { }
    protected override void OnSpawned()
    {
        Character.Player = this; Connection.Player = this; Connection.Stage = GameStage.Game;
        SEnvir.Players.Add(this);
    }
}

sealed class TameSuccessRandom : Random
{
    public override int Next(int maxValue) => maxValue switch { 4 => 0, 20 => 1, >= 70 => maxValue - 1, _ => 0 };
}
