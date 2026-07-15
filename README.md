<img width="876" height="492" alt="image" src="https://github.com/user-attachments/assets/f063e2eb-2442-4e9f-b66a-5b6a6f05a28a" />

# NinjaTrader Trade Copier AddOn

This project is a high-performance NinjaScript AddOn designed to replicate trading activity from a single "Leader" account to multiple "Follower" accounts in real-time. It provides a centralized interface within the NinjaTrader Control Center to manage multi-account execution with built-in risk controls and safety guards.

## Overview

The Trade Copier facilitates specialized account management by listening to order and execution events on a primary account and mirroring them across enabled follower accounts. It supports diverse order types including entries, exits, Stop Loss, and Take Profit orders, as well as real-time modifications and cancellations.

## Key Features

* **Leader-Follower Architecture**: Select any connected NinjaTrader account as the Leader and designate any number of other accounts as Followers.
* **Proportional Sizing**: Use custom Ratios to scale order quantities for each follower. A "Calculate Ratios" helper can automatically suggest ratios based on the cash value of the follower relative to the leader.
* **Smart Exit Scaling**: The engine is position-aware. It caps exit order quantities to the follower's actual open position, preventing "overfills" that could accidentally flip a follower into an unintended opposite position.
* **Risk Management**: Individual Profit Targets can be set per follower. Once an account reaches its total P&L target (Realized + Unrealized), the copier automatically pauses activity for that specific account.
* **Safety Guards**:
    * **Feedback Loop Protection**: The system prevents the Leader account from being enabled as its own Follower.
    * **Order Recognition**: Generated orders are prefixed with "[Copy]" to ensure the engine does not treat its own replications as new signals.
    * **Ignored Pre-existing Orders**: Orders already open before the engine starts are ignored to prevent duplication during restarts or recompilations.

## Installation

1. Copy the source files into your NinjaTrader folder:
   `Documents\NinjaTrader 8\bin\Custom\AddOns`
2. Open NinjaTrader 8.
3. If the files are not automatically compiled, go to the Control Center, select New -> NinjaScript Editor, and click Compile (F5).
4. Restart NinjaTrader.

## Usage Instructions

1. **Open the Interface**: Navigate to Control Center > Tools > Trade Copier.
2. **Select Leader**: Choose the account you intend to trade from the "Leader" dropdown menu.
3. **Configure Followers**: 
    * Toggle the "On" checkbox for the accounts you want to receive trades.
    * Set a "Profit Target" ($) if you want the account to stop copying after reaching a specific gain.
    * Set a "Ratio" to determine how many contracts the follower should take relative to the leader (e.g., a ratio of 2.0 will trade 2 contracts for every 1 contract the leader trades).
4. **Recalculate Ratios (Optional)**: Enter a "Target Qty" and click "Calculate Ratios" to set ratios based on account equity balance.
5. **Activate**: Press the "START" button to begin replication. The status indicator will turn green.
6. **Deactivate**: Press "STOP" at any time to pause the copier. Note that stopping the copier will not close existing open positions or cancel working orders already placed on follower accounts.

## Technical Architecture

* **TradeCopier.cs**: Handles the AddOn lifecycle and UI registration within NinjaTrader.
* **TradeCopierCore.cs**: Manages the application state, account refreshing logic, and data binding for the UI.
* **TradeCopierEngine.cs**: Contains the core replication logic. It subscribes to account events and handles the mathematics of ratio scaling and order submission.
* **TradeCopierWindow.cs**: Defines the WPF-based user interface using a dynamic XAML approach compatible with the NinjaScript Editor.

## Technical Notes

* **Account Connection**: Only accounts that are currently connected and active will appear in the Leader or Follower lists.
* **Order States**: The engine monitors transient states (Accepted, Working) to ensure follower orders are placed as quickly as possible once the leader order is validated by the broker.
* **Logging**: Detailed execution logs are provided in the right-hand panel of the window to track order replication, modifications, and any errors encountered during the process.

# Credits

A big thanks to [anaremore](https://github.com/anaremore/) for the inspiration behind this repository. You can find the original project that inspired this work here: [Austins Trade Copier](https://github.com/anaremore/austins-trade-copier "BIG THANKS")
