#region Using declarations
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    // ---------------------------------------------------------------
    // View of the AddOn. The whole UI is defined as a XAML string and
    // parsed at runtime with XamlReader.Parse. This is necessary
    // because this project is compiled only from the NinjaScript
    // Editor, which does not support compiled .xaml files (BAML) the
    // way a regular Visual Studio project would.
    //
    // Layout: two columns. Left (wider): Leader row (with the sizing
    // helper on its far right) + Followers table. Right (narrower):
    // program status indicator, Start/Stop, and the Log.
    //
    // Logic and state live in TradeCopierCore.cs; this file is only
    // responsible for building the interface and wiring the events.
    // ---------------------------------------------------------------
    public partial class TradeCopierWindow : NTWindow
    {
        private readonly TradeCopierCore core;
        private TextBox LogBox;

        public TradeCopierWindow(TradeCopierCore coreInstance)
        {
            core = coreInstance;
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
        <!-- Colored status dot used both for the top program status and
             for each row's Status cell in the Followers table. -->
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

    <!-- ============================= LEFT COLUMN ============================= -->
    <Grid Grid.Column=""0"" Margin=""10,10,6,10"">
        <Grid.RowDefinitions>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""*""/>
        </Grid.RowDefinitions>

        <!-- Leader row: combo on the left, sizing helper on the far right -->
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

        <!-- Followers label -->
        <TextBlock Grid.Row=""1"" Text=""Followers"" Foreground=""White"" FontWeight=""Bold"" Margin=""0,0,0,4""/>

        <!-- Followers table -->
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

    <!-- ============================= RIGHT COLUMN ============================= -->
    <Grid Grid.Column=""1"" Margin=""6,10,10,10"">
        <Grid.RowDefinitions>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""Auto""/>
            <RowDefinition Height=""*""/>
        </Grid.RowDefinitions>

        <!-- Program status indicator -->
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

        <!-- Start / Stop -->
        <StackPanel Grid.Row=""1"" Orientation=""Horizontal"" Margin=""0,0,0,10"">
            <Button x:Name=""StartBtn"" Content=""START"" Width=""90"" Padding=""6""
                    Background=""#2E7D32"" Foreground=""White"" FontWeight=""Bold""/>
            <Button x:Name=""StopBtn"" Content=""STOP"" Width=""90"" Padding=""6"" Margin=""8,0,0,0""
                    Background=""#C62828"" Foreground=""White"" FontWeight=""Bold""/>
        </StackPanel>

        <!-- Log -->
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

            core.Log("UI loaded.");
        }
    }
}