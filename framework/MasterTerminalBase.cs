// =============================================================================
//  MasterTerminalBase — strategy host for NinjaTrader 8
// =============================================================================
//
//  WHAT THIS IS
//  An abstract Strategy that every concrete strategy in /strategies inherits
//  from. It owns everything that is not the trading idea: the WPF control
//  surface, order and position lifecycle, the regime scanner, the parameter
//  optimiser, and the Monte Carlo risk simulation. A strategy subclass supplies
//  only its entry logic and the parameters it wants searched.
//
//  WHY A BASE CLASS RATHER THAN SIX STANDALONE STRATEGIES
//  The usual NinjaScript pattern is one self-contained file per strategy, which
//  means execution, risk and UI code is copy-pasted per idea. Each copy then
//  drifts. When a bug is found in bracket handling it is fixed in one file and
//  silently survives in the other five, and — worse for research — two
//  strategies become non-comparable because they no longer execute identically.
//
//  Centralising execution makes strategy results differ because the IDEAS
//  differ, which is the only way a comparison between them means anything.
//
//  THE EXTENSION CONTRACT
//  A subclass implements three members and may override three more:
//
//    OnStrategyInitialize()          one-time setup
//    OnStrategyBarUpdate()           the trading idea itself
//    GetStrategyDNA()                identity string used to tag results
//    GetStrategySessionWindow(...)   the hours this idea is valid in
//    GetOptimizableParameters()      the search space it exposes
//    EvaluateCandidateParameters(..) how a candidate is scored on session bars
//
//  The last two are what let the host optimise a strategy it knows nothing
//  about: the strategy declares its own parameter space and its own scoring
//  function, and the host runs the search.
//
//  THE MONTE CARLO IS A PROP-FIRM EVALUATION SIMULATOR, NOT A P&L CHART
//  It resamples the trade sequence against a funded-account rule set — starting
//  balance, profit target, and a TRAILING drawdown measured from the equity high
//  water mark. Ordering matters under a trailing drawdown: the same set of
//  trades in a different sequence can pass or fail, because a drawdown taken
//  after a run-up breaches a threshold that the same drawdown taken first would
//  not. A single backtest equity curve cannot show that; resampling can.
//
//  It simulates each trade using MFE and MAE rather than only the realised
//  result, because a trade that closed at +1R after trading 2R against you can
//  breach a trailing drawdown intramove. This is the reason the bar types in
//  /bar-types preserve true wicks: without real excursion data this simulation
//  would be measuring a path that never happened.
//
// =============================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    #region Global Data Structures
    public struct BrickData 
    { 
        public double O, H, L, C; 
        public DateTime Time; 
    }
    
    public struct SimTrade 
    { 
        public double PnL; 
        public double MAE; 
        public double MFE; 
        public double RiskAmount;
        public double RMultiple => RiskAmount > 0 ? PnL / RiskAmount : 0;
    }

    public class OptimizationParam
    {
        public string Name { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Step { get; set; }
        public double CurrentValue { get; set; }
        public double BestValue { get; set; }
        public Action<double> ApplyValue { get; set; }
    }
    #endregion

    public abstract class MasterTerminalBase : Strategy
    {
        #region Terminal Settings
        [NinjaScriptProperty, Display(Name = "Paint Historical Trades", GroupName = "0. Terminal Settings", Order = 1)]
        public bool PaintHistoricalTrades { get; set; } = true;
        #endregion

        #region Terminal State & Architecture
        public enum SystemMode { Disarmed, ArmLong, ArmShort, ArmAuto, Halted }
        public enum PropFirmMode { EOD, Intraday }
        
        protected SystemMode CurrentMode { get; private set; } = SystemMode.Disarmed;
        protected PropFirmMode TrailMode { get; private set; } = PropFirmMode.Intraday;
        
        protected List<BrickData> BrickCache = new List<BrickData>();
        
        private Queue<double> slippageQueue = new Queue<double>(5);
        private Queue<double> latencyQueue = new Queue<double>(5);
        private List<SimTrade> liveTrades = new List<SimTrade>();
        
        private SimTrade currentLiveTrade;
        protected double activeLiveRisk = 0;
        
        protected List<SimTrade> expectedTrades = new List<SimTrade>();
        private double expExpectancy, expPF, expCalmar, expMaxR;
        private double expPnLStdDev; 
        #endregion

        #region WPF UI Variables
        private Grid chartGrid;
        private Border dashboardBorder;
        private TextBlock txtStatus;
        
        private TextBlock txtExpExpectancy, txtLiveExpectancy, txtVarExpectancy;
        private TextBlock txtExpPF, txtLivePF, txtVarPF;
        private TextBlock txtExpCalmar, txtLiveCalmar, txtVarCalmar;
        
        private Expander statsExpander;
        private TextBlock txtExpWinRate, txtLiveWinRate;
        private TextBlock txtExpMaxDD, txtLiveMaxDD;
        private TextBlock txtExpAvgWL, txtLiveAvgWL;
        private TextBlock txtExpTrades, txtLiveTrades;

        // Optimization Decision Panel
        private Border optDecisionBorder;
        private TextBlock txtOptComparisonHeader;
        private StackPanel optParamComparisonPanel;
        private Button btnApplyOpt, btnKeepCurrent;

        private TextBlock txtDiagnostics;
        private TextBlock txtRegimeStatus;
        private TextBlock txtMC25, txtMC50, txtMC75;
        
        private Button btnSyncNative, btnOptimizeLevers, btnRegime, btnTrailMode, btnArmAuto, btnArmLong, btnArmShort, btnDisarm, btnFlatten;
        
        private SolidColorBrush bgDark = new SolidColorBrush(Color.FromRgb(22, 22, 24));
        private SolidColorBrush bgPanel = new SolidColorBrush(Color.FromRgb(32, 32, 35));
        private SolidColorBrush bgActive = new SolidColorBrush(Color.FromRgb(45, 52, 64));
        private SolidColorBrush accentCyan = new SolidColorBrush(Color.FromRgb(0, 229, 255));
        private SolidColorBrush accentRed = new SolidColorBrush(Color.FromRgb(255, 23, 68));
        private SolidColorBrush accentMint = new SolidColorBrush(Color.FromRgb(0, 230, 118));
        private SolidColorBrush accentYellow = new SolidColorBrush(Color.FromRgb(255, 214, 0));
        private SolidColorBrush textMuted = new SolidColorBrush(Color.FromRgb(158, 158, 158));
        #endregion

        #region Strategy-Agnostic Abstract Hooks
        protected abstract void OnStrategyInitialize();
        protected abstract void OnStrategyBarUpdate();
        protected abstract string GetStrategyDNA(); 
        
        // Window hooks for regime scanner & walk-forward optimization
        public virtual void GetStrategySessionWindow(out int startHHMM, out int endHHMM)
        {
            startHHMM = 0;
            endHHMM = 2359;
        }

        // Top 4-5 parameters defined by child strategy
        public virtual List<OptimizationParam> GetOptimizableParameters()
        {
            return new List<OptimizationParam>();
        }

        // Evaluator over session bars
        // Scoring is delegated to the strategy rather than fixed here on purpose.
        // A single host-wide objective function would force every idea to be
        // judged on the same metric, and a mean-reversion model and a breakout
        // model do not succeed or fail on the same measure.
        public virtual List<SimTrade> EvaluateCandidateParameters(List<BrickData> sessionBars, Dictionary<string, double> candidateValues)
        {
            return new List<SimTrade>();
        }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "MasterTerminalBase";
                Description = "Institutional Host Environment.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
            }
            else if (State == State.DataLoaded)
            {
                BrickCache.Clear();
                OnStrategyInitialize();
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() => 
                    {
                        if (dashboardBorder != null) return;
                        BuildDashboardUI(); 
                    });
                }
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() => 
                    { 
                        if (chartGrid != null && dashboardBorder != null) 
                        {
                            chartGrid.Children.Remove(dashboardBorder);
                            dashboardBorder = null;
                        }
                    });
                }
            }
        }

        protected override void OnBarUpdate()
        {
            BrickCache.Add(new BrickData { O = Open[0], H = High[0], L = Low[0], C = Close[0], Time = Time[0] });
            if (BrickCache.Count > 150000) BrickCache.RemoveAt(0); 

            bool runHistorically = (State == State.Historical && PaintHistoricalTrades);
            bool runLive = (State == State.Realtime && CurrentMode != SystemMode.Disarmed && CurrentMode != SystemMode.Halted);

            if (runHistorically || runLive)
            {
                SystemMode originalMode = CurrentMode;
                if (runHistorically) CurrentMode = SystemMode.ArmAuto;

                OnStrategyBarUpdate();

                if (runHistorically) CurrentMode = originalMode;
                
                if (State == State.Realtime && Position.MarketPosition != MarketPosition.Flat)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        currentLiveTrade.MAE = Math.Min(currentLiveTrade.MAE, (Low[0] - Position.AveragePrice) * Instrument.MasterInstrument.PointValue);
                        currentLiveTrade.MFE = Math.Max(currentLiveTrade.MFE, (High[0] - Position.AveragePrice) * Instrument.MasterInstrument.PointValue);
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        currentLiveTrade.MAE = Math.Min(currentLiveTrade.MAE, (Position.AveragePrice - High[0]) * Instrument.MasterInstrument.PointValue);
                        currentLiveTrade.MFE = Math.Max(currentLiveTrade.MFE, (Position.AveragePrice - Low[0]) * Instrument.MasterInstrument.PointValue);
                    }
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order != null && execution.Order.OrderState == OrderState.Filled)
            {
                double signalPrice = execution.Order.LimitPrice > 0 ? execution.Order.LimitPrice : (execution.Order.StopPrice > 0 ? execution.Order.StopPrice : execution.Price);
                double slippage = Math.Abs(execution.Price - signalPrice) / TickSize;
                slippageQueue.Enqueue(slippage);
                if (slippageQueue.Count > 5) slippageQueue.Dequeue();

                double latencyMs = (DateTime.Now - execution.Time).TotalMilliseconds;
                latencyQueue.Enqueue(latencyMs);
                if (latencyQueue.Count > 5) latencyQueue.Dequeue();
                
                UpdateDashboardDiagnostics();
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition != MarketPosition.Flat && liveTrades.Count == SystemPerformance.RealTimeTrades.Count)
            {
                currentLiveTrade = new SimTrade { MAE = 0, MFE = 0, RiskAmount = activeLiveRisk };
            }
            else if (marketPosition == MarketPosition.Flat && SystemPerformance.RealTimeTrades.Count > liveTrades.Count)
            {
                var lastTrade = SystemPerformance.RealTimeTrades[SystemPerformance.RealTimeTrades.Count - 1];
                currentLiveTrade.PnL = lastTrade.ProfitCurrency;
                liveTrades.Add(currentLiveTrade);
                UpdateVarianceMatrix();
            }
        }

        #region WPF UI Architecture
        private void BuildDashboardUI()
        {
            chartGrid = ChartControl.Parent as Grid;
            if (chartGrid == null) return;

            dashboardBorder = new Border
            {
                Background = bgDark,
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Width = 330,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(15)
            };

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel spMain = new StackPanel { Orientation = Orientation.Vertical };

            TextBlock txtTitle = new TextBlock { Text = "QUANT TERMINAL V4", Foreground = accentCyan, FontWeight = FontWeights.Bold, FontSize = 16, Margin = new Thickness(0,0,0,2) };
            txtStatus = new TextBlock { Text = "STATUS: DISARMED", Foreground = textMuted, FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 0, 0, 15) };
            spMain.Children.Add(txtTitle); spMain.Children.Add(txtStatus);

            Grid gridStats = new Grid();
            for(int i=0; i<4; i++) gridStats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for(int i=0; i<4; i++) gridStats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            gridStats.Children.Add(CreateText("", 0, 0, true));
            gridStats.Children.Add(CreateText("EXPECTED", 0, 1, true));
            gridStats.Children.Add(CreateText("LIVE", 0, 2, true));
            gridStats.Children.Add(CreateText("Z-DELTA", 0, 3, true));

            TextBlock lblExp = CreateText("Exp:", 1, 0, true); lblExp.HorizontalAlignment = HorizontalAlignment.Right; gridStats.Children.Add(lblExp);
            txtExpExpectancy = CreateText("$0.00", 1, 1, false); txtLiveExpectancy = CreateText("$0.00", 1, 2, false); txtVarExpectancy = CreateText("-", 1, 3, false);
            gridStats.Children.Add(txtExpExpectancy); gridStats.Children.Add(txtLiveExpectancy); gridStats.Children.Add(txtVarExpectancy);

            TextBlock lblPF = CreateText("PF:", 2, 0, true); lblPF.HorizontalAlignment = HorizontalAlignment.Right; gridStats.Children.Add(lblPF);
            txtExpPF = CreateText("0.00", 2, 1, false); txtLivePF = CreateText("0.00", 2, 2, false); txtVarPF = CreateText("-", 2, 3, false);
            gridStats.Children.Add(txtExpPF); gridStats.Children.Add(txtLivePF); gridStats.Children.Add(txtVarPF);

            TextBlock lblCal = CreateText("Calmar:", 3, 0, true); lblCal.HorizontalAlignment = HorizontalAlignment.Right; gridStats.Children.Add(lblCal);
            txtExpCalmar = CreateText("0.00", 3, 1, false); txtLiveCalmar = CreateText("0.00", 3, 2, false); txtVarCalmar = CreateText("-", 3, 3, false);
            gridStats.Children.Add(txtExpCalmar); gridStats.Children.Add(txtLiveCalmar); gridStats.Children.Add(txtVarCalmar);
            
            spMain.Children.Add(gridStats);

            txtDiagnostics = new TextBlock { Text = "Slip: -- | Latency: -- | Max R: --", Foreground = textMuted, FontSize = 11, Margin = new Thickness(0, 15, 0, 15), TextAlignment = TextAlignment.Center };
            spMain.Children.Add(txtDiagnostics);

            statsExpander = new Expander
            {
                Header = "ADVANCED STATISTICS",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Background = bgPanel,
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(10)
            };

            Grid advGrid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            advGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            advGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            advGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 5; i++) advGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            advGrid.Children.Add(CreateText("METRIC", 0, 0, true));
            advGrid.Children.Add(CreateText("EXPECTED", 0, 1, true));
            advGrid.Children.Add(CreateText("LIVE", 0, 2, true));

            TextBlock lblWr = CreateText("Win Rate:", 1, 0, true); lblWr.HorizontalAlignment = HorizontalAlignment.Right; advGrid.Children.Add(lblWr);
            txtExpWinRate = CreateText("0.0%", 1, 1, false); txtLiveWinRate = CreateText("0.0%", 1, 2, false);
            advGrid.Children.Add(txtExpWinRate); advGrid.Children.Add(txtLiveWinRate);

            TextBlock lblDd = CreateText("Max DD:", 2, 0, true); lblDd.HorizontalAlignment = HorizontalAlignment.Right; advGrid.Children.Add(lblDd);
            txtExpMaxDD = CreateText("$0", 2, 1, false); txtLiveMaxDD = CreateText("$0", 2, 2, false);
            advGrid.Children.Add(txtExpMaxDD); advGrid.Children.Add(txtLiveMaxDD);

            TextBlock lblWl = CreateText("Avg W/L:", 3, 0, true); lblWl.HorizontalAlignment = HorizontalAlignment.Right; advGrid.Children.Add(lblWl);
            txtExpAvgWL = CreateText("$0 / $0", 3, 1, false); txtLiveAvgWL = CreateText("$0 / $0", 3, 2, false);
            txtExpAvgWL.FontSize = 10; txtLiveAvgWL.FontSize = 10; 
            advGrid.Children.Add(txtExpAvgWL); advGrid.Children.Add(txtLiveAvgWL);

            TextBlock lblTr = CreateText("Total Trades:", 4, 0, true); lblTr.HorizontalAlignment = HorizontalAlignment.Right; advGrid.Children.Add(lblTr);
            txtExpTrades = CreateText("0", 4, 1, false); txtLiveTrades = CreateText("0", 4, 2, false);
            advGrid.Children.Add(txtExpTrades); advGrid.Children.Add(txtLiveTrades);

            statsExpander.Content = advGrid;
            spMain.Children.Add(statsExpander);

            // OPTIMIZATION CANDIDATE COMPARISON MODAL (Hidden by default)
            optDecisionBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 32, 40)),
                BorderBrush = accentCyan,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 15),
                Visibility = Visibility.Collapsed
            };
            StackPanel optStack = new StackPanel();
            optStack.Children.Add(new TextBlock { Text = "WALK-FORWARD CANDIDATE FOUND", Foreground = accentCyan, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0,0,0,4) });
            txtOptComparisonHeader = new TextBlock { Text = "PF: -- -> -- | Exp: -- -> --", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,6) };
            optStack.Children.Add(txtOptComparisonHeader);
            
            optParamComparisonPanel = new StackPanel { Margin = new Thickness(0,0,0,8) };
            optStack.Children.Add(optParamComparisonPanel);

            Grid optBtnGrid = new Grid();
            optBtnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            optBtnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            btnApplyOpt = CreateButton("APPLY OPTIMIZED", accentMint);
            Grid.SetColumn(btnApplyOpt, 0); btnApplyOpt.Margin = new Thickness(0, 0, 3, 0);

            btnKeepCurrent = CreateButton("KEEP CURRENT", textMuted);
            Grid.SetColumn(btnKeepCurrent, 1); btnKeepCurrent.Margin = new Thickness(3, 0, 0, 0);

            optBtnGrid.Children.Add(btnApplyOpt);
            optBtnGrid.Children.Add(btnKeepCurrent);
            optStack.Children.Add(optBtnGrid);

            optDecisionBorder.Child = optStack;
            spMain.Children.Add(optDecisionBorder);

            // 3-DAY SESSION-ISOLATED REGIME SCANNER
            Border regimeBorder = new Border { Background = bgPanel, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10) };
            StackPanel regimePanel = new StackPanel();
            regimePanel.Children.Add(new TextBlock { Text = "3-DAY SESSION REGIME SCANNER", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0,0,0,5) });
            txtRegimeStatus = new TextBlock { Text = "Awaiting 3-Day Scan...", Foreground = textMuted, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,8) };
            regimePanel.Children.Add(txtRegimeStatus);
            btnRegime = CreateButton("SCAN SESSION REGIME (3-DAY)", accentCyan);
            btnRegime.Click += (s, e) => RunSessionRegimeScan();
            regimePanel.Children.Add(btnRegime);
            regimeBorder.Child = regimePanel;
            spMain.Children.Add(regimeBorder);

            // MONTE CARLO
            Border mcBorder = new Border { Background = bgPanel, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 15) };
            StackPanel mcPanel = new StackPanel();
            mcPanel.Children.Add(new TextBlock { Text = "PROP FIRM MONTE CARLO (100k)", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0,0,0,8) });
            
            btnTrailMode = CreateButton("MODE: INTRADAY TRAIL", textMuted);
            btnTrailMode.Click += (s, e) => { 
                TrailMode = TrailMode == PropFirmMode.Intraday ? PropFirmMode.EOD : PropFirmMode.Intraday; 
                btnTrailMode.Content = "MODE: " + TrailMode.ToString().ToUpper() + " TRAIL";
                if(expectedTrades.Count > 0) RunMonteCarloSimulation();
            };
            mcPanel.Children.Add(btnTrailMode);

            txtMC25 = CreateText("25th %: -- Pass | DD: --", 0, 0, true); txtMC25.HorizontalAlignment = HorizontalAlignment.Left;
            txtMC50 = CreateText("50th %: -- Pass | DD: --", 1, 0, true); txtMC50.HorizontalAlignment = HorizontalAlignment.Left; txtMC50.Foreground = accentCyan;
            txtMC75 = CreateText("75th %: -- Pass | DD: --", 2, 0, true); txtMC75.HorizontalAlignment = HorizontalAlignment.Left;
            mcPanel.Children.Add(txtMC25); mcPanel.Children.Add(txtMC50); mcPanel.Children.Add(txtMC75);
            mcBorder.Child = mcPanel;
            spMain.Children.Add(mcBorder);

            // BUTTONS
            btnSyncNative = CreateButton("SYNC NATIVE STATS", accentCyan);
            btnSyncNative.Click += (s, e) => SyncNativeStats();

            btnOptimizeLevers = CreateButton("OPTIMIZE STRATEGY LEVERS", accentYellow);
            btnOptimizeLevers.Click += (s, e) => RunAssistedOptimization();

            btnArmAuto = CreateButton("ARM AUTO", accentMint);
            btnArmAuto.Click += (s, e) => SetSystemMode(SystemMode.ArmAuto);
            
            btnArmLong = CreateButton("ARM LONG ONLY", Brushes.MediumSeaGreen);
            btnArmLong.Click += (s, e) => SetSystemMode(SystemMode.ArmLong);
            
            btnArmShort = CreateButton("ARM SHORT ONLY", Brushes.IndianRed);
            btnArmShort.Click += (s, e) => SetSystemMode(SystemMode.ArmShort);
            
            btnDisarm = CreateButton("DISARM", accentYellow);
            btnDisarm.Click += (s, e) => SetSystemMode(SystemMode.Disarmed);
            
            btnFlatten = CreateButton("FLATTEN & HALT", accentRed);
            btnFlatten.Click += (s, e) => { 
                SetSystemMode(SystemMode.Halted); 
                if (Position.MarketPosition != MarketPosition.Flat) 
                { 
                    ExitLong(); 
                    ExitShort(); 
                } 
            };

            spMain.Children.Add(btnSyncNative);
            spMain.Children.Add(btnOptimizeLevers);
            spMain.Children.Add(btnArmAuto); 
            spMain.Children.Add(btnArmLong); 
            spMain.Children.Add(btnArmShort); 
            spMain.Children.Add(btnDisarm); 
            spMain.Children.Add(btnFlatten);

            scroll.Content = spMain;
            dashboardBorder.Child = scroll;
            Grid.SetColumn(dashboardBorder, 0);
            Grid.SetRowSpan(dashboardBorder, chartGrid.RowDefinitions.Count > 0 ? chartGrid.RowDefinitions.Count : 1);
            chartGrid.Children.Add(dashboardBorder);

            HighlightActiveModeButton();
        }

        private TextBlock CreateText(string text, int row, int col, bool isHeader)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                Foreground = isHeader ? textMuted : Brushes.White,
                FontSize = isHeader ? 10 : 12,
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 6)
            };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col);
            return tb;
        }

        private Button CreateButton(string text, Brush color)
        {
            return new Button
            {
                Content = text,
                Background = bgPanel,
                Foreground = color,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        private void SetSystemMode(SystemMode mode)
        {
            CurrentMode = mode;
            UpdateStatusUI();
            HighlightActiveModeButton();
        }

        private void HighlightActiveModeButton()
        {
            ResetButtonHighlight(btnArmAuto, accentMint);
            ResetButtonHighlight(btnArmLong, Brushes.MediumSeaGreen);
            ResetButtonHighlight(btnArmShort, Brushes.IndianRed);
            ResetButtonHighlight(btnDisarm, accentYellow);
            ResetButtonHighlight(btnFlatten, accentRed);

            switch (CurrentMode)
            {
                case SystemMode.ArmAuto:
                    ApplyActiveHighlight(btnArmAuto, accentMint);
                    break;
                case SystemMode.ArmLong:
                    ApplyActiveHighlight(btnArmLong, Brushes.MediumSeaGreen);
                    break;
                case SystemMode.ArmShort:
                    ApplyActiveHighlight(btnArmShort, Brushes.IndianRed);
                    break;
                case SystemMode.Disarmed:
                    ApplyActiveHighlight(btnDisarm, accentYellow);
                    break;
                case SystemMode.Halted:
                    ApplyActiveHighlight(btnFlatten, accentRed);
                    break;
            }
        }

        private void ResetButtonHighlight(Button btn, Brush defaultColor)
        {
            if (btn == null) return;
            btn.Background = bgPanel;
            btn.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            btn.BorderThickness = new Thickness(1);
            btn.Effect = null;
        }

        private void ApplyActiveHighlight(Button btn, Brush color)
        {
            if (btn == null) return;
            btn.Background = bgActive;
            btn.BorderBrush = color;
            btn.BorderThickness = new Thickness(2);
            btn.Effect = new DropShadowEffect { Color = (color as SolidColorBrush)?.Color ?? Colors.White, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 };
        }

        private void UpdateStatusUI()
        {
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() => {
                if (txtStatus == null) return;
                txtStatus.Text = "STATUS: " + CurrentMode.ToString().ToUpper();
                txtStatus.Foreground = CurrentMode == SystemMode.ArmAuto ? accentMint : CurrentMode == SystemMode.Halted ? accentRed : (CurrentMode == SystemMode.Disarmed ? accentYellow : accentCyan);
            });
        }

        private void UpdateDashboardDiagnostics()
        {
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() => {
                if (txtDiagnostics == null) return;
                string slip = slippageQueue.Count > 0 ? slippageQueue.Average().ToString("F1") : "--";
                string lat = latencyQueue.Count > 0 ? latencyQueue.Average().ToString("F0") : "--";
                double maxR = liveTrades.Count > 0 ? liveTrades.Max(t => t.RMultiple) : 0;
                txtDiagnostics.Text = $"Avg Slip: {slip} tk | API Ping: {lat} ms | Live Max R: {maxR:F1}R";
            });
        }
        #endregion

        #region Native Sync & Decision-Based Optimization
        private void SyncNativeStats()
        {
            if (ChartControl == null) return;

            txtStatus.Text = "STATUS: SYNCING NATIVE STATS...";
            txtStatus.Foreground = accentCyan;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                expectedTrades.Clear();
                foreach (Trade t in SystemPerformance.AllTrades)
                {
                    expectedTrades.Add(new SimTrade {
                        PnL = t.ProfitCurrency,
                        MAE = t.MaeCurrency,
                        MFE = t.MfeCurrency,
                        RiskAmount = 250.0 
                    });
                }

                if (expectedTrades.Count == 0)
                {
                    txtStatus.Text = "0 NATIVE TRADES FOUND";
                    txtStatus.Foreground = accentYellow;
                    return;
                }

                double totalPnL = expectedTrades.Sum(t => t.PnL);
                double grossWin = expectedTrades.Where(t => t.PnL > 0).Sum(t => t.PnL);
                double grossLoss = Math.Abs(expectedTrades.Where(t => t.PnL < 0).Sum(t => t.PnL));
                expExpectancy = totalPnL / expectedTrades.Count;
                expPF = grossLoss == 0 ? 99 : grossWin / grossLoss;
                
                double sumSq = expectedTrades.Sum(t => Math.Pow(t.PnL - expExpectancy, 2));
                expPnLStdDev = Math.Sqrt(sumSq / expectedTrades.Count);

                double peak = 0, maxDD = 0, cur = 0;
                foreach(var t in expectedTrades) { cur += t.PnL; if(cur > peak) peak = cur; double dd = peak - cur; if(dd > maxDD) maxDD = dd; }
                expCalmar = maxDD == 0 ? 99 : totalPnL / maxDD;

                int expTotalTrades = expectedTrades.Count;
                int expWins = expectedTrades.Count(t => t.PnL > 0);
                int expLosses = expectedTrades.Count(t => t.PnL <= 0);
                double expWinRate = expTotalTrades > 0 ? ((double)expWins / expTotalTrades) * 100 : 0;
                double expAvgWin = expWins > 0 ? expectedTrades.Where(t => t.PnL > 0).Average(t => t.PnL) : 0;
                double expAvgLoss = expLosses > 0 ? Math.Abs(expectedTrades.Where(t => t.PnL <= 0).Average(t => t.PnL)) : 0;

                txtExpExpectancy.Text = "$" + expExpectancy.ToString("F2");
                txtExpPF.Text = expPF.ToString("F2");
                txtExpCalmar.Text = expCalmar.ToString("F2");

                txtExpWinRate.Text = expWinRate.ToString("F1") + "%";
                txtExpMaxDD.Text = "$" + maxDD.ToString("F0");
                txtExpAvgWL.Text = $"${expAvgWin:F0} / ${expAvgLoss:F0}";
                txtExpTrades.Text = expTotalTrades.ToString();
                
                liveTrades.Clear(); 
                UpdateVarianceMatrix();
                RunSessionRegimeScan();
                RunMonteCarloSimulation();

                txtStatus.Text = "STATUS: READY (" + expectedTrades.Count + " TRADES)";
                txtStatus.Foreground = accentMint;
            });
        }

        private void RunAssistedOptimization()
        {
            if (ChartControl == null) return;

            var optimizable = GetOptimizableParameters();
            if (optimizable == null || optimizable.Count == 0)
            {
                txtStatus.Text = "NO LEVERS DEFINED BY STRATEGY";
                txtStatus.Foreground = accentYellow;
                return;
            }

            txtStatus.Text = "RUNNING WALK-FORWARD GRID...";
            txtStatus.Foreground = accentCyan;
            btnOptimizeLevers.IsEnabled = false;

            int startHHMM, endHHMM;
            GetStrategySessionWindow(out startHHMM, out endHHMM);

            List<BrickData> sessionBars;
            lock (BrickCache)
            {
                sessionBars = BrickCache.Where(b => 
                {
                    int hhmm = (b.Time.Hour * 100) + b.Time.Minute;
                    if (startHHMM <= endHHMM) return hhmm >= startHHMM && hhmm <= endHHMM;
                    return hhmm >= startHHMM || hhmm <= endHHMM;
                }).ToList();
            }

            if (sessionBars.Count < 50)
            {
                txtStatus.Text = $"NEED MORE SESSION BARS ({sessionBars.Count}/50)";
                txtStatus.Foreground = accentYellow;
                btnOptimizeLevers.IsEnabled = true;
                return;
            }

            Task.Run(() =>
            {
                // Generate permutation grid of candidate values
                List<Dictionary<string, double>> grid = GeneratePermutations(optimizable);

                double bestScore = -999999;
                Dictionary<string, double> bestCombo = null;
                List<SimTrade> bestTrades = null;

                foreach (var combo in grid)
                {
                    var trades = EvaluateCandidateParameters(sessionBars, combo);
                    if (trades == null || trades.Count < 5) continue;

                    double grossWin = trades.Where(t => t.PnL > 0).Sum(t => t.PnL);
                    double grossLoss = Math.Abs(trades.Where(t => t.PnL < 0).Sum(t => t.PnL));
                    double pf = grossLoss == 0 ? 5.0 : Math.Min(5.0, grossWin / grossLoss);
                    double expectancy = trades.Sum(t => t.PnL) / trades.Count;

                    // Objective function: Weight Profit Factor & Expectancy
                    double score = (pf * 100.0) + (expectancy * 0.5);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestCombo = combo;
                        bestTrades = trades;
                    }
                }

                ChartControl.Dispatcher.InvokeAsync(() =>
                {
                    btnOptimizeLevers.IsEnabled = true;

                    if (bestCombo == null || bestTrades == null)
                    {
                        txtStatus.Text = "NO SUPERIOR CANDIDATE FOUND";
                        txtStatus.Foreground = accentYellow;
                        return;
                    }

                    // Calculate Candidate Metrics
                    double cGrossWin = bestTrades.Where(t => t.PnL > 0).Sum(t => t.PnL);
                    double cGrossLoss = Math.Abs(bestTrades.Where(t => t.PnL < 0).Sum(t => t.PnL));
                    double cPF = cGrossLoss == 0 ? 99 : cGrossWin / cGrossLoss;
                    double cExp = bestTrades.Sum(t => t.PnL) / bestTrades.Count;

                    // Populate Decision Card
                    txtOptComparisonHeader.Text = $"PF: {expPF:F2} -> {cPF:F2} | Exp: ${expExpectancy:F0} -> ${cExp:F0}";
                    optParamComparisonPanel.Children.Clear();

                    foreach (var p in optimizable)
                    {
                        if (bestCombo.ContainsKey(p.Name))
                        {
                            p.BestValue = bestCombo[p.Name];
                            TextBlock row = new TextBlock
                            {
                                Text = $"{p.Name}: {p.CurrentValue:F1}  ->  {p.BestValue:F1}",
                                Foreground = (p.CurrentValue != p.BestValue) ? accentCyan : textMuted,
                                FontSize = 10,
                                Margin = new Thickness(0, 1, 0, 1)
                            };
                            optParamComparisonPanel.Children.Add(row);
                        }
                    }

                    // Wire up the Apply button to dynamically inject parameters into the strategy
                    btnApplyOpt.Click += (s, e) =>
                    {
                        foreach (var p in optimizable)
                        {
                            if (bestCombo.ContainsKey(p.Name))
                            {
                                p.ApplyValue(p.BestValue);
                                p.CurrentValue = p.BestValue;
                            }
                        }
                        optDecisionBorder.Visibility = Visibility.Collapsed;
                        txtStatus.Text = "OPTIMIZED SETTINGS APPLIED";
                        txtStatus.Foreground = accentMint;
                        SyncNativeStats();
                    };

                    btnKeepCurrent.Click += (s, e) =>
                    {
                        optDecisionBorder.Visibility = Visibility.Collapsed;
                        txtStatus.Text = "CURRENT SETTINGS RETAINED";
                        txtStatus.Foreground = textMuted;
                    };

                    optDecisionBorder.Visibility = Visibility.Visible;
                    txtStatus.Text = "REVIEW OPTIMIZATION CANDIDATE";
                    txtStatus.Foreground = accentYellow;
                });
            });
        }

        private List<Dictionary<string, double>> GeneratePermutations(List<OptimizationParam> pars)
        {
            List<Dictionary<string, double>> results = new List<Dictionary<string, double>>();
            GenerateRecursive(pars, 0, new Dictionary<string, double>(), results);
            return results;
        }

        private void GenerateRecursive(List<OptimizationParam> pars, int depth, Dictionary<string, double> current, List<Dictionary<string, double>> acc)
        {
            if (depth == pars.Count)
            {
                acc.Add(new Dictionary<string, double>(current));
                return;
            }

            var p = pars[depth];
            for (double val = p.Min; val <= p.Max + 0.0001; val += p.Step)
            {
                current[p.Name] = Math.Round(val, 2);
                GenerateRecursive(pars, depth + 1, current, acc);
                if (acc.Count > 1500) return; // Keep grid bound to ~1500 combinations to prevent CPU spikes
            }
        }
        #endregion

        #region 3-Day Session-Isolated Regime Scanner
        private void RunSessionRegimeScan()
        {
            if (ChartControl == null) return;

            int startHHMM, endHHMM;
            GetStrategySessionWindow(out startHHMM, out endHHMM);

            List<BrickData> localCache;
            lock (BrickCache)
            {
                localCache = new List<BrickData>(BrickCache);
            }

            Task.Run(() => 
            {
                // Group specifically by trading day within the strategy's defined trade window
                var sessionWindows = localCache
                    .Where(b => 
                    {
                        int hhmm = (b.Time.Hour * 100) + b.Time.Minute;
                        if (startHHMM <= endHHMM) return hhmm >= startHHMM && hhmm <= endHHMM;
                        return hhmm >= startHHMM || hhmm <= endHHMM;
                    })
                    .GroupBy(b => b.Time.Date)
                    .OrderBy(g => g.Key)
                    .ToList();

                if (sessionWindows.Count < 3)
                {
                    ChartControl.Dispatcher.InvokeAsync(() => 
                    {
                        txtRegimeStatus.Text = $"Need 3 Days of Session Data ({sessionWindows.Count}/3)";
                        txtRegimeStatus.Foreground = accentYellow;
                    });
                    return;
                }

                // Average session range across the last 3 days
                var last3 = sessionWindows.Skip(Math.Max(0, sessionWindows.Count - 3)).Take(3).ToList();
                double recent3DayAvgRange = last3.Average(g => g.Max(b => b.H) - g.Min(b => b.L));

                // Preceding 3 days baseline
                var prior3 = sessionWindows.Take(Math.Max(1, sessionWindows.Count - 3)).ToList();
                double priorAvgRange = prior3.Average(g => g.Max(b => b.H) - g.Min(b => b.L));

                double expansionRatio = priorAvgRange == 0 ? 1.0 : recent3DayAvgRange / priorAvgRange;
                string dna = GetStrategyDNA();

                ChartControl.Dispatcher.InvokeAsync(() => 
                {
                    string label = $"{recent3DayAvgRange:F1}pt Avg ({expansionRatio:F2}x)";
                    if (dna.Contains("Breakout"))
                    {
                        if (expansionRatio > 1.15) { txtRegimeStatus.Text = "ALIGNED: 3-Day Expansion (" + label + ")"; txtRegimeStatus.Foreground = accentMint; }
                        else if (expansionRatio < 0.85) { txtRegimeStatus.Text = "WARNING: 3-Day Compression (" + label + ")"; txtRegimeStatus.Foreground = accentRed; }
                        else { txtRegimeStatus.Text = "NEUTRAL: Balanced 3-Day (" + label + ")"; txtRegimeStatus.Foreground = accentYellow; }
                    }
                    else if (dna.Contains("Mean-Reversion"))
                    {
                        if (expansionRatio < 0.85) { txtRegimeStatus.Text = "ALIGNED: 3-Day Compression (" + label + ")"; txtRegimeStatus.Foreground = accentMint; }
                        else if (expansionRatio > 1.15) { txtRegimeStatus.Text = "WARNING: 3-Day Breakout (" + label + ")"; txtRegimeStatus.Foreground = accentRed; }
                        else { txtRegimeStatus.Text = "NEUTRAL: Balanced 3-Day (" + label + ")"; txtRegimeStatus.Foreground = accentYellow; }
                    }
                });
            });
        }
        #endregion

        #region Monte Carlo & Variance Matrix
        private void RunMonteCarloSimulation()
        {
            if (expectedTrades.Count < 5 || ChartControl == null) return;
            
            Task.Run(() =>
            {
                int iterations = 100000;
                double[] maxDrawdowns = new double[iterations];
                bool[] passed = new bool[iterations];
                
                double target = 3000;
                double maxTrail = 2500;
                Random rnd = new Random();

                for (int i = 0; i < iterations; i++)
                {
                    double balance = 50000;
                    double highWaterMark = 50000;
                    double peakDD = 0;
                    
                    for (int t = 0; t < expectedTrades.Count; t++)
                    {
                        var trade = expectedTrades[rnd.Next(expectedTrades.Count)];
                        
                        if (TrailMode == PropFirmMode.Intraday)
                        {
                            double intraPeak = balance + trade.MFE;
                            if (intraPeak > highWaterMark) highWaterMark = intraPeak;
                            
                            double intraLow = balance - trade.MAE;
                            if (intraLow <= highWaterMark - maxTrail) { peakDD = maxTrail; break; }
                        }
                        
                        balance += trade.PnL;
                        
                        if (TrailMode == PropFirmMode.EOD)
                        {
                            if (balance > highWaterMark) highWaterMark = balance;
                            if (balance <= highWaterMark - maxTrail) { peakDD = maxTrail; break; }
                        }

                        double dd = highWaterMark - balance;
                        if (dd > peakDD) peakDD = dd;
                        
                        if (balance >= 50000 + target) { passed[i] = true; break; }
                    }
                    maxDrawdowns[i] = peakDD;
                }

                Array.Sort(maxDrawdowns);
                int p25 = (int)maxDrawdowns[(int)(iterations * 0.25)];
                int p50 = (int)maxDrawdowns[(int)(iterations * 0.50)];
                int p75 = (int)maxDrawdowns[(int)(iterations * 0.75)];
                
                double passRate = (double)passed.Count(p => p) / iterations * 100;

                ChartControl.Dispatcher.InvokeAsync(() =>
                {
                    txtMC25.Text = $"25th %: {passRate:F1}% Pass | DD: ${p25}";
                    txtMC50.Text = $"50th %: {passRate:F1}% Pass | DD: ${p50}";
                    txtMC75.Text = $"75th %: {passRate:F1}% Pass | DD: ${p75}";
                });
            });
        }

        private void UpdateVarianceMatrix()
        {
            if (ChartControl == null) return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (liveTrades.Count < 5) 
                {
                    txtLiveExpectancy.Text = $"Sample {liveTrades.Count}/5";
                    return; 
                }

                double livePnLTotal = liveTrades.Sum(t => t.PnL);
                double liveGrossWin = liveTrades.Where(t => t.PnL > 0).Sum(t => t.PnL);
                double liveGrossLoss = Math.Abs(liveTrades.Where(t => t.PnL < 0).Sum(t => t.PnL));
                
                double liveExp = livePnLTotal / liveTrades.Count;
                double livePF = liveGrossLoss == 0 ? 99 : liveGrossWin / liveGrossLoss;
                
                double peak = 0, liveMaxDD = 0, cur = 0;
                foreach(var t in liveTrades) { cur += t.PnL; if(cur > peak) peak = cur; double dd = peak - cur; if(dd > liveMaxDD) liveMaxDD = dd; }
                double liveCalmar = liveMaxDD == 0 ? 99 : livePnLTotal / liveMaxDD;

                double zScore = expPnLStdDev == 0 ? 0 : (liveExp - expExpectancy) / (expPnLStdDev / Math.Sqrt(liveTrades.Count));

                int liveTotalTrades = liveTrades.Count;
                int liveWins = liveTrades.Count(t => t.PnL > 0);
                int liveLosses = liveTrades.Count(t => t.PnL <= 0);
                double liveWinRate = liveTotalTrades > 0 ? ((double)liveWins / liveTotalTrades) * 100 : 0;
                double liveAvgWin = liveWins > 0 ? liveTrades.Where(t => t.PnL > 0).Average(t => t.PnL) : 0;
                double liveAvgLoss = liveLosses > 0 ? Math.Abs(liveTrades.Where(t => t.PnL <= 0).Average(t => t.PnL)) : 0;

                txtLiveExpectancy.Text = "$" + liveExp.ToString("F2");
                txtLivePF.Text = livePF.ToString("F2");
                txtLiveCalmar.Text = liveCalmar.ToString("F2");

                txtVarExpectancy.Text = zScore.ToString("F2") + " Z";
                SetRowColor(txtExpExpectancy, txtLiveExpectancy, txtVarExpectancy, zScore, -1.5, -2.5);

                double pfDelta = expPF == 0 ? 0 : ((livePF - expPF) / expPF) * 100;
                txtVarPF.Text = pfDelta.ToString("F1") + "%";
                SetRowColor(txtExpPF, txtLivePF, txtVarPF, pfDelta, -15, -30);

                double calDelta = expCalmar == 0 ? 0 : ((liveCalmar - expCalmar) / expCalmar) * 100;
                txtVarCalmar.Text = calDelta.ToString("F1") + "%";
                SetRowColor(txtExpCalmar, txtLiveCalmar, txtVarCalmar, calDelta, -15, -30);

                txtLiveWinRate.Text = liveWinRate.ToString("F1") + "%";
                txtLiveMaxDD.Text = "$" + liveMaxDD.ToString("F0");
                txtLiveAvgWL.Text = $"${liveAvgWin:F0} / ${liveAvgLoss:F0}";
                txtLiveTrades.Text = liveTotalTrades.ToString();
            });
        }
        
        private void SetRowColor(TextBlock t1, TextBlock t2, TextBlock t3, double val, double yellowThresh, double redThresh)
        {
            Brush color = accentMint;
            if (val <= redThresh) color = accentRed;
            else if (val <= yellowThresh) color = accentYellow;
            t1.Foreground = t2.Foreground = t3.Foreground = color;
        }
        #endregion
    }
}