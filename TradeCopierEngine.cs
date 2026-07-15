#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using NinjaTrader.Cbi;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    // ---------------------------------------------------------------
    // TradeCopierEngine
    //
    // Single responsibility: listen to everything the Leader account
    // does and replicate it on the enabled Follower accounts.
    //
    // It copies:
    //   - Buy entries
    //   - Sell Short entries
    //   - Exits
    //   - Stop Loss orders
    //   - Take Profit orders
    //   - SL/TP modifications
    //   - Order cancellations
    //
    // Sizing is the only "business" decision it makes, and only because
    // the user asked for it: every quantity sent to a Follower is the
    // Leader's quantity scaled by that Follower's Ratio (rounded to the
    // nearest whole contract). Exits are additionally capped to whatever
    // the Follower actually has open, so a mismatch can never flip a
    // Follower into a new, unintended position. Followers whose Profit
    // Target has been reached (TradeCopierCore.RecomputeStatus) are
    // excluded from copying entirely until re-enabled.
    // ---------------------------------------------------------------
    public class TradeCopierEngine : IDisposable
    {
        private readonly TradeCopierCore core;
        private readonly object syncRoot = new object();

        private Account leaderAccount;

        // Leader Order -> (FollowerAccountName -> Follower Order)
        // NOTE: Order objects are used as dictionary keys directly (not
        // Order.OrderId, which the NinjaTrader docs explicitly say is
        // NOT stable/unique over an order's lifetime). Comparing/keying
        // on the Order object itself is the pattern NinjaTrader's own
        // documentation recommends.
        private readonly Dictionary<Order, Dictionary<string, Order>> replicaMap =
            new Dictionary<Order, Dictionary<string, Order>>();

        // Last known Quantity/LimitPrice/StopPrice seen for a Leader order,
        // used to detect modifications (SL/TP moved, quantity changed, etc).
        private readonly Dictionary<Order, (int Quantity, double LimitPrice, double StopPrice)> lastKnownState =
            new Dictionary<Order, (int, double, double)>();

        // Orders that were already open on the Leader account BEFORE this
        // engine attached (e.g. because the AddOn was just recompiled from
        // the NinjaScript Editor while positions were still open). These
        // must never be copied: their own resting state updates would
        // otherwise look like "brand new" orders to a fresh engine instance
        // and get duplicated onto the followers.
        private readonly HashSet<Order> ignoredOrders = new HashSet<Order>();

        // Prefix used to name every order this engine creates on a Follower
        // account (see ReplicateNewOrder). Used as a second, independent
        // guard against feedback loops: if an order carrying this prefix
        // ever shows up as a "Leader" event (e.g. because of a
        // misconfiguration where the Leader account is also a Follower),
        // it is recognized and ignored instead of being copied again.
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

        // -------------------------------------------------------
        // Leader subscription
        // -------------------------------------------------------
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

            // Snapshot whatever is already open/working on the Leader right
            // now and mark it as "ignored". Only orders that arrive AFTER
            // this point are treated as new and copied to the followers.
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

        // -------------------------------------------------------
        // Follower resolution
        // -------------------------------------------------------
        // NOTE: this assumes Followers is only mutated from the UI thread
        // (see the DispatcherTimer in TradeCopierCore.Initialize).
        private List<Account> GetFollowerAccounts()
        {
            return core.Followers
                .Where(f => f.Enabled && !f.TargetReachedFlag)
                // CRITICAL: never copy the Leader account onto itself. If the
                // Leader is also left "On" in the Followers table, copying to
                // it would fire another OrderUpdate on the very account we're
                // subscribed to as Leader, which would look like a brand new
                // Leader order and get copied again - an infinite feedback
                // loop that fires real orders as fast as the broker allows.
                .Where(f => leaderAccount == null ||
                            !string.Equals(f.AccountName, leaderAccount.Name, StringComparison.OrdinalIgnoreCase))
                .Select(f => Account.All.FirstOrDefault(a => a.Name == f.AccountName))
                .Where(a => a != null && a.Connection != null && a.Connection.Status == ConnectionStatus.Connected)
                .ToList();
        }

        // Looks up the FollowerRow (Ratio, Profit Target, etc) for a given
        // account name. Returns null if the account isn't in the table
        // (shouldn't normally happen, since GetFollowerAccounts sources from
        // the same table).
        private FollowerRow GetFollowerRow(string accountName) =>
            core.Followers.FirstOrDefault(f => f.AccountName == accountName);

        // -------------------------------------------------------
        // Leader order events
        // -------------------------------------------------------
        private void OnLeaderOrderUpdate(object sender, OrderEventArgs e)
        {
            var leaderOrder = e.Order;
            if (leaderOrder == null)
                return;

            // Second, independent feedback-loop guard: never treat one of
            // our own generated orders as a new signal to copy.
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

            // The user's explicit kill switch: while stopped, the engine keeps
            // listening (so it doesn't lose track of anything) but performs
            // NO action on the followers at all. This is what actually makes
            // the STOP button stop the copying, instead of just logging it.
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
                            // Fully filled: nothing left to modify/cancel for this order.
                            // Follower orders fill on their own; any OCO sibling (SL/TP)
                            // is cancelled independently on each follower account because
                            // they share their own (per-follower) OCO id.
                            replicaMap.Remove(leaderOrder);
                            lastKnownState.Remove(leaderOrder);
                        }
                        break;

                        // Initialized, Submitted, TriggerPending, ChangePending,
                        // ChangeSubmitted, CancelPending, CancelSubmitted, Unknown:
                        // transient states, nothing to do yet.
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

        // -------------------------------------------------------
        // Replication
        // -------------------------------------------------------
        private void ReplicateNewOrder(Order leaderOrder)
        {
            var followerAccounts = GetFollowerAccounts();

            if (followerAccounts.Count == 0)
            {
                core.Log("[Copier] New leader order (" + DescribeOrder(leaderOrder) +
                          ") but there are no enabled/connected followers.");
                return;
            }

            var followerOrders = new Dictionary<string, Order>();
            bool isExit = IsExitAction(leaderOrder.OrderAction);

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

                // Exits (plain exits AND SL/TP orders, since both use Sell/
                // BuyToCover) must never send more than what the follower
                // actually has open. Blindly copying the ratio-scaled quantity
                // here is what causes a BuyToCover/Sell to "overfill" and
                // flip into a brand-new opposite position on the follower
                // (and the margin-rejection errors that come with it).
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

                // Keep the OCO grouping (entry/SL/TP) intact, but scope it to
                // each follower account so brackets resolve independently.
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

        // -------------------------------------------------------
        // Helpers
        // -------------------------------------------------------
        private static bool IsTerminal(OrderState state) =>
            state == OrderState.Filled || state == OrderState.Cancelled || state == OrderState.Rejected;

        // Scales a leader quantity by a follower's Ratio, rounding to the
        // nearest whole contract/lot. A ratio can legitimately round a small
        // quantity down to 0 (e.g. ratio 0.1 on a 1-lot order) - callers must
        // check for that and skip rather than force a minimum size, since
        // forcing a size would silently override the ratio the user set.
        private static int ApplyRatio(int leaderQuantity, double ratio) =>
            (int)Math.Round(leaderQuantity * ratio, MidpointRounding.AwayFromZero);

        // An "exit" here means any order whose action reduces/closes an
        // existing position: Sell (closes a long) or BuyToCover (closes a
        // short). This includes plain manual exits AND SL/TP orders, since
        // both use these same two OrderAction values.
        private static bool IsExitAction(OrderAction action) =>
            action == OrderAction.Sell || action == OrderAction.BuyToCover;

        // Reads the follower's current signed position for the given
        // instrument directly from the account (not from anything the
        // Leader reports), so exits are always sized against reality.
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

        // Human-readable description used only for the Log panel; it does
        // not drive any copying decision (the copier itself treats every
        // order the same way regardless of what it "means").
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
}