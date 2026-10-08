using Client.Controls;
using Client.Envir;
using Library;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using C = Library.Network.ClientPackets;

namespace Client.Scenes.Views
{
    public sealed partial class GuildDialog
    {
        private DXTab FragmentTab;
        private DXItemGrid FragmentGrid;
        private DXLabel FragmentCapacityLabel, FragmentRecipeLabel;
        private DXNumberBox FragmentAmount;
        private DXVScrollBar FragmentScroll;
        private DXButton FragmentDepositButton, FragmentWithdrawButton, FragmentAssembleButton, FragmentExpandButton;
        private readonly ClientUserItem[] GuildFragments = new ClientUserItem[GuildFragmentSettings.MaxCapacity];
        private int SelectedFragmentSlot = -1;

        private void CreateFragmentsTab()
        {
            FragmentTab = new DXTab { Parent = GuildTabs, TabButton = { Label = { Text = "Fragmenty" } }, BackColour = Color.Empty, Location = new Point(0, 23) };
            FragmentTab.TabButton.MouseClick += (o, e) =>
            {
                BackgroundImage.Index = 263;
                CreatePanel.Visible = AddMemberPanel.Visible = TreasuryPanel.Visible = StoragePanel.Visible = WarPanel.Visible = CastlePanel.Visible = false;
                RefreshFragmentDisplay();
                RefreshFragmentPermissions();
            };
            // Cover the storage artwork: its baked-in filters and slots do not match this tab.
            new DXControl { Parent = FragmentTab, Location = new Point(12, 8), Size = new Size(432, 424), DrawTexture = true, BackColour = Color.FromArgb(24, 18, 12), Border = false };
            FragmentCapacityLabel = new DXLabel { Parent = FragmentTab, Location = new Point(20, 12), ForeColour = Color.Gold };
            FragmentGrid = new DXItemGrid
            {
                Parent = FragmentTab, Location = new Point(20, 38), GridType = GridType.None,
                GridSize = new Size(10, 50), ItemGrid = GuildFragments, VisibleHeight = 7,
                ReadOnly = true, GridPadding = 1, Border = true, BorderColour = Color.FromArgb(92, 65, 35), BackColour = Color.FromArgb(18, 10, 7)
            };
            var scroll = FragmentScroll = new DXVScrollBar { Parent = FragmentTab, Location = new Point(408, 38), Size = new Size(14, FragmentGrid.Size.Height), VisibleSize = 7, MaxValue = 50 };
            scroll.ValueChanged += (o, e) => FragmentGrid.ScrollValue = scroll.Value;
            foreach (DXItemCell cell in FragmentGrid.Grid)
            {
                cell.MouseWheel += (o, e) => scroll.Value -= e.Delta / 120;
                cell.MouseClick += (o, e) =>
                {
                    if (cell.Item == null) return;
                    SelectedFragmentSlot = cell.Slot;
                    RefreshFragmentDisplay();
                };
            }
            FragmentRecipeLabel = new DXLabel { Parent = FragmentTab, Location = new Point(20, 308), AutoSize = false, Size = new Size(404, 38), ForeColour = Color.White };
            new DXLabel { Parent = FragmentTab, Location = new Point(20, 355), ForeColour = Color.White, Text = "Ilość wpłaty / wypłaty:" };
            FragmentAmount = new DXNumberBox { Parent = FragmentTab, Location = new Point(174, 352), MinValue = 1, MaxValue = int.MaxValue, Value = 1, Change = 1 };
            FragmentDepositButton = FragmentButton("Wpłać", 20, () =>
            {
                DXItemCell cell = DXItemCell.SelectedCell;
                if (cell?.Item == null || cell.Locked ||
                    !(cell.GridType == GridType.Inventory || cell.GridType == GridType.Storage || cell.GridType == GridType.PartsStorage)) return;
                var p = FragmentRequest(GuildFragmentAction.Deposit, cell.Slot, cell.Item.Index, FragmentAmount.Value);
                p.SourceGrid = cell.GridType;
                DXItemCell.SelectedCell = null;
                CEnvir.Enqueue(p);
            });
            FragmentDepositButton.Hint = "Wybierz części w plecaku lub prywatnym magazynie, ustaw ilość i kliknij Wpłać.";
            FragmentWithdrawButton = FragmentButton("Wypłać", 115, () => SendSelectedFragment(GuildFragmentAction.Withdraw));
            FragmentAssembleButton = FragmentButton("Złóż 1 szt.", 210, () => SendSelectedFragment(GuildFragmentAction.Assemble));
            FragmentExpandButton = FragmentButton("+1 miejsce", 315, () =>
            {
                var request = FragmentRequest(GuildFragmentAction.Expand, 0, 0, 0);
                var box = new DXMessageBox($"Dodać 1 miejsce za {Globals.GuildStorageCost:N0} z funduszy gildii?", "Magazyn fragmentów", DXMessageBoxButtons.YesNo);
                box.YesButton.MouseClick += (o, e) => CEnvir.Enqueue(request);
            });
            new DXLabel { Parent = FragmentTab, Location = new Point(20, 414), ForeColour = Color.Gold, Text = "Składanie zużywa wspólną pulę. Wynik: magazyn gildii." };
        }

        private DXButton FragmentButton(string text, int x, Action action)
        {
            var button = new DXButton { Parent = FragmentTab, Location = new Point(x, 382), Size = new Size(92, SmallButtonHeight), ButtonType = ButtonType.SmallButton, Label = { Text = text } };
            button.MouseClick += (o, e) => { if (GuildInfo != null && !GameScene.Game.Observer) action(); };
            return button;
        }

        private C.GuildFragmentOperation FragmentRequest(GuildFragmentAction action, int slot, int index, long count) =>
            new C.GuildFragmentOperation { Action = action, Slot = slot, ItemIndex = index, Count = count, Revision = GuildInfo.FragmentRevision };

        private void SendSelectedFragment(GuildFragmentAction action)
        {
            if (SelectedFragmentSlot < 0) return;
            ClientUserItem part = GuildFragments[SelectedFragmentSlot];
            if (part == null) return;
            var request = FragmentRequest(action, SelectedFragmentSlot, part.Index, action == GuildFragmentAction.Assemble ? 1 : FragmentAmount.Value);
            if (action != GuildFragmentAction.Assemble) { CEnvir.Enqueue(request); return; }
            var box = new DXMessageBox(FragmentRecipeLabel.Text + "\nZużyć części ze wspólnej puli?", "Składanie gildii", DXMessageBoxButtons.YesNo);
            box.YesButton.MouseClick += (o, e) => CEnvir.Enqueue(request);
        }

        public void UpdateFragments(int capacity, long revision, List<ClientUserItem> items)
        {
            if (GuildInfo == null) return;
            GuildInfo.FragmentStorageLimit = capacity;
            GuildInfo.FragmentRevision = revision;
            GuildInfo.FragmentStorage = items;
            Array.Clear(GuildFragments, 0, GuildFragments.Length);
            foreach (ClientUserItem item in items ?? new List<ClientUserItem>())
            {
                int slot = item.Slot - GuildFragmentSettings.SlotOffset;
                if (slot >= 0 && slot < GuildFragments.Length) GuildFragments[slot] = item;
            }
            if (SelectedFragmentSlot >= 0 && GuildFragments[SelectedFragmentSlot] == null) SelectedFragmentSlot = -1;
            RefreshFragmentDisplay();
            RefreshFragmentPermissions();
        }

        private static int FragmentTarget(ClientUserItem part) => part.AddedStats[Stat.ItemIndex];

        private void RefreshFragmentDisplay()
        {
            if (FragmentGrid == null) return;
            FragmentScroll.MaxValue = Math.Max(7, (int)Math.Ceiling((GuildInfo?.FragmentStorageLimit ?? 0) / 10D));
            foreach (DXItemCell cell in FragmentGrid.Grid)
            {
                cell.Enabled = GuildInfo != null && cell.Slot < GuildInfo.FragmentStorageLimit;
                cell.RefreshItem();
            }
            FragmentCapacityLabel.Text = GuildInfo == null ? "" : $"Miejsca: {GuildFragments.Count(x => x != null)} / {GuildInfo.FragmentStorageLimit} (maks. {GuildFragmentSettings.MaxCapacity})";
            FragmentRecipeLabel.Text = "Wybierz fragment, aby zobaczyć recepturę.";
            if (GuildInfo == null || SelectedFragmentSlot < 0) return;
            ClientUserItem part = GuildFragments[SelectedFragmentSlot];
            if (part == null) return;
            int index = FragmentTarget(part);
            var target = Globals.ItemInfoList.Binding.FirstOrDefault(x => x.Index == index);
            if (target == null || target.PartCount <= 0) { FragmentRecipeLabel.Text = "Brak receptury dla tego fragmentu."; return; }
            long available = GuildFragments.Where(x => x != null && FragmentTarget(x) == index).Sum(x => x.Count);
            FragmentRecipeLabel.Text = $"{target.ItemName}\nPotrzeba: {target.PartCount:N0} części; w puli: {available:N0}.";
        }

        private void RefreshFragmentPermissions()
        {
            if (FragmentDepositButton == null) return;
            GuildPermission p = GuildInfo?.Permission ?? GuildPermission.None;
            bool active = GuildInfo != null && !GameScene.Game.Observer;
            FragmentDepositButton.Enabled = active && (p & GuildPermission.FragmentDeposit) != 0;
            FragmentWithdrawButton.Enabled = active && (p & GuildPermission.FragmentWithdraw) != 0;
            FragmentAssembleButton.Enabled = active && (p & GuildPermission.FragmentAssemble) != 0;
            FragmentExpandButton.Enabled = active && p == GuildPermission.Leader && GuildInfo.FragmentStorageLimit < GuildFragmentSettings.MaxCapacity;
        }
    }
}