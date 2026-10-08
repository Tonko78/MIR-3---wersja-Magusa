using Client.Controls;
using Client.Envir;
using Client.Models;
using Client.UserModels;
using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using C = Library.Network.ClientPackets;

namespace Client.Scenes.Views
{
    public sealed class InventoryDialog : DXWindow
    {
        #region Properties

        public DXItemGrid Grid;

        public DXLabel PrimaryCurrencyLabel, SecondaryCurrencyLabel, WeightLabel, WalletLabel, PrimaryCurrencyTitle, SecondaryCurrencyTitle;
        public DXButton SortButton, TrashButton, SellButton;
        public DXButton UndoDeleteButton;
        public bool DeleteEnabled { get; private set; }
        private int recycleCount;

        public List<DXItemCell> SelectedItems = new();

        public List<ItemType> SellableItemTypes = new();

        #region PrimaryCurrency

        public CurrencyInfo PrimaryCurrency
        {
            get => _PrimaryCurrency;
            set
            {
                if (_PrimaryCurrency == value) return;

                CurrencyInfo oldValue = _PrimaryCurrency;
                _PrimaryCurrency = value;

                OnPrimaryCurrencyChanged(oldValue, value);
            }
        }
        private CurrencyInfo _PrimaryCurrency;

        public event EventHandler<EventArgs> PrimaryCurrencyChanged;
        public void OnPrimaryCurrencyChanged(CurrencyInfo oValue, CurrencyInfo nValue)
        {
            if (GameScene.Game.User == null)
                return;

            foreach (DXItemCell cell in Grid.Grid)
                cell.Selected = false;

            RefreshPrimaryCurrency();

            PrimaryCurrencyChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region SecondaryCurrency

        public CurrencyInfo SecondaryCurrency
        {
            get => _SecondaryCurrency;
            set
            {
                if (_SecondaryCurrency == value) return;

                CurrencyInfo oldValue = _SecondaryCurrency;
                _SecondaryCurrency = value;

                OnPrimaryCurrencyChanged(oldValue, value);
            }
        }
        private CurrencyInfo _SecondaryCurrency;

        public event EventHandler<EventArgs> SecondaryCurrencyChanged;
        public void OnSecondaryCurrencyChanged(CurrencyInfo oValue, CurrencyInfo nValue)
        {
            if (GameScene.Game.User == null)
                return;

            foreach (DXItemCell cell in Grid.Grid)
                cell.Selected = false;

            RefreshSecondaryCurrency();

            SecondaryCurrencyChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        public override void OnIsVisibleChanged(bool oValue, bool nValue)
        {
            if (!IsVisible)
                Grid?.ClearLinks();

            if (IsVisible)
                BringToFront();

            if (Settings != null)
                Settings.Visible = nValue;

            base.OnIsVisibleChanged(oValue, nValue);
        }

        #endregion

        #region Settings

        public override WindowType Type => WindowType.InventoryBox;
        public override bool CustomSize => false;
        public override bool AutomaticVisibility => true;

        public override void ApplySettings()
        {
            base.ApplySettings();
            Size viewport = Parent?.Size ?? Config.GameSize;
            Location = new Point(Math.Clamp(Location.X, 0, Math.Max(0, viewport.Width - Size.Width)),
                                 Math.Clamp(Location.Y, 0, Math.Max(0, viewport.Height - Size.Height)));
            if (Settings != null)
            {
                Settings.Size = Size;
                Settings.Location = Location;
            }
        }

        #endregion

        public InventoryDialog()
        {
            DropShadow = true;
            TitleLabel.Text = CEnvir.Language.InventoryDialogTitle + $" ({Globals.InventorySize})";

            Grid = new DXItemGrid
            {
                GridSize = new Size(13, Globals.InventorySize / 13),
                Parent = this,
                ItemGrid = GameScene.Game.Inventory,
                GridType = GridType.Inventory,
                Location = new Point(20, 39),
                GridPadding = 1,
                BackColour = Color.FromArgb(24, 12, 12),
                Border = true,
                BorderColour = Color.FromArgb(92, 65, 35)
            };

            foreach (DXItemCell cell in Grid.Grid)
            {
                cell.SelectedChanged += Cell_SelectedChanged;
            }

            int footerY = Grid.Location.Y + Grid.Size.Height + 12;
            Size = new Size(Grid.Size.Width + 40, footerY + 96);
            CEnvir.LibraryList.TryGetValue(LibraryFile.GameInter, out MirLibrary library);

            DXControl WeightBar = new DXControl
            {
                Parent = this,
                Location = new Point(20, footerY),
                Size = library?.GetSize(360) ?? new Size(192, 14),
                DrawTexture = true,
                BackColour = Color.FromArgb(40, 30, 20),
            };
            WeightBar.BeforeDraw += (o, e) =>
            {
                if (library == null) return;

                if (MapObject.User.Stats[Stat.BagWeight] == 0) return;

                float percent = Math.Min(1, Math.Max(0, MapObject.User.BagWeight / (float)MapObject.User.Stats[Stat.BagWeight]));

                if (percent == 0) return;

                if (!library.TryGetTexture(360, ImageType.Image, out MirImage image, out var texture, out var sourceRectangle)) return;

                PresentTexture(texture, sourceRectangle, this, new Rectangle(WeightBar.DisplayArea.X, WeightBar.DisplayArea.Y, (int)(image.Width * percent), image.Height), Color.White, WeightBar);
            };

            WeightLabel = new DXLabel
            {
                Parent = this,
                ForeColour = Color.White,
                Outline = true,
                OutlineColour = Color.Black,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter
            };
            WeightLabel.SizeChanged += (o, e) =>
            {
                WeightLabel.Location = new Point(WeightBar.Location.X + (WeightBar.Size.Width - WeightLabel.Size.Width) / 2, WeightBar.Location.Y - 1 + (WeightBar.Size.Height - WeightLabel.Size.Height) / 2);
            };

            PrimaryCurrencyTitle = new DXLabel
            {
                AutoSize = false,
                ForeColour = Color.Goldenrod,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left,
                Parent = this,
                Location = new Point(20, footerY + 22),
                Font = new Font(Config.FontName, CEnvir.FontSize(8F), FontStyle.Bold),
                Text = CEnvir.Language.InventoryDialogPrimaryCurrencyTitle,
                Size = new Size(72, 20)
            };

            PrimaryCurrencyLabel = new DXLabel
            {
                AutoSize = false,
                ForeColour = Color.White,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left,
                Parent = this,
                Location = new Point(80, footerY + 22),
                Text = "0",
                Size = new Size(200, 20)
            };
            PrimaryCurrencyLabel.MouseClick += PrimaryCurrencyLabel_MouseClick;

            SecondaryCurrencyTitle = new DXLabel
            {
                AutoSize = false,
                ForeColour = Color.DarkOrange,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left,
                Parent = this,
                Location = new Point(20, footerY + 44),
                Font = new Font(Config.FontName, CEnvir.FontSize(8F), FontStyle.Bold),
                Text = CEnvir.Language.InventoryDialogSecondaryCurrencyTitle,
                Size = new Size(72, 20)
            };

            SecondaryCurrencyLabel = new DXLabel
            {
                AutoSize = false,
                ForeColour = Color.White,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left,
                Parent = this,
                Location = new Point(80, footerY + 44),
                Text = "0",
                Size = new Size(180, 20)
            };
            SecondaryCurrencyLabel.MouseClick += SecondaryCurrencyLabel_MouseClick;

            SortButton = new DXButton
            {
                LibraryFile = LibraryFile.GameInter,
                Index = 364,
                Parent = this,
                Location = new Point(Size.Width - 60, footerY - 6),
                Hint = CEnvir.Language.InventoryDialogSortButtonHint
            };
            SortButton.MouseClick += SortButton_MouseClick;

            TrashButton = new DXButton
            {
                ButtonType = ButtonType.SmallButton,
                Parent = this,
                Size = new Size(120, SmallButtonHeight),
                Location = new Point(Size.Width - 140, footerY + 34),
                Label = { Text = "DEL: WYŁ.", ForeColour = Color.LightGreen },
                Hint = "Bezpiecznik DEL. Kliknij, aby włączyć lub wyłączyć usuwanie pod kursorem."
            };
            TrashButton.MouseClick += TrashButton_MouseClick;
            UndoDeleteButton = new DXButton
            {
                Parent = this, ButtonType = ButtonType.SmallButton, Size = new Size(120, SmallButtonHeight),
                Location = new Point(Size.Width - 140, footerY + 58),
                Label = { Text = "Cofnij" }, Visible = false,
                Hint = "Odzyskaj ostatni usunięty przedmiot. Każdy przedmiot można odzyskać przez 45 sekund."
            };
            UndoDeleteButton.MouseClick += (o,e) => { if (e.Button == MouseButtons.Left && !GameScene.Game.Observer) CEnvir.Enqueue(new C.ItemRecover()); };

            SellButton = new DXButton
            {
                LibraryFile = LibraryFile.GameInter,
                Index = 354,
                Parent = this,
                Location = new Point(Size.Width - 60, footerY + 24),
                Hint = "Sell All",
                Enabled = true,
                Visible = false
            };
            SellButton.MouseClick += SellButton_MouseClick;

            WalletLabel = new DXLabel
            {
                Parent = this,
                AutoSize = false,
                Location = new Point(20, footerY + 66),
                Text = CEnvir.Language.CurrencyDialogTitle,
                ForeColour = Color.Gold,
                Hint = string.Format(CEnvir.Language.InventoryDialogWalletLabelHint, CEnvir.GetKeyBindLabel(KeyBindAction.CurrencyWindow)),
                Size = new Size(240, 20),
                Sound = SoundIndex.GoldPickUp
            };
            WalletLabel.DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            WalletLabel.MouseClick += WalletLabel_MouseClick;
            PrimaryCurrencyTitle.TextChanged += (o, e) => ArrangeCurrencyRows();
            SecondaryCurrencyTitle.TextChanged += (o, e) => ArrangeCurrencyRows();
            PrimaryCurrencyTitle.FontChanged += (o, e) => ArrangeCurrencyRows();
            SecondaryCurrencyTitle.FontChanged += (o, e) => ArrangeCurrencyRows();
            ArrangeCurrencyRows();
        }

        private void SortButton_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.Observer) return;

            C.ItemSort packet = new C.ItemSort { Grid = GridType.Inventory };
            CEnvir.Enqueue(packet);
        }

        private void ArrangeCurrencyRows()
        {
            if (PrimaryCurrencyLabel == null || SecondaryCurrencyLabel == null) return;
            int footerY = Grid.Location.Y + Grid.Size.Height + 12;
            ArrangeCurrencyRow(PrimaryCurrencyTitle, PrimaryCurrencyLabel, footerY + 22);
            ArrangeCurrencyRow(SecondaryCurrencyTitle, SecondaryCurrencyLabel, footerY + 44);
        }

        private void ArrangeCurrencyRow(DXLabel title, DXLabel amount, int y)
        {
            int titleWidth = Math.Clamp(DXLabel.GetSize(title.Text, title.Font, title.Outline).Width + 4, 24, 120);
            title.Location = new Point(20, y);
            title.Size = new Size(titleWidth, 20);
            amount.Location = new Point(title.Location.X + titleWidth + 8, y);
            amount.Size = new Size(Math.Max(100, Size.Width - 140 - amount.Location.X), 20);
        }

        public override void OnKeyDown(KeyEventArgs e)
        {
            if (!IsVisible || !IsEnabled) return;
            if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None && DXTextBox.ActiveTextBox == null &&
                DeleteEnabled && TrashButton.IsVisible && MouseControl is DXItemCell hovered && Grid.Grid.Contains(hovered))
            {
                DeleteItem(hovered);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void TrashButton_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || GameScene.Game.Observer) return;
            DeleteEnabled = !DeleteEnabled;
            TrashButton.Label.Text = DeleteEnabled ? "DEL: AKTYWNY" : "DEL: WYŁ.";
            TrashButton.Label.ForeColour = DeleteEnabled ? Color.OrangeRed : Color.LightGreen;
        }

        public void UpdateRecycleState(int count, int seconds)
        {
            recycleCount = count;
            UndoDeleteButton.Visible = count > 0 && InvMode == InventoryMode.Normal;
            UndoDeleteButton.Enabled = seconds > 0;
            UndoDeleteButton.Label.Text = $"Cofnij ({seconds}s)";
            UndoDeleteButton.Hint = $"Do odzyskania: {count}. Kliknij, aby przywrócić ostatni. Każdy znika po 45 sekundach.";
        }

        private void DeleteItem(DXItemCell cell)
        {
            if (GameScene.Game.Observer) return;
            if (cell == null || cell.Item == null || cell.Locked || cell.ReadOnly || !cell.IsEnabled) return;
            if ((cell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) return;
            if ((cell.Item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) return;
            if (cell.GridType != GridType.Inventory) return;
            cell.Locked = true;
            CEnvir.Enqueue(new C.ItemDelete { Grid = cell.GridType, Slot = cell.Slot });
        }

        private void Cell_SelectedChanged(object sender, EventArgs e)
        {
            var cell = sender as DXItemCell;

            if (InvMode == InventoryMode.Sell)
            {
                if (cell.Selected)
                {
                    if (cell.Item != null && (cell.Item.Flags & UserItemFlags.Locked) != UserItemFlags.Locked)
                    {
                        if (!SellableItemTypes.Contains(cell.Item.Info.ItemType))
                        {
                            GameScene.Game.ReceiveChat(string.Format(CEnvir.Language.UnableToSellHere, cell.Item.Info.ItemName), MessageType.System);
                            cell.Selected = false;
                            return;
                        }

                        SelectedItems.Add(cell);
                    }
                }
                else
                    SelectedItems.Remove(cell);

                long sum = 0;
                int count = 0;
                decimal exchangeRate = PrimaryCurrency.ExchangeRate <= 0M ? 1M : PrimaryCurrency.ExchangeRate;

                foreach (DXItemCell itemCell in SelectedItems)
                {
                    count++;
                    sum += (long)(itemCell.Item.Price(itemCell.Item.Count) / exchangeRate);
                }

                SecondaryCurrencyLabel.Text = sum.ToString("#,##0");

                SellButton.Enabled = true;
                SellButton.Hint = count == 1 ? "Sell" : "Sell All";
            }
        }

        private void SellButton_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.Observer) return;

            var cell = DXItemCell.SelectedCell;

            if (cell != null && cell.Item != null && (cell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) return;

            List<CellLinkInfo> links = new();

            if (SelectedItems.Count > 0)
            {
                foreach (DXItemCell itemCell in SelectedItems)
                {
                    if ((itemCell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;

                    links.Add(new CellLinkInfo { Count = itemCell.Item.Count, GridType = GridType.Inventory, Slot = itemCell.Slot });
                }
            }
            else
            {
                //Sell all
                foreach (DXItemCell itemCell in Grid.Grid)
                {
                    if (itemCell.Item == null) continue;
                    if ((itemCell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;

                    if (SellableItemTypes.Count > 0 && !SellableItemTypes.Contains(itemCell.Item.Info.ItemType)) continue;

                    links.Add(new CellLinkInfo { Count = itemCell.Item.Count, GridType = GridType.Inventory, Slot = itemCell.Slot });
                }
            }

            if (links.Count > 0)
            {
                CEnvir.Enqueue(new C.NPCSell { Links = links });
            }
        }

        private void PrimaryCurrencyLabel_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.SelectedCell == null)
            {
                var userCurrency = GameScene.Game.User.GetCurrency(PrimaryCurrency);

                if (!userCurrency.CanPickup) return;
                DXSoundManager.Play(SoundIndex.GoldPickUp);

                if (GameScene.Game.CurrencyPickedUp == null && userCurrency.Amount > 0)
                    GameScene.Game.CurrencyPickedUp = userCurrency;
                else
                    GameScene.Game.CurrencyPickedUp = null;
            }
        }

        private void SecondaryCurrencyLabel_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.SelectedCell == null)
            {
                var userCurrency = GameScene.Game.User.GetCurrency(SecondaryCurrency);

                if (!userCurrency.CanPickup) return;
                DXSoundManager.Play(SoundIndex.GoldPickUp);

                if (GameScene.Game.CurrencyPickedUp == null && userCurrency.Amount > 0)
                    GameScene.Game.CurrencyPickedUp = userCurrency;
                else
                    GameScene.Game.CurrencyPickedUp = null;
            }
        }

        private void WalletLabel_MouseClick(object sender, MouseEventArgs e)
        {
            GameScene.Game.CurrencyBox.Visible = !GameScene.Game.CurrencyBox.Visible;
        }

        #region Methods

        public void RefreshCurrency()
        {
            RefreshPrimaryCurrency();
            RefreshSecondaryCurrency();
        }

        private void SetPrimaryCurrency(CurrencyInfo currency)
        {
            PrimaryCurrency = currency ?? Globals.CurrencyInfoList.Binding.First(x => x.Type == CurrencyType.Gold);
        }

        private void RefreshPrimaryCurrency()
        {
            SetPrimaryCurrency(PrimaryCurrency);

            var userCurrency = GameScene.Game.User.GetCurrency(PrimaryCurrency);

            PrimaryCurrencyTitle.Text = userCurrency.Info.Abbreviation;
            PrimaryCurrencyLabel.Text = userCurrency.Amount.ToString("#,##0");
        }

        private void SetSecondaryCurrency(CurrencyInfo currency)
        {
            SecondaryCurrency = currency ?? Globals.CurrencyInfoList.Binding.First(x => x.Type == CurrencyType.GameGold);
        }

        private void RefreshSecondaryCurrency()
        {
            SetSecondaryCurrency(SecondaryCurrency);

            if (InvMode == InventoryMode.Sell) return;

            var userCurrency = GameScene.Game.User.GetCurrency(SecondaryCurrency);

            SecondaryCurrencyTitle.Text = userCurrency.Info.Abbreviation;
            SecondaryCurrencyTitle.ForeColour = Color.DarkOrange;
            SecondaryCurrencyLabel.Text = userCurrency.Amount.ToString("#,##0");
        }

        public void SellMode(CurrencyInfo currency, List<ItemType> sellableItemTypes)
        {
            SetPrimaryCurrency(currency);

            SellableItemTypes = sellableItemTypes;

            InvMode = InventoryMode.Sell;
        }

        public void NormalMode()
        {
            foreach (DXItemCell cell in SelectedItems.ToArray())
                cell.Selected = false;

            SelectedItems.Clear();

            SetPrimaryCurrency(null);

            SellableItemTypes.Clear();

            InvMode = InventoryMode.Normal;
        }

        #region InventoryMode

        public InventoryMode InvMode
        {
            get => _InvMode;
            set
            {
                if (_InvMode == value) return;

                InventoryMode oldValue = _InvMode;
                _InvMode = value;

                OnInventoryModeChanged(oldValue, value);
            }
        }
        private InventoryMode _InvMode;
        public event EventHandler<EventArgs> InventoryModeChanged;
        public void OnInventoryModeChanged(InventoryMode oValue, InventoryMode nValue)
        {
            TrashButton.Visible = false;
            UndoDeleteButton.Visible = false;
            SellButton.Visible = false;

            DXItemCell.SelectedCell = null;

            switch (nValue)
            {
                case InventoryMode.Normal:
                    {
                        RefreshCurrency();

                        TrashButton.Visible = true;
                        UndoDeleteButton.Visible = recycleCount > 0;

                        TitleLabel.Text = CEnvir.Language.InventoryDialogTitle + $" ({Globals.InventorySize})";
                    }
                    break;
                case InventoryMode.Sell:
                    {
                        SecondaryCurrencyTitle.Text = "Total";
                        SecondaryCurrencyTitle.ForeColour = Color.CornflowerBlue;
                        SecondaryCurrencyLabel.Text = 0.ToString("#,##0");

                        SellButton.Visible = true;

                        TitleLabel.Text = CEnvir.Language.InventoryDialogTitle + $" ({Globals.InventorySize}) [Sell]";
                    }
                    break;
            }

            InventoryModeChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #endregion

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                if (Grid != null)
                {
                    if (!Grid.IsDisposed)
                        Grid.Dispose();

                    Grid = null;
                }

                if (TitleLabel != null)
                {
                    if (!TitleLabel.IsDisposed)
                        TitleLabel.Dispose();

                    TitleLabel = null;
                }

                if (PrimaryCurrencyLabel != null)
                {
                    if (!PrimaryCurrencyLabel.IsDisposed)
                        PrimaryCurrencyLabel.Dispose();

                    PrimaryCurrencyLabel = null;
                }

                if (SecondaryCurrencyLabel != null)
                {
                    if (!SecondaryCurrencyLabel.IsDisposed)
                        SecondaryCurrencyLabel.Dispose();

                    SecondaryCurrencyLabel = null;
                }

                if (PrimaryCurrencyTitle != null)
                {
                    if (!PrimaryCurrencyTitle.IsDisposed)
                        PrimaryCurrencyTitle.Dispose();

                    PrimaryCurrencyTitle = null;
                }

                if (SecondaryCurrencyTitle != null)
                {
                    if (!SecondaryCurrencyTitle.IsDisposed)
                        SecondaryCurrencyTitle.Dispose();

                    SecondaryCurrencyTitle = null;
                }

                if (WeightLabel != null)
                {
                    if (!WeightLabel.IsDisposed)
                        WeightLabel.Dispose();

                    WeightLabel = null;
                }

                if (WalletLabel != null)
                {
                    WalletLabel.MouseClick -= WalletLabel_MouseClick;

                    if (!WalletLabel.IsDisposed)
                        WalletLabel.Dispose();

                    WalletLabel = null;
                }

                if (CloseButton != null)
                {
                    if (!CloseButton.IsDisposed)
                        CloseButton.Dispose();

                    CloseButton = null;
                }

                if (SortButton != null)
                {
                    if (!SortButton.IsDisposed)
                        SortButton.Dispose();

                    SortButton = null;
                }

                if (TrashButton != null)
                {
                    if (!TrashButton.IsDisposed)
                        TrashButton.Dispose();

                    TrashButton = null;
                }

                if (SellButton != null)
                {
                    if (!SellButton.IsDisposed)
                        SellButton.Dispose();

                    SellButton = null;
                }

                if (PrimaryCurrencyTitle != null)
                {
                    if (!PrimaryCurrencyTitle.IsDisposed)
                        PrimaryCurrencyTitle.Dispose();

                    SecondaryCurrencyTitle = null;
                }

                if (SecondaryCurrencyTitle != null)
                {
                    if (!SecondaryCurrencyTitle.IsDisposed)
                        SecondaryCurrencyTitle.Dispose();

                    SecondaryCurrencyTitle = null;
                }
            }
        }

        #endregion
    }
}
