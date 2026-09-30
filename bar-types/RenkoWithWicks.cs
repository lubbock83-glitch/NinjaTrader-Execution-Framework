// =============================================================================
//  RenkoWithWicks — custom BarsType for NinjaTrader 8
// =============================================================================
//
//  WHY THIS EXISTS
//  Stock Renko makes backtests structurally optimistic. Because the bar reports
//  only the brick box, a stop or target that was genuinely touched while the
//  brick was forming never appears in the series — so the backtest records a
//  winner on a trade that would have been stopped out in the market. The error
//  is not random; it is biased in favour of the strategy every time.
//
//  This bar type reports the true high and low reached during formation, which
//  is what makes excursion-based analysis (MAE/MFE, stop viability, R-multiple)
//  meaningful on Renko at all.
//
//  RELATIONSHIP TO THE OTHER BAR TYPES IN THIS FOLDER
//  Four Renko implementations are included deliberately: they are successive
//  answers to the same question — how much of the real path should a synthetic
//  bar preserve. This one adds wicks to a fixed brick size. PrecisionWickRenko
//  reworks the state handling for reload safety, ATRMeanRenko makes the brick
//  size volatility-adaptive, and LevelRenko anchors bricks to an absolute price
//  grid. They register overlapping BarsPeriodType ids and are intended to be
//  installed one at a time, not side by side.
//
// =============================================================================

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.BarsTypes
{
    /// <summary>
    /// ============================================================================
    /// RENKO WITH WICKS - Custom BarsType for NinjaTrader 8
    /// ============================================================================
    /// 
    /// PURPOSE:
    /// Standard Renko bars only show brick Open/Close, hiding actual price movement.
    /// This makes backtesting INACCURATE because stops/targets that would have been 
    /// hit during brick formation appear to not have been triggered.
    /// 
    /// This BarsType adds WICKS showing the ACTUAL HIGH and LOW during each brick's
    /// formation period, enabling accurate backtesting.
    /// 
    /// INSTALLATION:
    /// 1. NinjaTrader 8: Tools → NinjaScript Editor
    /// 2. Right-click "BarsTypes" folder → New Bars Type
    /// 3. Delete generated code, paste this file
    /// 4. Press F5 to compile
    /// 5. Restart NinjaTrader 8
    /// 
    /// USAGE:
    /// 1. Open chart → Right-click → Data Series
    /// 2. Type dropdown → "RenkoWithWicks"
    /// 3. Set brick size in ticks
    /// 4. Use Candlestick chart style to see wicks
    /// 
    /// Version: 1.2
    /// ============================================================================
    /// </summary>
    public class RenkoWithWicks : BarsType
    {
        #region Variables
        
        // Brick formation state
        private double brickPriceSize;
        private double brickOpenPrice;
        private double brickHighPrice;
        private double brickLowPrice;
        private double lastCompletedBrickClose;
        
        // Direction tracking
        private int lastBrickDirection;  // 1 = bullish, -1 = bearish, 0 = none
        
        // Session handling
        private bool isFirstTickOfSession;
        private DateTime lastSessionDate;
        
        #endregion

        #region OnStateChange
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "RenkoWithWicks";
                BarsPeriod              = new BarsPeriod { BarsPeriodType = (BarsPeriodType)1001 };
                BuiltFrom               = BarsPeriodType.Tick;
                DaysToLoad              = 5;
                IsIntraday              = true;
                IsTimeBased             = false;
                DefaultChartStyle       = Gui.Chart.ChartStyleType.CandleStick;
                
                ResetState();
            }
            else if (State == State.Configure)
            {
                Properties.Remove(Properties.Find("BaseBarsPeriodType", true));
                Properties.Remove(Properties.Find("BaseBarsPeriodValue", true));
                Properties.Remove(Properties.Find("PointAndFigurePriceType", true));
                Properties.Remove(Properties.Find("ReversalType", true));
                Properties.Remove(Properties.Find("Value2", true));
                
                SetPropertyName("Value", "Brick Size (Ticks)");
            }
        }
        
        private void ResetState()
        {
            brickPriceSize          = 0;
            brickOpenPrice          = 0;
            brickHighPrice          = double.MinValue;
            brickLowPrice           = double.MaxValue;
            lastCompletedBrickClose = 0;
            lastBrickDirection      = 0;
            lastSessionDate         = DateTime.MinValue;
            isFirstTickOfSession    = true;
        }
        
        #endregion

        #region Required Overrides
        
        public override int GetInitialLookBackDays(BarsPeriod barsPeriod, TradingHours tradingHours, int barsBack)
        {
            return Math.Max(5, (int)Math.Ceiling(barsBack / 5.0));
        }
        
        public override void ApplyDefaultValue(BarsPeriod barsPeriod)
        {
            barsPeriod.Value = 10;  // Default 10 ticks
        }
        
        public override void ApplyDefaultBasePeriodValue(BarsPeriod barsPeriod)
        {
            // Not applicable for Renko
        }
        
        public override double GetPercentComplete(Bars bars, DateTime now)
        {
            return 0;  // Renko bars don't have time-based completion
        }
        
        /// <summary>
        /// Returns the label shown on the chart for a specific time
        /// </summary>
        public override string ChartLabel(DateTime time)
        {
            return time.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        }
        
        #endregion

        #region OnDataPoint - Main Bar Building Logic
        
        protected override void OnDataPoint(Bars bars, double open, double high, double low, double close, 
            DateTime time, long volume, bool isBar, double bid, double ask)
        {
            // Calculate brick size in price units
            brickPriceSize = bars.BarsPeriod.Value * bars.Instrument.MasterInstrument.TickSize;
            
            if (brickPriceSize <= 0)
                brickPriceSize = bars.Instrument.MasterInstrument.TickSize;
            
            // Check for new session
            bool newSession = CheckForNewSession(bars, time);
            
            // Handle first tick or new session
            if (isFirstTickOfSession || newSession || bars.Count == 0)
            {
                InitializeFirstBrick(bars, close, time, volume);
                isFirstTickOfSession = false;
                return;
            }
            
            // Get current tick price
            double tickPrice = close;
            
            // Update high/low tracking for wicks - THE KEY FOR ACCURATE BACKTESTING
            if (tickPrice > brickHighPrice) brickHighPrice = tickPrice;
            if (tickPrice < brickLowPrice) brickLowPrice = tickPrice;
            
            // Process tick and check for brick completion
            ProcessIncomingTick(bars, tickPrice, time, volume);
        }
        
        #endregion

        #region Session Handling
        
        private bool CheckForNewSession(Bars bars, DateTime time)
        {
            if (!bars.IsResetOnNewTradingDay)
                return false;
            
            DateTime tickDate = time.Date;
            
            if (tickDate != lastSessionDate)
            {
                lastSessionDate = tickDate;
                return true;
            }
            
            return false;
        }
        
        #endregion

        #region Initialize First Brick
        
        private void InitializeFirstBrick(Bars bars, double price, DateTime time, long volume)
        {
            // Round to nearest brick boundary
            brickOpenPrice = Math.Floor(price / brickPriceSize) * brickPriceSize;
            
            // Initialize high/low tracking
            brickHighPrice = price;
            brickLowPrice = price;
            
            // No direction yet
            lastBrickDirection = 0;
            lastCompletedBrickClose = brickOpenPrice;
            
            // Add initial forming bar
            AddBar(bars, brickOpenPrice, price, price, price, time, volume);
        }
        
        #endregion

        #region Process Incoming Tick
        
        private void ProcessIncomingTick(Bars bars, double tickPrice, DateTime time, long volume)
        {
            // Calculate distance from brick open
            double moveUp = tickPrice - brickOpenPrice;
            double moveDown = brickOpenPrice - tickPrice;
            
            // Count potential bricks
            int bullishBricks = (moveUp >= brickPriceSize) ? (int)Math.Floor(moveUp / brickPriceSize) : 0;
            int bearishBricks = (moveDown >= brickPriceSize) ? (int)Math.Floor(moveDown / brickPriceSize) : 0;
            
            // Reversal requires 2 bricks, continuation requires 1
            int bricksForReversal = 2;
            int bricksForContinuation = 1;
            
            // Check for bullish brick completion
            if (bullishBricks >= bricksForContinuation)
            {
                bool isReversal = (lastBrickDirection == -1);
                
                if (isReversal && bullishBricks < bricksForReversal)
                {
                    UpdateFormingBar(bars, tickPrice, time, volume);
                    return;
                }
                
                // Complete bullish brick(s)
                for (int i = 0; i < bullishBricks; i++)
                {
                    CompleteBullishBrick(bars, time, volume, i == 0);
                }
                
                // Start new forming brick
                StartNewFormingBrick(bars, tickPrice, time, volume);
            }
            // Check for bearish brick completion
            else if (bearishBricks >= bricksForContinuation)
            {
                bool isReversal = (lastBrickDirection == 1);
                
                if (isReversal && bearishBricks < bricksForReversal)
                {
                    UpdateFormingBar(bars, tickPrice, time, volume);
                    return;
                }
                
                // Complete bearish brick(s)
                for (int i = 0; i < bearishBricks; i++)
                {
                    CompleteBearishBrick(bars, time, volume, i == 0);
                }
                
                // Start new forming brick
                StartNewFormingBrick(bars, tickPrice, time, volume);
            }
            else
            {
                // Price hasn't moved enough - update forming bar
                UpdateFormingBar(bars, tickPrice, time, volume);
            }
        }
        
        #endregion

        #region Complete Brick Methods
        
        private void CompleteBullishBrick(Bars bars, DateTime time, long volume, bool isFirstBrick)
        {
            double brickClose = brickOpenPrice + brickPriceSize;
            
            // THE KEY DIFFERENCE: Use ACTUAL high/low for wicks!
            double actualHigh, actualLow;
            
            if (isFirstBrick)
            {
                actualHigh = Math.Max(brickHighPrice, brickClose);
                actualLow = Math.Min(brickLowPrice, brickOpenPrice);
            }
            else
            {
                actualHigh = brickClose;
                actualLow = brickOpenPrice;
            }
            
            // Ensure valid OHLC relationships
            actualHigh = Math.Max(actualHigh, Math.Max(brickOpenPrice, brickClose));
            actualLow = Math.Min(actualLow, Math.Min(brickOpenPrice, brickClose));
            
            // UpdateBar signature: (Bars, high, low, close, time, volume) - NO open parameter
            UpdateBar(bars, actualHigh, actualLow, brickClose, time, volume);
            
            // Update state for next brick
            lastCompletedBrickClose = brickClose;
            lastBrickDirection = 1;
            brickOpenPrice = brickClose;
        }
        
        private void CompleteBearishBrick(Bars bars, DateTime time, long volume, bool isFirstBrick)
        {
            double brickClose = brickOpenPrice - brickPriceSize;
            
            // THE KEY DIFFERENCE: Use ACTUAL high/low for wicks!
            double actualHigh, actualLow;
            
            if (isFirstBrick)
            {
                actualHigh = Math.Max(brickHighPrice, brickOpenPrice);
                actualLow = Math.Min(brickLowPrice, brickClose);
            }
            else
            {
                actualHigh = brickOpenPrice;
                actualLow = brickClose;
            }
            
            // Ensure valid OHLC relationships
            actualHigh = Math.Max(actualHigh, Math.Max(brickOpenPrice, brickClose));
            actualLow = Math.Min(actualLow, Math.Min(brickOpenPrice, brickClose));
            
            // UpdateBar signature: (Bars, high, low, close, time, volume) - NO open parameter
            UpdateBar(bars, actualHigh, actualLow, brickClose, time, volume);
            
            // Update state for next brick
            lastCompletedBrickClose = brickClose;
            lastBrickDirection = -1;
            brickOpenPrice = brickClose;
        }
        
        #endregion

        #region Forming Bar Management
        
        private void UpdateFormingBar(Bars bars, double tickPrice, DateTime time, long volume)
        {
            double formingHigh = Math.Max(brickHighPrice, tickPrice);
            double formingLow = Math.Min(brickLowPrice, tickPrice);
            
            // Ensure valid OHLC
            formingHigh = Math.Max(formingHigh, Math.Max(brickOpenPrice, tickPrice));
            formingLow = Math.Min(formingLow, Math.Min(brickOpenPrice, tickPrice));
            
            // UpdateBar signature: (Bars, high, low, close, time, volume) - NO open parameter
            UpdateBar(bars, formingHigh, formingLow, tickPrice, time, volume);
        }
        
        private void StartNewFormingBrick(Bars bars, double tickPrice, DateTime time, long volume)
        {
            // Reset high/low tracking for new brick
            brickHighPrice = tickPrice;
            brickLowPrice = tickPrice;
            
            // Include open price in high/low
            brickHighPrice = Math.Max(brickHighPrice, brickOpenPrice);
            brickLowPrice = Math.Min(brickLowPrice, brickOpenPrice);
            
            // Add new forming bar
            double newHigh = Math.Max(brickOpenPrice, tickPrice);
            double newLow = Math.Min(brickOpenPrice, tickPrice);
            
            // AddBar signature: (Bars, open, high, low, close, time, volume)
            AddBar(bars, brickOpenPrice, newHigh, newLow, tickPrice, time, volume);
        }
        
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
    }
}
#endregion