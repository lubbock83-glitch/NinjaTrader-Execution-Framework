// =============================================================================
//  FailedAuctionModelV1 - auction failure reversal
// =============================================================================
//
//  THE IDEA
//  An auction that probes beyond value and fails to find acceptance tends to
//  return through it. The model hunts for that failure within a bounded window
//  after the probe, rather than reacting to the probe itself.
//
//  DESIGN DECISIONS AND WHY
//
//  * The hunting window is bounded in bars. A failed auction that resolves
//    twenty bars later is a different event from one that resolves in three,
//    and without the bound the model keeps finding "confirmation" long after
//    the context that justified the trade has gone.
//
//  * Take profit is an R-multiple rather than a point target, so reward is
//    defined relative to the risk actually taken on that trade instead of a
//    fixed distance that means different things at different volatilities. The
//    optional live-POC target overrides it, because when a real structural
//    magnet exists it is a better exit than an arithmetic one.
//
//  * The NY gravity filter suppresses entries too close to the prior session
//    reference. Price tends to be pinned near that level, and a reversal signal
//    taken inside the pin is mostly noise.
//
//  * GetOptimizableParameters() exposes exactly four: stop distance, target
//    multiple, hunting window, gravity distance. The restraint is deliberate.
//    Every parameter added to a search multiplies the space and the chance of
//    fitting the sample rather than the market.
//
//  HOST CONTRACT
//  This strategy derives from MasterTerminalBase and implements only its idea.
//  Order handling, risk, the control surface, optimisation and reporting all
//  live in the host. GetStrategyDNA() declares which family this idea belongs
//  to, so the regime scanner can tell whether current conditions suit it.
// =============================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
using System.Windows.Media;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum CatalystLogicMode 
    { 
        Exclusive, 
        Inclusive  
    }

    public class FailedAuctionModelV1 : MasterTerminalBase
    {
        #region Strategy Specific Optimization Levers
        [NinjaScriptProperty, Range(5, 50), Display(Name = "Static Stop Loss (Points)", GroupName = "1. Risk Management", Order = 1)]
        public double StopLossPoints { get; set; } = 20.0;

        [NinjaScriptProperty, Range(1.0, 5.0), Display(Name = "Take Profit (R-Multiplier)", GroupName = "1. Risk Management", Order = 2)]
        public double TargetRMultiple { get; set; } = 1.5;

        [NinjaScriptProperty, Display(Name = "Target Live POC (Overrides R-Mult)", GroupName = "1. Risk Management", Order = 3)]
        public bool TargetPOC { get; set; } = false;

        [NinjaScriptProperty, Display(Name = "Catalyst Logic Mode", GroupName = "2. Entry Logic", Order = 1)]
        public CatalystLogicMode CatalystMode { get; set; } = CatalystLogicMode.Inclusive;

        [NinjaScriptProperty, Range(3, 30), Display(Name = "Hunting Window (Bars)", GroupName = "2. Entry Logic", Order = 2)]
        public int HuntingWindowBars { get; set; } = 10;

        [NinjaScriptProperty, Range(5, 30), Display(Name = "NY Gravity Filter (Points)", GroupName = "3. Institutional Filters", Order = 1)]
        public double NYGravityDistance { get; set; } = 10.0;
        #endregion

        #region Hardcoded Institutional Specifics
        private const double ZoneBufferPoints = 2.0;       
        private const double POCBufferPoints = 1.0;        
        private const int ProfileBuildBufferMins = 60;     
        private const int WickLookback = 5;                
        private const int RequiredWicks = 3;               
        private const double WickPercentage = 0.30;        
        #endregion

        #region Live State Variables
        private Dictionary<double, int> nyProfile = new Dictionary<double, int>();
        private Dictionary<double, int> liveProfile = new Dictionary<double, int>();
        
        private double nyPOC, nyVAH, nyVAL;
        private double livePOC, liveVAH, liveVAL;
        
        private int setupState = 0; 
        private bool isLongSetup = false;
        private double pendingEntryPrice = 0;
        private double pendingStopPrice = 0;
        private double pendingTargetPrice = 0;
        
        private double fvgHigh = 0, fvgLow = 0;
        private double extremePivotPrice = 0;
        private int huntingBarsElapsed = 0;
        private Order entryOrder = null;
        
        private DateTime lastSessionDate;
        #endregion

        protected override void OnStateChange()
        {
            base.OnStateChange();
            if (State == State.SetDefaults)
            {
                Name = "Failed Auction Model (FAM)";
            }
        }

        protected override void OnStrategyInitialize()
        {
            // Empty hook for FAM specific initializations
        }
        
        protected override string GetStrategyDNA()
        {
            return "Mean-Reversion"; 
        }

        #region Master Terminal Agnostic Contracts
        public override void GetStrategySessionWindow(out int startHHMM, out int endHHMM)
        {
            // FAM targets the Globex/Asian overnight session for fading
            startHHMM = 1800; // 6:00 PM
            endHHMM = 930;    // 9:30 AM (NY Open)
        }

        public override List<OptimizationParam> GetOptimizableParameters()
        {
            return new List<OptimizationParam>
            {
                new OptimizationParam { 
                    Name = "StopLossPoints", 
                    Min = 10.0, Max = 30.0, Step = 2.5, 
                    CurrentValue = StopLossPoints, 
                    ApplyValue = (v) => StopLossPoints = v 
                },
                new OptimizationParam { 
                    Name = "TargetRMultiple", 
                    Min = 1.0, Max = 4.0, Step = 0.5, 
                    CurrentValue = TargetRMultiple, 
                    ApplyValue = (v) => TargetRMultiple = v 
                },
                new OptimizationParam { 
                    Name = "HuntingWindowBars", 
                    Min = 5, Max = 20, Step = 5, 
                    CurrentValue = HuntingWindowBars, 
                    ApplyValue = (v) => HuntingWindowBars = (int)v 
                },
                new OptimizationParam { 
                    Name = "NYGravityDistance", 
                    Min = 5.0, Max = 20.0, Step = 5.0, 
                    CurrentValue = NYGravityDistance, 
                    ApplyValue = (v) => NYGravityDistance = v 
                }
            };
        }

        public override List<SimTrade> EvaluateCandidateParameters(List<BrickData> sessionBars, Dictionary<string, double> candidateValues)
        {
            // Streamlined evaluator to prevent TPO building from crashing the WPF grid
            List<SimTrade> results = new List<SimTrade>();
            double stopPts = candidateValues.ContainsKey("StopLossPoints") ? candidateValues["StopLossPoints"] : StopLossPoints;
            double rMult = candidateValues.ContainsKey("TargetRMultiple") ? candidateValues["TargetRMultiple"] : TargetRMultiple;
            int huntBars = candidateValues.ContainsKey("HuntingWindowBars") ? (int)candidateValues["HuntingWindowBars"] : HuntingWindowBars;

            if (sessionBars.Count < 50) return results;

            int pos = 0; 
            double ePrice = 0, sPrice = 0, tPrice = 0;
            double ptVal = Instrument.MasterInstrument.PointValue;

            for (int i = 10; i < sessionBars.Count; i++)
            {
                if (pos == 0)
                {
                    // Simulated rough FVG trigger for background math grid speed
                    if (sessionBars[i-2].H < sessionBars[i].L && sessionBars[i].C < sessionBars[i].O) 
                    {
                        pos = 1; 
                        ePrice = sessionBars[i].C; 
                        sPrice = ePrice - stopPts; 
                        tPrice = ePrice + (stopPts * rMult);
                    }
                    else if (sessionBars[i-2].L > sessionBars[i].H && sessionBars[i].C > sessionBars[i].O)
                    {
                        pos = -1; 
                        ePrice = sessionBars[i].C; 
                        sPrice = ePrice + stopPts; 
                        tPrice = ePrice - (stopPts * rMult);
                    }
                }
                else
                {
                    if (pos == 1)
                    {
                        if (sessionBars[i].L <= sPrice) { results.Add(new SimTrade { PnL = (sPrice - ePrice)*ptVal, RiskAmount = (ePrice-sPrice)*ptVal }); pos = 0; }
                        else if (sessionBars[i].H >= tPrice) { results.Add(new SimTrade { PnL = (tPrice - ePrice)*ptVal, RiskAmount = (ePrice-sPrice)*ptVal }); pos = 0; }
                    }
                    else if (pos == -1)
                    {
                        if (sessionBars[i].H >= sPrice) { results.Add(new SimTrade { PnL = (ePrice - sPrice)*ptVal, RiskAmount = (sPrice-ePrice)*ptVal }); pos = 0; }
                        else if (sessionBars[i].L <= tPrice) { results.Add(new SimTrade { PnL = (ePrice - tPrice)*ptVal, RiskAmount = (sPrice-ePrice)*ptVal }); pos = 0; }
                    }
                }
            }
            return results;
        }
        #endregion

        protected override void OnStrategyBarUpdate()
        {
            if (CurrentBar < 10) return;

            UpdateProfilesAndZones(Time[0], High[0], Low[0]);
            
            if (nyPOC > 0) Draw.HorizontalLine(this, "NY_POC_Line", nyPOC, Brushes.Magenta, DashStyleHelper.Dash, 2);
            if (liveVAH > 0) Draw.HorizontalLine(this, "Live_VAH_Line", liveVAH, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            if (liveVAL > 0) Draw.HorizontalLine(this, "Live_VAL_Line", liveVAL, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            if (livePOC > 0) Draw.HorizontalLine(this, "Live_POC_Line", livePOC, Brushes.Gold, DashStyleHelper.Dot, 2);

            bool isGlobexOrAsia = Time[0].Hour >= 18 || Time[0].Hour < 9 || (Time[0].Hour == 9 && Time[0].Minute < 30);
            bool profileMature = Time[0].Hour >= 19 || Time[0].Hour < 9;

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (CurrentMode == SystemMode.ArmAuto || CurrentMode == SystemMode.ArmLong || CurrentMode == SystemMode.ArmShort)
                {
                    bool blockedByGravity = Math.Abs(Close[0] - nyPOC) <= NYGravityDistance;

                    if (isGlobexOrAsia && profileMature && !blockedByGravity)
                    {
                        ScanForFailedAuction();
                    }
                    else
                    {
                        ResetSetup(); 
                    }
                }
            }
            else
            {
                ManageOpenTrade();
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order.Name == "FAM_Long" || order.Name == "FAM_Short")
                entryOrder = order;
        }

        #region Core FAM Logic (Scanning & Execution)
        private void ScanForFailedAuction()
        {
            if (setupState == 0)
            {
                if (Close[0] < liveVAL + ZoneBufferPoints && CurrentMode != SystemMode.ArmShort)
                {
                    if (CheckWickDensity(true)) { setupState = 1; isLongSetup = true; }
                }
                else if (Close[0] > liveVAH - ZoneBufferPoints && CurrentMode != SystemMode.ArmLong)
                {
                    if (CheckWickDensity(false)) { setupState = 1; isLongSetup = false; }
                }
            }
            else if (setupState == 1)
            {
                if (isLongSetup)
                {
                    if (Low[1] < Low[2] && Low[1] < Low[0]) 
                    {
                        setupState = 2;
                        extremePivotPrice = Low[1];
                        FindMostRecentFVG(true);
                    }
                    else if (Close[0] > liveVAL + ZoneBufferPoints) ResetSetup(); 
                }
                else
                {
                    if (High[1] > High[2] && High[1] > High[0]) 
                    {
                        setupState = 2;
                        extremePivotPrice = High[1];
                        FindMostRecentFVG(false);
                    }
                    else if (Close[0] < liveVAH - ZoneBufferPoints) ResetSetup(); 
                }
            }
            else if (setupState == 2)
            {
                if (fvgHigh == 0 || fvgLow == 0) { ResetSetup(); return; }

                bool gapClosed = isLongSetup ? Close[0] > fvgHigh : Close[0] < fvgLow;
                
                if (gapClosed)
                {
                    bool isCat = CheckEngulfing(isLongSetup, Close[0], Open[0], Close[1], Open[1]);
                    
                    if (CatalystMode == CatalystLogicMode.Exclusive)
                    {
                        if (isCat) ArmPendingOrder(isLongSetup);
                        else ResetSetup(); 
                    }
                    else 
                    {
                        if (isCat) ArmPendingOrder(isLongSetup);
                        else
                        {
                            setupState = 3;
                            huntingBarsElapsed = 0;
                        }
                    }
                }
                else
                {
                    if (isLongSetup && Close[0] < extremePivotPrice) ResetSetup();
                    if (!isLongSetup && Close[0] > extremePivotPrice) ResetSetup();
                }
            }
            else if (setupState == 3)
            {
                huntingBarsElapsed++;
                
                if (huntingBarsElapsed > HuntingWindowBars || (isLongSetup && Close[0] < extremePivotPrice) || (!isLongSetup && Close[0] > extremePivotPrice))
                {
                    ResetSetup(); 
                    return;
                }

                if (CheckEngulfing(isLongSetup, Close[0], Open[0], Close[1], Open[1]))
                {
                    ArmPendingOrder(isLongSetup);
                }
            }
            else if (setupState == 4)
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    if (entryOrder != null && entryOrder.OrderState == OrderState.Working)
                        CancelOrder(entryOrder);

                    if (CatalystMode == CatalystLogicMode.Exclusive)
                    {
                        ResetSetup();
                    }
                    else 
                    {
                        setupState = 3;
                    }
                }
            }
        }

        private void ArmPendingOrder(bool isLong)
        {
            double tickVal = TickSize;
            if (isLong)
            {
                pendingEntryPrice = High[0] + tickVal; 
                pendingStopPrice = pendingEntryPrice - StopLossPoints;
                pendingTargetPrice = TargetPOC ? livePOC : pendingEntryPrice + (StopLossPoints * TargetRMultiple);
                
                activeLiveRisk = (pendingEntryPrice - pendingStopPrice) * Instrument.MasterInstrument.PointValue;
                EnterLongStopMarket(1, pendingEntryPrice, "FAM_Long");
                setupState = 4;
            }
            else
            {
                pendingEntryPrice = Low[0] - tickVal; 
                pendingStopPrice = pendingEntryPrice + StopLossPoints;
                pendingTargetPrice = TargetPOC ? livePOC : pendingEntryPrice - (StopLossPoints * TargetRMultiple);
                
                activeLiveRisk = (pendingStopPrice - pendingEntryPrice) * Instrument.MasterInstrument.PointValue;
                EnterShortStopMarket(1, pendingEntryPrice, "FAM_Short");
                setupState = 4;
            }
        }

        private void ManageOpenTrade()
        {
            setupState = 0; 
            
            if (Position.MarketPosition == MarketPosition.Long)
            {
                ExitLongStopMarket(0, true, 1, pendingStopPrice, "FAM_Stop", "FAM_Long");
                ExitLongLimit(0, true, 1, pendingTargetPrice, "FAM_Target", "FAM_Long");
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                ExitShortStopMarket(0, true, 1, pendingStopPrice, "FAM_Stop", "FAM_Short");
                ExitShortLimit(0, true, 1, pendingTargetPrice, "FAM_Target", "FAM_Short");
            }
        }

        private void ResetSetup()
        {
            setupState = 0;
            isLongSetup = false;
            fvgHigh = 0;
            fvgLow = 0;
            extremePivotPrice = 0;
            huntingBarsElapsed = 0;
            if (entryOrder != null && entryOrder.OrderState == OrderState.Working) CancelOrder(entryOrder);
            entryOrder = null;
        }
        #endregion

        #region Mathematics & Indicators
        private bool CheckWickDensity(bool checkingLong)
        {
            int validWicks = 0;
            for (int i = 0; i < WickLookback; i++)
            {
                double range = High[i] - Low[i];
                if (range == 0) continue;

                if (checkingLong)
                {
                    double lowerWick = Math.Min(Open[i], Close[i]) - Low[i];
                    if (lowerWick / range >= WickPercentage) validWicks++;
                }
                else
                {
                    double upperWick = High[i] - Math.Max(Open[i], Close[i]);
                    if (upperWick / range >= WickPercentage) validWicks++;
                }
            }
            return validWicks >= RequiredWicks;
        }

        private void FindMostRecentFVG(bool findingBearishForLong)
        {
            for (int i = 1; i <= 20; i++)
            {
                if (findingBearishForLong)
                {
                    if (High[i] < Low[i + 2]) 
                    {
                        fvgHigh = Low[i + 2];
                        fvgLow = High[i];
                        return;
                    }
                }
                else
                {
                    if (Low[i] > High[i + 2]) 
                    {
                        fvgHigh = Low[i];
                        fvgLow = High[i + 2];
                        return;
                    }
                }
            }
            fvgHigh = 0; fvgLow = 0;
        }

        private bool CheckEngulfing(bool isLong, double currClose, double currOpen, double prevClose, double prevOpen)
        {
            if (isLong) return currClose > prevOpen && currOpen < prevClose; 
            return currClose < prevOpen && currOpen > prevClose; 
        }

        private void UpdateProfilesAndZones(DateTime t, double h, double l)
        {
            double tick = TickSize;
            
            if (t.Date != lastSessionDate && t.Hour == 18) 
            {
                liveProfile.Clear();
                lastSessionDate = t.Date;
            }
            else if (t.Hour == 9 && t.Minute == 30) 
            {
                nyProfile.Clear();
            }

            if (t.Hour >= 9 && t.Hour < 16)
            {
                for (double p = l; p <= h; p += tick)
                {
                    double rp = Math.Round(p / tick) * tick;
                    if (!nyProfile.ContainsKey(rp)) nyProfile[rp] = 0;
                    nyProfile[rp]++;
                }
                if (t.Hour == 15 && t.Minute == 59) CalculateValueAreas(nyProfile, out nyPOC, out nyVAH, out nyVAL);
            }
            else if (t.Hour >= 18 || t.Hour < 9 || (t.Hour == 9 && t.Minute < 30))
            {
                for (double p = l; p <= h; p += tick)
                {
                    double rp = Math.Round(p / tick) * tick;
                    if (!liveProfile.ContainsKey(rp)) liveProfile[rp] = 0;
                    liveProfile[rp]++;
                }
                CalculateValueAreas(liveProfile, out livePOC, out liveVAH, out liveVAL);
            }
        }

        private void CalculateValueAreas(Dictionary<double, int> profile, out double poc, out double vah, out double val)
        {
            poc = 0; vah = 0; val = 0;
            if (profile.Count == 0) return;

            var sortedByVol = profile.OrderByDescending(x => x.Value).ToList();
            poc = sortedByVol.First().Key;

            int totalTPO = profile.Sum(x => x.Value);
            int targetTPO = (int)(totalTPO * 0.70);
            int currentTPO = sortedByVol.First().Value;

            double tick = TickSize;
            double highPtr = poc;
            double lowPtr = poc;

            while (currentTPO < targetTPO)
            {
                double nextHigh = highPtr + tick;
                double nextLow = lowPtr - tick;

                int highVol = profile.ContainsKey(nextHigh) ? profile[nextHigh] : 0;
                int lowVol = profile.ContainsKey(nextLow) ? profile[nextLow] : 0;

                if (highVol >= lowVol && highVol > 0)
                {
                    currentTPO += highVol;
                    highPtr = nextHigh;
                }
                else if (lowVol > 0)
                {
                    currentTPO += lowVol;
                    lowPtr = nextLow;
                }
                else break;
            }

            vah = highPtr;
            val = lowPtr;
        }
        #endregion
    }
}