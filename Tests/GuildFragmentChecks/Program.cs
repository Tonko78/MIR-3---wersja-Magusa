using Library;
using Library.Network;
using Library.SystemModels;
using Library.MirDB;
using MirDB;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using C = Library.Network.ClientPackets;
using S = Library.Network.ServerPackets;

try
{
int checks = 0;
void Require(bool value, string message) { checks++; if (!value) throw new Exception(message); }

var assemblies = new[] { typeof(ItemInfo).Assembly, typeof(GuildInfo).Assembly };
string root = Path.GetFullPath(args.Length == 0 ? "./test-data/" : args[0]) + Path.DirectorySeparatorChar;
Session NewSession(string path)
{
    var s = new Session(SessionMode.Both, path, path + "backup/") { BackUp = false };
    SEnvir.Random = new Random(37);
    s.Initialize(assemblies);
    var get = typeof(Session).GetMethods().Single(x => x.Name == "GetCollection" && x.IsGenericMethod);
    foreach (var field in typeof(SEnvir).GetFields(BindingFlags.Public | BindingFlags.Static))
        if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(DBCollection<>))
            field.SetValue(null, get.MakeGenericMethod(field.FieldType.GetGenericArguments()).Invoke(s, null));
    SEnvir.Session = s;
    return s;
}
var session = NewSession(root); Globals.ItemInfoList = SEnvir.ItemInfoList;
ItemInfo Info(string name, ItemType type, ItemEffect effect, int stack)
{
    var info = SEnvir.ItemInfoList.CreateNewObject();
    info.ItemName = name; info.ItemType = type; info.ItemEffect = effect;
    info.StackSize = stack; info.CanTrade = true;
    return info;
}
var target = Info("Test assembled sword", ItemType.Weapon, ItemEffect.None, 1);
target.PartCount = 10;
var partInfo = Info("Test parts", ItemType.ItemPart, ItemEffect.ItemPart, 100);
var otherTarget = Info("Different recipe", ItemType.Armour, ItemEffect.None, 1);
otherTarget.PartCount = 5;
var guild = SEnvir.GuildInfoList.CreateNewObject();
guild.GuildName = "Test"; guild.StorageSize = 2; guild.FragmentStorageSize = 2;
UserItem Part(long count, int targetIndex)
{
    var item = SEnvir.CreateFreshItem(partInfo);
    item.Count = count;
    item.AddStat(Stat.ItemIndex, targetIndex, StatSource.Added); item.StatsChanged();
    return item;
}
void Store(int slot, UserItem item)
{
    guild.FragmentStorage[slot] = item;
    item.Slot = GuildFragmentSettings.SlotOffset + slot; item.Guild = guild;
}
var account = session.GetCollection<AccountInfo>().CreateNewObject(); account.Password = [];
var character = session.GetCollection<CharacterInfo>().CreateNewObject();
character.Account = account; character.CharacterName = "Tester"; account.LastCharacter = character;
var membership = session.GetCollection<GuildMemberInfo>().CreateNewObject();
membership.Account = account; membership.Guild = guild; membership.Permission = GuildPermission.Leader;
// A disconnected, headless fixture: no socket or map is required by these handlers.
var con = (SConnection)RuntimeHelpers.GetUninitializedObject(typeof(SConnection));
con.Connected = true; con.SendList = new ConcurrentQueue<Packet>(); con.Observers = [];
var player = (PlayerObject)RuntimeHelpers.GetUninitializedObject(typeof(PlayerObject));
player.Character = character; player.Connection = con; player.InSafeZone = true;
player.Inventory = new UserItem[Globals.InventorySize]; player.Equipment = new UserItem[Globals.EquipmentSize];
player.Storage = new UserItem[1000]; player.PartsStorage = new UserItem[1000]; player.Stats = new Stats { [Stat.BagWeight] = 100000 };
player.TradeItems = new Dictionary<UserItem, CellLinkInfo>();
account.Connection = con; con.Player = player; con.Stage = GameStage.Game;
var peerAccount = session.GetCollection<AccountInfo>().CreateNewObject(); peerAccount.Password = [];
var peerCharacter = session.GetCollection<CharacterInfo>().CreateNewObject();
peerCharacter.Account = peerAccount; peerCharacter.CharacterName = "Peer"; peerAccount.LastCharacter = peerCharacter;
var peerMember = session.GetCollection<GuildMemberInfo>().CreateNewObject();
peerMember.Account = peerAccount; peerMember.Guild = guild; peerMember.Permission = GuildPermission.FragmentAssemble;
var peerCon = (SConnection)RuntimeHelpers.GetUninitializedObject(typeof(SConnection));
peerCon.Connected = true; peerCon.SendList = new ConcurrentQueue<Packet>(); peerCon.Observers = []; peerCon.Stage = GameStage.Game;
var peer = (PlayerObject)RuntimeHelpers.GetUninitializedObject(typeof(PlayerObject));
peer.Character = peerCharacter; peer.Connection = peerCon; peer.InSafeZone = true;
peerAccount.Connection = peerCon; peerCon.Player = peer;

C.GuildFragmentOperation Request(GuildFragmentAction action, int slot, UserItem item, long count = 1) =>
    new() { Action = action, Slot = slot, ItemIndex = item?.Index ?? 0, Count = count, Revision = guild.FragmentRevision, SourceGrid = GridType.Inventory };
void Call(C.GuildFragmentOperation p) { con.SendList.Clear(); con.Process(p); }
long Pool() => guild.FragmentStorage.Where(x => x != null).Sum(x => x.Count);

var deposit = Part(25, target.Index); deposit.Character = character; deposit.Slot = 0; player.Inventory[0] = deposit;
membership.Permission = GuildPermission.Storage;
Call(Request(GuildFragmentAction.Deposit, 0, deposit, 15));
Require(deposit.Count == 25 && Pool() == 0, "Old storage permission must not grant fragment access.");
membership.Permission = GuildPermission.FragmentDeposit;
Call(Request(GuildFragmentAction.Deposit, 0, deposit, -1));
Call(Request(GuildFragmentAction.Deposit, 0, deposit, long.MaxValue));
Require(deposit.Count == 25 && Pool() == 0, "Invalid quantities mutated data.");
deposit.Flags = UserItemFlags.Bound;
Call(Request(GuildFragmentAction.Deposit, 0, deposit, 15));
Require(Pool() == 0, "Bound material accepted.");
deposit.Flags = UserItemFlags.None;
player.InSafeZone = false;
Call(Request(GuildFragmentAction.Deposit, 0, deposit, 15));
Require(Pool() == 0, "Unsafe zone accepted.");
player.InSafeZone = true;
player.Observer = true;
Call(Request(GuildFragmentAction.Deposit, 0, deposit, 15));
Require(Pool() == 0, "Observer mutated the guild.");
player.Observer = false;
player.TradePartner = player;
Call(Request(GuildFragmentAction.Deposit, 0, deposit, 15));
Require(Pool() == 0, "Trading source accepted.");
player.TradePartner = null;
var stale = Request(GuildFragmentAction.Deposit, 0, deposit, 15); stale.Revision = -1;
Call(stale); Require(Pool() == 0, "Stale revision accepted.");
var valid = Request(GuildFragmentAction.Deposit, 0, deposit, 15);
Call(valid);
Require(Pool() == 15 && deposit.Count == 10 && guild.FragmentRevision == 1, "Partial deposit or revision incorrect.");
Require(guild.FragmentStorage[0].Stats[Stat.ItemIndex] == target.Index, "Deposit lost recipe identity.");
Require(guild.FragmentStorage[0].Guild == guild && guild.FragmentStorage[0].Character == null, "Deposit ownership incorrect.");
Call(valid); Require(Pool() == 15 && deposit.Count == 10, "Repeated deposit consumed twice.");

var different = Part(95, otherTarget.Index); Store(1, different);
Require(GuildFragmentStorage.PlanDeposit(guild, Part(200, target.Index), 200) == null, "Full capacity accepted excess material.");
Require(Pool() == 110, "Planning changed stacks.");
Require(GuildFragmentStorage.PlanAssembly(guild, target.Index, 16) == null, "Different recipe material was consumed.");
var craft = Request(GuildFragmentAction.Assemble, 0, guild.FragmentStorage[0]);
membership.Permission = GuildPermission.FragmentWithdraw;
Call(craft); Require(Pool() == 110 && guild.Storage[0] == null, "Withdrawal right granted assembly.");
membership.Permission = GuildPermission.FragmentAssemble;
guild.StorageSize = 0;
Call(craft); Require(Pool() == 110, "Full result warehouse consumed materials.");
guild.StorageSize = 2;
Call(craft);
Require(Pool() == 100 && guild.Storage[0]?.Info == target && guild.FragmentRevision == 2, "Assembly did not debit exactly once.");
Require(guild.Storage[0].Guild == guild && guild.Storage[0].Slot == 0, "Assembly output ownership incorrect.");
Require(con.SendList.OfType<S.GuildNewItem>().Count() == 1, "Result was not broadcast.");
peerCon.Process(craft);
Require(Pool() == 100 && guild.Storage[1] == null, "Two clients using the same revision duplicated output.");
Require(peerCon.SendList.OfType<S.GuildNewItem>().Count() == 1, "Peer did not see the first assembly result.");
Call(craft); Require(Pool() == 100 && guild.Storage[1] == null, "Repeated recipe request duplicated output.");
Require(guild.FragmentStorage[1].Count == 95, "Assembly consumed a different recipe.");
var info = guild.ToClientInfo();
Require(info.Storage.Count == 1 && info.FragmentStorage.Count == 2, "Guild client snapshot mixed warehouses.");

membership.Permission = GuildPermission.FragmentWithdraw;
var withdrawal = Request(GuildFragmentAction.Withdraw, 0, guild.FragmentStorage[0], 3);
player.Stats[Stat.BagWeight] = -1;
Call(withdrawal); Require(Pool() == 100, "Overweight withdrawal consumed fragments.");
player.Stats[Stat.BagWeight] = 100000;
Call(withdrawal);
Require(Pool() == 97 && deposit.Count == 13, $"Partial withdrawal or inventory merge incorrect: pool={Pool()}, inventory={deposit.Count}, message={con.SendList.OfType<S.GuildFragmentState>().LastOrDefault()?.Message}");
Call(withdrawal); Require(Pool() == 97, "Repeated withdrawal consumed twice.");

membership.Permission = GuildPermission.Leader;
guild.GuildFunds = Globals.GuildStorageCost - 1;
Call(Request(GuildFragmentAction.Expand, 0, null));
Require(guild.FragmentStorageSize == 2, "Expansion ignored funds.");
guild.GuildFunds = Globals.GuildStorageCost;
Call(Request(GuildFragmentAction.Expand, 0, null));
Require(guild.FragmentStorageSize == 3 && guild.GuildFunds == 0 && guild.StorageSize == 2, "Separate expansion cost or capacity incorrect.");
Store(2, Part(8, target.Index));
var splitPlan = GuildFragmentStorage.PlanAssembly(guild, target.Index, 10);
Require(splitPlan.Count == 2 && splitPlan.Sum(x => x.Count) == 10, "Multi-stack recipe planning incorrect.");
long before = Pool();
try { GuildFragmentStorage.CommitAssembly(guild, splitPlan, 1, () => throw new Exception("factory failure")); } catch (Exception) { }
Require(Pool() == before && guild.Storage[1] == null, "Allocation failure consumed material.");
Call(Request(GuildFragmentAction.Assemble, 0, guild.FragmentStorage[0]));
Require(Pool() == before - 10 && guild.FragmentStorage[0] == null && guild.FragmentStorage[2] == null,
    "Multi-stack debit incorrect.");

var state = new S.GuildFragmentState { Capacity = 3, Revision = 50, Items = guild.ToClientInfo().FragmentStorage, Message = "Test" };
var decoded = (S.GuildFragmentState)Packet.ReceivePacket(state.GetPacketBytes(), out var extra);
Require(decoded.Capacity == 3 && decoded.Revision == 50 && decoded.Items.Count == state.Items.Count && extra.Length == 0, "Snapshot serialization failed.");
var op = Request(GuildFragmentAction.Assemble, 2, null); op.Revision = 72;
var opRead = (C.GuildFragmentOperation)Packet.ReceivePacket(op.GetPacketBytes(), out _);
Require(opRead.Action == op.Action && opRead.Revision == 72, "Request serialization failed.");

int guildIndex = guild.Index;
session.Save(true);
session = NewSession(root);
guild = SEnvir.GuildInfoList.Binding.Single(x => x.Index == guildIndex);
Require(guild.FragmentStorageSize == 3 && guild.StorageSize == 2, "Capacity did not survive restart.");
Require(guild.FragmentStorage[1].Count == 95 && guild.FragmentStorage[1].Stats[Stat.ItemIndex] == otherTarget.Index, "Parts did not survive restart.");
Require(guild.Storage.Count(x => x != null) == 2 && guild.Items.Count == 3, "Item ownership or arrays did not survive restart.");
Require(guild.Members.Single(x => x.Account.Index == account.Index).Permission == GuildPermission.Leader, "Permissions did not survive restart.");
Require(guild.Members.Single(x => x.Account.Index == peerAccount.Index).Permission == GuildPermission.FragmentAssemble, "Separate fragment rights did not survive restart.");

// Write an actual old-schema Users.db by removing the new column from mapping and header.
var mappingProperty = typeof(ADBCollection).GetProperty("Mapping", BindingFlags.NonPublic | BindingFlags.Instance);
var collection = session.GetCollection<GuildInfo>();
var mapping = (DBMapping)mappingProperty.GetValue(collection);
mapping.Properties.RemoveAll(x => x.PropertyName == nameof(GuildInfo.FragmentStorageSize));
var oldHeader = new List<DBMapping>();
using (var reader = new BinaryReader(new MemoryStream(session.UsersHeader)))
{
    int count = reader.ReadInt32();
    for (int i = 0; i < count; i++)
    {
        var m = new DBMapping(assemblies, reader);
        if (m.Type == typeof(GuildInfo)) m.Properties.RemoveAll(x => x.PropertyName == nameof(GuildInfo.FragmentStorageSize));
        oldHeader.Add(m);
    }
}
using (var stream = new MemoryStream())
{
    using var writer = new BinaryWriter(stream);
    writer.Write(oldHeader.Count); foreach (var m in oldHeader) m.Save(writer);
    writer.Flush(); session.UsersHeader = stream.ToArray();
}
guild.GuildNotice = "force old schema save";
session.Save(true);
session = NewSession(root);
guild = SEnvir.GuildInfoList.Binding.Single(x => x.Index == guildIndex);
Require(guild.FragmentStorageSize == GuildFragmentSettings.InitialCapacity, "Missing old-schema column did not migrate.");
Require(guild.FragmentStorage[1].Count == 95 && guild.Storage.Count(x => x != null) == 2, "Migration lost warehouse items.");
session.Save(true);
Console.WriteLine($"PASS: {checks} checks: authoritative operations, rights, stale/replayed requests, capacity, recipes, allocation failure, packet round trips, MirDB restart and old-schema migration.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
