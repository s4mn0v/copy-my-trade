#region Using declarations
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class TradeCopier : NinjaTrader.NinjaScript.AddOnBase
    {
        private TradeCopierWindow window;
        private NTMenuItem toolsMenuItem;
        private NTMenuItem tradeCopierMenuItem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Multi-Account Trade Copier Addon";
                Name = "Trade Copier";
            }
            else if (State == State.Terminated)
            {
                RemoveControlCenterMenuItem();
                if (window != null)
                {
                    window.Close();
                    window = null;
                }
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            var controlCenter = window as NinjaTrader.Gui.ControlCenter;
            if (controlCenter != null)
                AddControlCenterMenuItem(controlCenter);

            if (window is TradeCopierWindow)
                this.window = window as TradeCopierWindow;
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (window is NinjaTrader.Gui.ControlCenter)
                RemoveControlCenterMenuItem();

            if (window is TradeCopierWindow)
                this.window = null;
        }

        private void AddControlCenterMenuItem(NinjaTrader.Gui.ControlCenter controlCenter)
        {
            if (controlCenter == null || tradeCopierMenuItem != null)
                return;

            toolsMenuItem = controlCenter.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;
            if (toolsMenuItem == null)
                return;

            tradeCopierMenuItem = new NTMenuItem
            {
                Header = "Trade Copier",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };
            tradeCopierMenuItem.Click += TradeCopierMenuItem_Click;
            toolsMenuItem.Items.Add(tradeCopierMenuItem);
        }

        private void RemoveControlCenterMenuItem()
        {
            if (tradeCopierMenuItem != null)
                tradeCopierMenuItem.Click -= TradeCopierMenuItem_Click;

            if (toolsMenuItem != null && tradeCopierMenuItem != null && toolsMenuItem.Items.Contains(tradeCopierMenuItem))
                toolsMenuItem.Items.Remove(tradeCopierMenuItem);

            tradeCopierMenuItem = null;
            toolsMenuItem = null;
        }

        private void TradeCopierMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (window == null || !window.IsVisible)
            {
                window = new TradeCopierWindow();
                window.Show();
                return;
            }

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Focus();
        }
    }

    public class FollowerRow : INotifyPropertyChanged
    {
        public const double MinRatio = 0.1;
        public const double MaxRatio = 2.0;

        private bool enabled;
        private string accountName;
        private double profitTarget;
        private double ratio = 1.0;
        private string status;

        public bool TargetReachedFlag { get; set; }

        public bool Enabled
        {
            get => enabled;
            set
            {
                bool turningOn = value && !enabled;
                enabled = value;
                if (turningOn)
                    TargetReachedFlag = false;
                OnPropertyChanged(nameof(Enabled));
            }
        }

        public string AccountName
        {
            get => accountName;
            set { accountName = value; OnPropertyChanged(nameof(AccountName)); }
        }

        public double ProfitTarget
        {
            get => profitTarget;
            set
            {
                double clamped = Math.Max(0, value);
                if (clamped != profitTarget)
                    TargetReachedFlag = false;
                profitTarget = clamped;
                OnPropertyChanged(nameof(ProfitTarget));
            }
        }

        public double Ratio
        {
            get => ratio;
            set
            {
                ratio = Math.Max(MinRatio, Math.Min(MaxRatio, value));
                OnPropertyChanged(nameof(Ratio));
            }
        }

        public string Status
        {
            get => status;
            set { status = value; OnPropertyChanged(nameof(Status)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class TradeCopierCore : INotifyPropertyChanged
    {
        public Action<string> Logger { get; set; }

        public ObservableCollection<string> LeaderOptions { get; } = new ObservableCollection<string>();
        public ObservableCollection<FollowerRow> Followers { get; } = new ObservableCollection<FollowerRow>();

        private string selectedLeader;
        public string SelectedLeader
        {
            get => selectedLeader;
            set
            {
                selectedLeader = value;
                OnPropertyChanged(nameof(SelectedLeader));
                RefreshAccounts();
            }
        }

        private bool isRunning;
        public bool IsRunning
        {
            get => isRunning;
            private set { isRunning = value; OnPropertyChanged(nameof(IsRunning)); }
        }

        private int targetQuantity = 1;
        public int TargetQuantity
        {
            get => targetQuantity;
            set { targetQuantity = Math.Max(1, value); OnPropertyChanged(nameof(TargetQuantity)); }
        }

        private DispatcherTimer refreshTimer;
        public TradeCopierEngine Engine { get; private set; }

        private bool terminated;

        public void Initialize()
        {
            RefreshAccounts();

            refreshTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                Application.Current.Dispatcher
                ) 
            { 
                Interval = TimeSpan.FromSeconds(8) // Before: 3
            };
            refreshTimer.Tick += (s, e) => RefreshAccounts();
            refreshTimer.Start();

            Engine = new TradeCopierEngine(this);
            Engine.AttachToLeader(SelectedLeader);

            Log("Core initialized.");
        }

        public void Terminate()
        {
            if (terminated)
                return;
            terminated = true;

            refreshTimer?.Stop();
            refreshTimer = null;

            Engine?.Dispose();
            Engine = null;
        }

        public void RefreshAccounts()
        {
            try
            {
                var connectedAccounts = new System.Collections.Generic.HashSet<string>(
                    Account.All
                        .Where(a => a.Connection != null && a.Connection.Status == ConnectionStatus.Connected)
                        .Select(a => a.Name));

                foreach (var name in LeaderOptions.Where(n => !connectedAccounts.Contains(n)).ToList())
                    LeaderOptions.Remove(name);

                foreach (var name in connectedAccounts.Where(n => !LeaderOptions.Contains(n)).OrderBy(n => n))
                    LeaderOptions.Add(name);

                if (SelectedLeader == null || !LeaderOptions.Contains(SelectedLeader))
                    SelectedLeader = LeaderOptions.FirstOrDefault();

                foreach (var row in Followers.Where(r => !connectedAccounts.Contains(r.AccountName)).ToList())
                    Followers.Remove(row);

                foreach (var name in connectedAccounts.Where(n => Followers.All(r => r.AccountName != n)))
                {
                    Followers.Add(new FollowerRow
                    {
                        Enabled = false,
                        AccountName = name,
                        ProfitTarget = 0,
                        Ratio = 1,
                        Status = "Ready"
                    });
                }

                foreach (var row in Followers)
                    RecomputeStatus(row, connectedAccounts);
            }
            catch (Exception ex)
            {
                Log("Error refreshing accounts: " + ex.Message);
            }
        }

        private void RecomputeStatus(FollowerRow row, System.Collections.Generic.HashSet<string> connectedAccounts)
        {
            if (!connectedAccounts.Contains(row.AccountName))
            {
                row.Status = "Disconnected";
                return;
            }

            if (row.Enabled && !string.IsNullOrEmpty(SelectedLeader) &&
                string.Equals(row.AccountName, SelectedLeader, StringComparison.OrdinalIgnoreCase))
            {
                row.Enabled = false;
                Log("[Copier] " + row.AccountName + " is the selected Leader; its Follower row was turned off " +
                    "to prevent copying it onto itself.");
            }

            if (!row.TargetReachedFlag && row.ProfitTarget > 0)
            {
                var account = Account.All.FirstOrDefault(a => a.Name == row.AccountName);
                if (account != null)
                {
                    try
                    {
                        double totalPnl = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar)
                                        + account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);

                        if (totalPnl >= row.ProfitTarget)
                        {
                            row.TargetReachedFlag = true;
                            Log("[Target] " + row.AccountName + " reached its profit target ($" +
                                totalPnl.ToString("N2") + " >= $" + row.ProfitTarget.ToString("N2") +
                                "). Copying to this account is now paused.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("[Target] Could not read P&L for " + row.AccountName + ": " + ex.Message);
                    }
                }
            }

            if (row.TargetReachedFlag)
                row.Status = "Target Reached";
            else if (IsRunning && row.Enabled)
                row.Status = "Copying";
            else
                row.Status = "Ready";
        }

        public void RecalculateRatios()
        {
            if (string.IsNullOrEmpty(SelectedLeader))
            {
                Log("[Sizing] No leader selected, cannot calculate ratios.");
                return;
            }

            var leaderAccount = Account.All.FirstOrDefault(a => a.Name == SelectedLeader);
            if (leaderAccount == null)
            {
                Log("[Sizing] Leader account not found.");
                return;
            }

            double leaderCapital;
            try
            {
                leaderCapital = leaderAccount.Get(AccountItem.CashValue, Currency.UsDollar);
            }
            catch (Exception ex)
            {
                Log("[Sizing] Could not read the leader's cash value: " + ex.Message);
                return;
            }

            if (leaderCapital <= 0)
            {
                Log("[Sizing] Leader account has no positive cash value; cannot calculate ratios.");
                return;
            }

            foreach (var row in Followers)
            {
                var followerAccount = Account.All.FirstOrDefault(a => a.Name == row.AccountName);
                if (followerAccount == null)
                    continue;

                try
                {
                    double followerCapital = followerAccount.Get(AccountItem.CashValue, Currency.UsDollar);
                    row.Ratio = followerCapital / leaderCapital;

                    int estimatedQty = (int)Math.Round(TargetQuantity * row.Ratio, MidpointRounding.AwayFromZero);

                    Log("[Sizing] " + row.AccountName + ": cash $" + followerCapital.ToString("N0") +
                        " vs leader $" + leaderCapital.ToString("N0") + " -> ratio " + row.Ratio.ToString("0.00") +
                        " (~" + estimatedQty + " contract(s) if the leader trades " + TargetQuantity + ").");
                }
                catch (Exception ex)
                {
                    Log("[Sizing] Could not read cash value for " + row.AccountName + ": " + ex.Message);
                }
            }
        }

        public void Start()
        {
            IsRunning = true;
            Log("START pressed. Copying is now ACTIVE.");
            RefreshAccounts();
        }

        public void Stop()
        {
            IsRunning = false;
            Log("STOP pressed. Copying is now PAUSED (existing open orders/positions are not touched).");
            RefreshAccounts();
        }

        public void Log(string message)
        {
            Logger?.Invoke(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class TradeCopierEngine : IDisposable
    {
        private readonly TradeCopierCore core;
        private readonly object syncRoot = new object();

        private Account leaderAccount;

        private readonly System.Collections.Generic.Dictionary<Order, System.Collections.Generic.Dictionary<string, Order>> replicaMap =
            new System.Collections.Generic.Dictionary<Order, System.Collections.Generic.Dictionary<string, Order>>();

        private readonly System.Collections.Generic.Dictionary<Order, (int Quantity, double LimitPrice, double StopPrice)> lastKnownState =
            new System.Collections.Generic.Dictionary<Order, (int, double, double)>();

        private readonly System.Collections.Generic.HashSet<Order> ignoredOrders = new System.Collections.Generic.HashSet<Order>();

        private const string CopyOrderNamePrefix = "[Copy] ";

        private static bool IsCopierGeneratedOrder(Order order) =>
            order?.Name != null && order.Name.StartsWith(CopyOrderNamePrefix, StringComparison.Ordinal);

        public TradeCopierEngine(TradeCopierCore core)
        {
            this.core = core;
            core.PropertyChanged += OnCorePropertyChanged;
        }

        private void OnCorePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TradeCopierCore.SelectedLeader))
                AttachToLeader(core.SelectedLeader);
        }

        public void AttachToLeader(string leaderName)
        {
            DetachFromLeader();

            if (string.IsNullOrEmpty(leaderName))
                return;

            leaderAccount = Account.All.FirstOrDefault(a => a.Name == leaderName);

            if (leaderAccount == null)
            {
                core.Log("[Copier] Leader account not found: " + leaderName);
                return;
            }

            lock (syncRoot)
            {
                ignoredOrders.Clear();

                lock (leaderAccount.Orders)
                {
                    foreach (var existingOrder in leaderAccount.Orders)
                    {
                        if (!IsTerminal(existingOrder.OrderState))
                            ignoredOrders.Add(existingOrder);
                    }
                }

                if (ignoredOrders.Count > 0)
                    core.Log("[Copier] Ignoring " + ignoredOrders.Count +
                              " pre-existing order(s) already open on the leader.");
            }

            leaderAccount.OrderUpdate += OnLeaderOrderUpdate;
            leaderAccount.ExecutionUpdate += OnLeaderExecutionUpdate;

            core.Log("[Copier] Now listening to leader: " + leaderAccount.Name);
        }

        public void DetachFromLeader()
        {
            if (leaderAccount != null)
            {
                leaderAccount.OrderUpdate -= OnLeaderOrderUpdate;
                leaderAccount.ExecutionUpdate -= OnLeaderExecutionUpdate;
                core.Log("[Copier] Stopped listening to leader: " + leaderAccount.Name);
            }

            leaderAccount = null;

            lock (syncRoot)
            {
                replicaMap.Clear();
                lastKnownState.Clear();
                ignoredOrders.Clear();
            }
        }

        public void Dispose()
        {
            core.PropertyChanged -= OnCorePropertyChanged;
            DetachFromLeader();
        }

        private System.Collections.Generic.List<Account> GetFollowerAccounts()
        {
            return core.Followers
                .Where(f => f.Enabled && !f.TargetReachedFlag)
                .Where(f => leaderAccount == null ||
                            !string.Equals(f.AccountName, leaderAccount.Name, StringComparison.OrdinalIgnoreCase))
                .Select(f => Account.All.FirstOrDefault(a => a.Name == f.AccountName))
                .Where(a => a != null && a.Connection != null && a.Connection.Status == ConnectionStatus.Connected)
                .ToList();
        }

        private FollowerRow GetFollowerRow(string accountName) =>
            core.Followers.FirstOrDefault(f => f.AccountName == accountName);

        private void OnLeaderOrderUpdate(object sender, OrderEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => HandleLeaderOrderUpdateOnUiThread(e)));
        }

        private void HandleLeaderOrderUpdateOnUiThread(OrderEventArgs e)
        {
            var leaderOrder = e.Order;
            if (leaderOrder == null)
                return;

            if (IsCopierGeneratedOrder(leaderOrder))
            {
                core.Log("[Copier] Ignoring a self-generated order seen on the leader feed " +
                          "(check that the Leader account isn't also enabled as a Follower).");
                return;
            }

            lock (syncRoot)
            {
                if (ignoredOrders.Contains(leaderOrder))
                    return;
            }

            if (!core.IsRunning)
                return;

            try
            {
                switch (e.OrderState)
                {
                    case OrderState.Accepted:
                    case OrderState.Working:
                        lock (syncRoot)
                        {
                            if (!replicaMap.ContainsKey(leaderOrder))
                                ReplicateNewOrder(leaderOrder);
                            else
                                ReplicateModifyIfChanged(leaderOrder);
                        }
                        break;

                    case OrderState.Cancelled:
                        lock (syncRoot)
                        {
                            ReplicateCancel(leaderOrder);
                        }
                        break;

                    case OrderState.Rejected:
                        core.Log("[Copier] Leader order rejected, nothing copied: " + DescribeOrder(leaderOrder));
                        lock (syncRoot)
                        {
                            replicaMap.Remove(leaderOrder);
                            lastKnownState.Remove(leaderOrder);
                        }
                        break;

                    case OrderState.PartFilled:
                        core.Log("[Copier] Leader order partially filled: " + DescribeOrder(leaderOrder) +
                                  " (" + leaderOrder.Filled + "/" + leaderOrder.Quantity + ")");
                        break;

                    case OrderState.Filled:
                        core.Log("[Copier] Leader order filled: " + DescribeOrder(leaderOrder));
                        lock (syncRoot)
                        {
                            replicaMap.Remove(leaderOrder);
                            lastKnownState.Remove(leaderOrder);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                core.Log("[Copier] Error handling leader order update: " + ex.Message);
            }
        }

        private void OnLeaderExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                var execution = e.Execution;
                if (execution?.Order == null)
                    return;

                core.Log("[Copier] Leader execution: " + execution.Order.Instrument?.FullName + " " +
                          execution.Order.OrderAction + " " + execution.Quantity + " @ " + execution.Price);
            }
            catch (Exception ex)
            {
                core.Log("[Copier] Error logging leader execution: " + ex.Message);
            }
        }

        private void ReplicateNewOrder(Order leaderOrder)
        {
            var followerAccounts = GetFollowerAccounts();

            if (followerAccounts.Count == 0)
            {
                core.Log("[Copier] New leader order (" + DescribeOrder(leaderOrder) +
                          ") but there are no enabled/connected followers.");
                return;
            }

            var followerOrders = new System.Collections.Generic.Dictionary<string, Order>();
            bool isExit = IsExitOrderForLeader(leaderOrder);

            foreach (var followerAccount in followerAccounts)
            {
                var followerRow = GetFollowerRow(followerAccount.Name);
                double ratio = followerRow?.Ratio ?? 1.0;
                int quantityToSend = ApplyRatio(leaderOrder.Quantity, ratio);

                if (quantityToSend <= 0)
                {
                    core.Log("[Copier] Skipped " + followerAccount.Name + ": ratio " + ratio.ToString("0.00") +
                              " rounds " + leaderOrder.Quantity + " contract(s) down to 0 for " + DescribeOrder(leaderOrder));
                    continue;
                }

                if (isExit)
                {
                    int followerQty = GetFollowerPositionQuantity(followerAccount, leaderOrder.Instrument, out var followerMarketPosition);

                    bool matchesDirection =
                        (leaderOrder.OrderAction == OrderAction.Sell && followerMarketPosition == MarketPosition.Long) ||
                        (leaderOrder.OrderAction == OrderAction.BuyToCover && followerMarketPosition == MarketPosition.Short);

                    if (!matchesDirection || followerQty <= 0)
                    {
                        core.Log("[Copier] Skipped " + followerAccount.Name +
                                  ": no matching open position to exit for " + DescribeOrder(leaderOrder));
                        continue;
                    }

                    quantityToSend = Math.Min(quantityToSend, followerQty);
                }

                string followerOco = string.IsNullOrEmpty(leaderOrder.Oco)
                    ? string.Empty
                    : leaderOrder.Oco + "_" + followerAccount.Name;

                Order newOrder;
                try
                {
                    newOrder = followerAccount.CreateOrder(
                        leaderOrder.Instrument,
                        leaderOrder.OrderAction,
                        leaderOrder.OrderType,
                        OrderEntry.Manual,
                        leaderOrder.TimeInForce,
                        quantityToSend,
                        leaderOrder.LimitPrice,
                        leaderOrder.StopPrice,
                        followerOco,
                        "[Copy] " + leaderOrder.Name,
                        leaderOrder.Gtd,
                        null);
                }
                catch (Exception ex)
                {
                    core.Log("[Copier] Failed to build order for " + followerAccount.Name + ": " + ex.Message);
                    continue;
                }

                followerOrders[followerAccount.Name] = newOrder;

                try
                {
                    followerAccount.Submit(new[] { newOrder });
                    core.Log("[Copier] Copied to " + followerAccount.Name + ": " + DescribeOrder(leaderOrder));
                }
                catch (Exception ex)
                {
                    core.Log("[Copier] Failed to submit copy to " + followerAccount.Name + ": " + ex.Message);
                }
            }

            replicaMap[leaderOrder] = followerOrders;
            lastKnownState[leaderOrder] = (leaderOrder.Quantity, leaderOrder.LimitPrice, leaderOrder.StopPrice);
        }

        private bool IsExitOrderForLeader(Order leaderOrder)
        {
            lock (leaderAccount.Positions)
            {
                var leaderPosition = leaderAccount.Positions.FirstOrDefault(p => p.Instrument == leaderOrder.Instrument);
                var currentPosition = leaderPosition?.MarketPosition ?? MarketPosition.Flat;

                bool sellSide = leaderOrder.OrderAction == OrderAction.Sell || leaderOrder.OrderAction == OrderAction.SellShort;
                bool buySide = leaderOrder.OrderAction == OrderAction.Buy || leaderOrder.OrderAction == OrderAction.BuyToCover;

                if (sellSide) return currentPosition == MarketPosition.Long;
                if (buySide) return currentPosition == MarketPosition.Short;
            }
            return false;
        }

        private void ReplicateModifyIfChanged(Order leaderOrder)
        {
            if (!lastKnownState.TryGetValue(leaderOrder, out var last))
                return;

            bool changed = last.Quantity != leaderOrder.Quantity
                        || last.LimitPrice != leaderOrder.LimitPrice
                        || last.StopPrice != leaderOrder.StopPrice;

            if (!changed)
                return;

            lastKnownState[leaderOrder] = (leaderOrder.Quantity, leaderOrder.LimitPrice, leaderOrder.StopPrice);

            if (!replicaMap.TryGetValue(leaderOrder, out var followerOrders))
                return;

            core.Log("[Copier] Leader order modified: " + DescribeOrder(leaderOrder));

            foreach (var kvp in followerOrders)
            {
                var followerOrder = kvp.Value;
                if (followerOrder == null || IsTerminal(followerOrder.OrderState))
                    continue;

                var followerRow = GetFollowerRow(kvp.Key);
                double ratio = followerRow?.Ratio ?? 1.0;
                int scaledQuantity = ApplyRatio(leaderOrder.Quantity, ratio);

                if (scaledQuantity <= 0)
                {
                    core.Log("[Copier] Skipped modification on " + kvp.Key + ": ratio " + ratio.ToString("0.00") +
                              " rounds down to 0");
                    continue;
                }

                followerOrder.QuantityChanged = scaledQuantity;
                followerOrder.LimitPriceChanged = leaderOrder.LimitPrice;
                followerOrder.StopPriceChanged = leaderOrder.StopPrice;

                try
                {
                    followerOrder.Account.Change(new[] { followerOrder });
                    core.Log("[Copier] Modification propagated to " + kvp.Key);
                }
                catch (Exception ex)
                {
                    core.Log("[Copier] Failed to modify order on " + kvp.Key + ": " + ex.Message);
                }
            }
        }

        private void ReplicateCancel(Order leaderOrder)
        {
            if (!replicaMap.TryGetValue(leaderOrder, out var followerOrders))
                return;

            core.Log("[Copier] Leader order cancelled: " + DescribeOrder(leaderOrder));

            foreach (var kvp in followerOrders)
            {
                var followerOrder = kvp.Value;
                if (followerOrder == null || IsTerminal(followerOrder.OrderState))
                    continue;

                try
                {
                    followerOrder.Account.Cancel(new[] { followerOrder });
                    core.Log("[Copier] Cancellation propagated to " + kvp.Key);
                }
                catch (Exception ex)
                {
                    core.Log("[Copier] Failed to cancel order on " + kvp.Key + ": " + ex.Message);
                }
            }

            replicaMap.Remove(leaderOrder);
            lastKnownState.Remove(leaderOrder);
        }

        private static bool IsTerminal(OrderState state) =>
            state == OrderState.Filled || state == OrderState.Cancelled || state == OrderState.Rejected;

        private static int ApplyRatio(int leaderQuantity, double ratio) =>
            (int)Math.Round(leaderQuantity * ratio, MidpointRounding.AwayFromZero);

        private static int GetFollowerPositionQuantity(Account followerAccount, Instrument instrument, out MarketPosition marketPosition)
        {
            lock (followerAccount.Positions)
            {
                var position = followerAccount.Positions.FirstOrDefault(p => p.Instrument == instrument);

                if (position == null || position.MarketPosition == MarketPosition.Flat)
                {
                    marketPosition = MarketPosition.Flat;
                    return 0;
                }

                marketPosition = position.MarketPosition;
                return position.Quantity;
            }
        }

        private static string DescribeOrder(Order order)
        {
            string action;
            switch (order.OrderAction)
            {
                case OrderAction.Buy: action = "BUY entry"; break;
                case OrderAction.SellShort: action = "SELL SHORT entry"; break;
                case OrderAction.Sell: action = "SELL exit"; break;
                case OrderAction.BuyToCover: action = "BUY TO COVER exit"; break;
                default: action = order.OrderAction.ToString(); break;
            }

            string name = order.Name ?? string.Empty;
            string kind = null;

            if (name.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0)
                kind = "Stop Loss";
            else if (name.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("profit", StringComparison.OrdinalIgnoreCase) >= 0)
                kind = "Take Profit";

            string label = kind != null ? kind + " (" + action + ")" : action;

            return order.Instrument?.FullName + " " + label + " x" + order.Quantity;
        }
    }

    public partial class TradeCopierWindow : NTWindow
    {
        private readonly TradeCopierCore core;
        private TextBox LogBox;

        public TradeCopierWindow()
        {
            core = new TradeCopierCore();
            DataContext = core;

            Caption = "Trade Copier";
            Width = 780;
            Height = 520;

            core.Logger = (msg) => Dispatcher.InvokeAsync(() =>
            {
                LogBox.AppendText(msg + Environment.NewLine);
                LogBox.ScrollToEnd();
            });

            string ui = @"
<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
      Background=""#1E1F22"">
    <Grid.Resources>
        <Style TargetType=""DataGridColumnHeader"">
            <Setter Property=""Background"" Value=""#383A40""/>
            <Setter Property=""Foreground"" Value=""White""/>
            <Setter Property=""Padding"" Value=""6,4""/>
            <Setter Property=""FontWeight"" Value=""SemiBold""/>
        </Style>
        <Style TargetType=""TextBlock"" x:Key=""LabelStyle"">
            <Setter Property=""Foreground"" Value=""White""/>
            <Setter Property=""VerticalAlignment"" Value=""Center""/>
            <Setter Property=""Margin"" Value=""0,0,8,0""/>
        </Style>
        <Style TargetType=""Ellipse"" x:Key=""StatusDotStyle"">
            <Setter Property=""Width"" Value=""9""/>
            <Setter Property=""Height"" Value=""9""/>
            <Setter Property=""Margin"" Value=""0,0,6,0""/>
            <Setter Property=""VerticalAlignment"" Value=""Center""/>
            <Setter Property=""Fill"" Value=""#9E9E9E""/>
        </Style>
    </Grid.Resources>

    <Grid.ColumnDefinitions>
        <ColumnDefinition Width=""3*""/>
        <ColumnDefinition Width=""2*""/>
    </Grid.ColumnDefinitions>

    <Grid Grid.Column=""0"" Margin=""10,10,6,10"">
        <Grid.RowDefinitions>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""*""/>
        </Grid.RowDefinitions>

        <Grid Grid.Row=""0"" Margin=""0,0,0,10"">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width=""Auto""/>
                <ColumnDefinition Width=""*""/>
                <ColumnDefinition Width=""Auto""/>
            </Grid.ColumnDefinitions>

            <StackPanel Grid.Column=""0"" Orientation=""Horizontal"">
                <TextBlock Text=""Leader"" Style=""{StaticResource LabelStyle}"" FontWeight=""Bold""/>
                <ComboBox x:Name=""LeaderCombo"" Width=""160"" Background=""#383A40"" Foreground=""White""
                          ItemsSource=""{Binding LeaderOptions}""
                          SelectedItem=""{Binding SelectedLeader, UpdateSourceTrigger=PropertyChanged}""/>
            </StackPanel>

            <StackPanel Grid.Column=""2"" Orientation=""Horizontal"">
                <TextBlock Text=""Target Qty"" Style=""{StaticResource LabelStyle}""/>
                <TextBox x:Name=""TargetQtyBox"" Width=""48"" Background=""#383A40"" Foreground=""White""
                         VerticalContentAlignment=""Center"" TextAlignment=""Center""
                         Text=""{Binding TargetQuantity, UpdateSourceTrigger=PropertyChanged}""/>
                <Button x:Name=""CalculateRatiosBtn"" Content=""Calculate Ratios"" Margin=""8,0,0,0"" Padding=""8,3""
                        Background=""#383A40"" Foreground=""White""/>
            </StackPanel>
        </Grid>

        <TextBlock Grid.Row=""1"" Text=""Followers"" Foreground=""White"" FontWeight=""Bold"" Margin=""0,0,0,4""/>

        <DataGrid x:Name=""FollowersGrid"" Grid.Row=""2""
                  ItemsSource=""{Binding Followers}"" AutoGenerateColumns=""False""
                  CanUserAddRows=""False"" HeadersVisibility=""Column""
                  Background=""#1E1F22"" RowBackground=""#2B2D31"" AlternatingRowBackground=""#232529""
                  Foreground=""White"" GridLinesVisibility=""Horizontal"" HorizontalGridLinesBrush=""#383A40"">
            <DataGrid.Columns>
                <DataGridCheckBoxColumn Header=""On"" Binding=""{Binding Enabled, UpdateSourceTrigger=PropertyChanged}"" Width=""36""/>
                <DataGridTextColumn Header=""Account"" Binding=""{Binding AccountName}"" IsReadOnly=""True"" Width=""*""/>
                <DataGridTextColumn Header=""Profit Target"" Binding=""{Binding ProfitTarget, UpdateSourceTrigger=PropertyChanged}"" Width=""100""/>
                <DataGridTextColumn Header=""Ratio"" Binding=""{Binding Ratio, UpdateSourceTrigger=PropertyChanged, StringFormat={}{0:0.0}}"" Width=""64""/>
                <DataGridTemplateColumn Header=""Status"" Width=""130"">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <StackPanel Orientation=""Horizontal"" VerticalAlignment=""Center"" Margin=""4,0,0,0"">
                                <Ellipse>
                                    <Ellipse.Style>
                                        <Style TargetType=""Ellipse"" BasedOn=""{StaticResource StatusDotStyle}"">
                                            <Style.Triggers>
                                                <DataTrigger Binding=""{Binding Status}"" Value=""Copying"">
                                                    <Setter Property=""Fill"" Value=""#4CAF50""/>
                                                </DataTrigger>
                                                <DataTrigger Binding=""{Binding Status}"" Value=""Target Reached"">
                                                    <Setter Property=""Fill"" Value=""#FFC107""/>
                                                </DataTrigger>
                                                <DataTrigger Binding=""{Binding Status}"" Value=""Disconnected"">
                                                    <Setter Property=""Fill"" Value=""#E53935""/>
                                                </DataTrigger>
                                                <DataTrigger Binding=""{Binding Status}"" Value=""Ready"">
                                                    <Setter Property=""Fill"" Value=""#5C9EEC""/>
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </Ellipse.Style>
                                </Ellipse>
                                <TextBlock Text=""{Binding Status}"" Foreground=""White"" VerticalAlignment=""Center""/>
                            </StackPanel>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>

    <Grid Grid.Column=""1"" Margin=""6,10,10,10"">
        <Grid.RowDefinitions>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""*""/>
        </Grid.RowDefinitions>

        <Border Grid.Row=""0"" Background=""#2B2D31"" CornerRadius=""4"" Padding=""10,8"" Margin=""0,0,0,8"">
            <StackPanel Orientation=""Horizontal"">
                <Ellipse>
                    <Ellipse.Style>
                        <Style TargetType=""Ellipse"" BasedOn=""{StaticResource StatusDotStyle}"">
                            <Style.Triggers>
                                <DataTrigger Binding=""{Binding IsRunning}"" Value=""True"">
                                    <Setter Property=""Fill"" Value=""#4CAF50""/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Ellipse.Style>
                </Ellipse>
                <TextBlock Foreground=""White"" FontWeight=""Bold"" VerticalAlignment=""Center"">
                    <TextBlock.Style>
                        <Style TargetType=""TextBlock"">
                            <Setter Property=""Text"" Value=""Suspended""/>
                            <Style.Triggers>
                                <DataTrigger Binding=""{Binding IsRunning}"" Value=""True"">
                                    <Setter Property=""Text"" Value=""Started""/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </StackPanel>
        </Border>

        <StackPanel Grid.Row=""1"" Orientation=""Horizontal"" Margin=""0,0,0,10"">
            <Button x:Name=""StartBtn"" Content=""START"" Width=""90"" Padding=""6""
                    Background=""#2E7D32"" Foreground=""White"" FontWeight=""Bold""/>
            <Button x:Name=""StopBtn"" Content=""STOP"" Width=""90"" Padding=""6"" Margin=""8,0,0,0""
                    Background=""#C62828"" Foreground=""White"" FontWeight=""Bold""/>
        </StackPanel>

        <TextBlock Grid.Row=""2"" Text=""Log"" Foreground=""White"" FontWeight=""Bold"" Margin=""0,0,0,4""/>
        <TextBox x:Name=""LogBox"" Grid.Row=""3"" Background=""#0F1012"" Foreground=""#00FF00""
                 IsReadOnly=""True"" TextWrapping=""NoWrap"" FontFamily=""Consolas""
                 VerticalScrollBarVisibility=""Auto"" HorizontalScrollBarVisibility=""Auto""/>
    </Grid>
</Grid>";

            this.Content = (FrameworkElement)XamlReader.Parse(ui);

            LogBox = (TextBox)LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "LogBox");
            var startBtn = (Button)LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "StartBtn");
            var stopBtn = (Button)LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "StopBtn");
            var calculateBtn = (Button)LogicalTreeHelper.FindLogicalNode((DependencyObject)this.Content, "CalculateRatiosBtn");

            startBtn.Click += (s, e) => core.Start();
            stopBtn.Click += (s, e) => core.Stop();
            calculateBtn.Click += (s, e) => core.RecalculateRatios();

            core.Initialize();
            core.Log("UI loaded.");

            Closing += TradeCopierWindow_Closing;
        }

        private void TradeCopierWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            core.Terminate();
        }
    }
}