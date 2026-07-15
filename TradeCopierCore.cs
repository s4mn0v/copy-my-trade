#region Using declarations
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using NinjaTrader.Cbi;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    // ---------------------------------------------------------------
    // Represents a row of the "Followers" table.
    // Implements INotifyPropertyChanged so changes made from the UI
    // (checkbox, Profit Target, Ratio) are reflected both ways.
    // ---------------------------------------------------------------
    public class FollowerRow : INotifyPropertyChanged
    {
        // Allowed range for Ratio. Anything set outside this range
        // (typed in the grid, or computed by RecalculateRatios) is
        // clamped back into range.
        public const double MinRatio = 0.1;
        public const double MaxRatio = 2.0;

        private bool enabled;
        private string accountName;
        private double profitTarget;
        private double ratio = 1.0;
        private string status;

        // Sticky flag: once the Profit Target is reached it stays reached
        // (Status = "Target Reached", excluded from copying) until the user
        // edits the Profit Target or re-enables the row.
        public bool TargetReachedFlag { get; set; }

        public bool Enabled
        {
            get => enabled;
            set
            {
                bool turningOn = value && !enabled;
                enabled = value;
                if (turningOn)
                    TargetReachedFlag = false; // give it a fresh shot when re-enabled
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
                    TargetReachedFlag = false; // re-arm when the target changes
                profitTarget = clamped;
                OnPropertyChanged(nameof(ProfitTarget));
            }
        }

        // Clamped to [MinRatio, MaxRatio] no matter where the value comes
        // from: typed directly in the grid, or set by RecalculateRatios().
        public double Ratio
        {
            get => ratio;
            set
            {
                ratio = Math.Max(MinRatio, Math.Min(MaxRatio, value));
                OnPropertyChanged(nameof(Ratio));
            }
        }

        // One of: "Ready", "Copying", "Target Reached", "Disconnected".
        public string Status
        {
            get => status;
            set { status = value; OnPropertyChanged(nameof(Status)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ---------------------------------------------------------------
    // Core: holds the state and logic (visual/placeholder only for now).
    // The window (TradeCopierWindow) binds directly against this class.
    // ---------------------------------------------------------------
    public class TradeCopierCore : INotifyPropertyChanged
    {
        // Delegate used by the window to receive log messages.
        public Action<string> Logger { get; set; }

        // Accounts available to be selected as Leader (connected accounts only).
        public ObservableCollection<string> LeaderOptions { get; } = new ObservableCollection<string>();

        // Rows of the Followers table (connected accounts only).
        public ObservableCollection<FollowerRow> Followers { get; } = new ObservableCollection<FollowerRow>();

        private string selectedLeader;
        public string SelectedLeader
        {
            get => selectedLeader;
            set
            {
                selectedLeader = value;
                OnPropertyChanged(nameof(SelectedLeader));
                RefreshAccounts(); // immediately re-check the self-copy guard against the new leader
            }
        }

        private bool isRunning;
        public bool IsRunning
        {
            get => isRunning;
            private set { isRunning = value; OnPropertyChanged(nameof(IsRunning)); }
        }

        // Approximate quantity the user wants the Leader's trades sized at.
        // Used only by RecalculateRatios() as a reference for the estimated
        // quantity preview it logs; it does not change any live order.
        private int targetQuantity = 1;
        public int TargetQuantity
        {
            get => targetQuantity;
            set { targetQuantity = Math.Max(1, value); OnPropertyChanged(nameof(TargetQuantity)); }
        }

        private DispatcherTimer refreshTimer;

        // Owns the "listen to Leader, replicate to Followers" responsibility.
        // See TradeCopierEngine.cs.
        public TradeCopierEngine Engine { get; private set; }

        public void Initialize()
        {
            RefreshAccounts();

            // Periodic refresh of the connected accounts list, statuses and
            // profit-target checks. This also covers the case where the user
            // connects/disconnects an account while the window is open.
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            refreshTimer.Tick += (s, e) => RefreshAccounts();
            refreshTimer.Start();

            Engine = new TradeCopierEngine(this);
            Engine.AttachToLeader(SelectedLeader);

            Log("Core initialized.");
        }

        public void Terminate()
        {
            refreshTimer?.Stop();
            refreshTimer = null;

            Engine?.Dispose();
            Engine = null;
        }

        // Retrieves only the currently connected accounts (includes Sim/Demo),
        // keeps the Leader/Follower lists in sync with them, and recomputes
        // each Follower's Status (including the Profit Target check).
        public void RefreshAccounts()
        {
            try
            {
                var connectedAccounts = Account.All
                    .Where(a => a.Connection != null && a.Connection.Status == ConnectionStatus.Connected)
                    .Select(a => a.Name)
                    .OrderBy(n => n)
                    .ToList();

                // --- Update the Leader combo box ---
                foreach (var name in LeaderOptions.Where(n => !connectedAccounts.Contains(n)).ToList())
                    LeaderOptions.Remove(name);

                foreach (var name in connectedAccounts.Where(n => !LeaderOptions.Contains(n)))
                    LeaderOptions.Add(name);

                if (SelectedLeader == null || !LeaderOptions.Contains(SelectedLeader))
                    SelectedLeader = LeaderOptions.FirstOrDefault();

                // --- Update the Followers table ---
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

                // --- Recompute status + profit target for every row ---
                foreach (var row in Followers)
                    RecomputeStatus(row, connectedAccounts);
            }
            catch (Exception ex)
            {
                Log("Error refreshing accounts: " + ex.Message);
            }
        }

        private void RecomputeStatus(FollowerRow row, System.Collections.Generic.List<string> connectedAccounts)
        {
            if (!connectedAccounts.Contains(row.AccountName))
            {
                row.Status = "Disconnected";
                return;
            }

            // Guard: the Leader account must never also be an active
            // Follower - copying the Leader onto itself creates an infinite
            // feedback loop that fires real orders as fast as possible.
            if (row.Enabled && !string.IsNullOrEmpty(SelectedLeader) &&
                string.Equals(row.AccountName, SelectedLeader, StringComparison.OrdinalIgnoreCase))
            {
                row.Enabled = false;
                Log("[Copier] " + row.AccountName + " is the selected Leader; its Follower row was turned off " +
                    "to prevent copying it onto itself.");
            }

            // Sticky profit-target check (only while it hasn't already tripped).
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

        // Quick sizing helper (requirement #5): sets each Follower's Ratio
        // proportionally to its own cash value relative to the Leader's cash
        // value, clamped to [0.1, 2.0] by the Ratio setter itself. The user
        // can still edit any Ratio by hand afterwards - this only provides a
        // starting point.
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
                    row.Ratio = followerCapital / leaderCapital; // setter clamps to [0.1, 2.0]

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

        // Real kill switch for the copier: TradeCopierEngine checks IsRunning
        // before replicating anything from the Leader (see OnLeaderOrderUpdate).
        public void Start()
        {
            IsRunning = true;
            Log("START pressed. Copying is now ACTIVE.");
            RefreshAccounts(); // immediate status refresh instead of waiting for the timer
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
}