#region Using declarations
using System;
using System.Windows;
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    // ---------------------------------------------------------------
    // Entry point of the AddOn. Responsible for:
    //  - Creating the Core (state/logic).
    //  - Registering the menu entry under Control Center > Tools.
    //  - Opening/focusing the window (TradeCopierWindow).
    //
    // Contains no business logic: for now this AddOn is only a
    // reference interface, with no real copy-trading functionality.
    // ---------------------------------------------------------------
    public class TradeCopier : NinjaTrader.NinjaScript.AddOnBase
    {
        private TradeCopierCore core;
        private TradeCopierWindow window;
        private NTMenuItem toolsMenu;
        private NTMenuItem copierItem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TradeCopier";
            }
            else if (State == State.Configure)
            {
                core = new TradeCopierCore();
                core.Initialize();
            }
            else if (State == State.Terminated)
            {
                core?.Terminate();
                RemoveMenu();
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            if (window is NinjaTrader.Gui.ControlCenter cc)
                AddMenu(cc);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (window is NinjaTrader.Gui.ControlCenter)
                RemoveMenu();
        }

        private void AddMenu(NinjaTrader.Gui.ControlCenter cc)
        {
            if (copierItem != null) return;

            toolsMenu = cc.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;
            if (toolsMenu == null) return;

            copierItem = new NTMenuItem
            {
                Header = "Trade Copier",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            copierItem.Click += (s, e) =>
            {
                if (window == null || !window.IsVisible)
                {
                    window = new TradeCopierWindow(core);
                    window.Show();
                }
                else
                {
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    window.Focus();
                }
            };

            toolsMenu.Items.Add(copierItem);
        }

        private void RemoveMenu()
        {
            if (toolsMenu != null && copierItem != null)
                toolsMenu.Items.Remove(copierItem);
            copierItem = null;
        }
    }
}