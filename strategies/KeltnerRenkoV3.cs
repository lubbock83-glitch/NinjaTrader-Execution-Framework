// =============================================================================
//  KeltnerRenkoV3 - channel breakout with structural confirmation
//  DNA: Breakout
// =============================================================================
//
//  THE IDEA
//  Take Keltner channel breaches on Renko bricks, but only when the structure
//  leading into the breach says the move is an expansion rather than the tail
//  of a pullback.
//
//  DESIGN DECISIONS AND WHY
//
//  * Pullback structure is counted in BRICKS, not bars or ticks. On a Renko
//    series a brick is a fixed quantum of price movement, so "three bricks of
//    pullback" means the same thing in every regime, which a bar count does
//    not. Min/max pullback bounds reject both a breach with no preceding
//    consolidation and one that has already retraced too far to be continuation.
//
//  * DisplacementMode is an enum rather than a boolean, because projecting the
//    channel forward has several defensible definitions. PhaseLeadProjection
//    extrapolates from recent momentum instead of using the channel as printed:
//    a channel computed on closed bricks necessarily lags the price that just
//    breached it, and entering against a stale band is a systematic late entry.
//
//  * Every parameter is exposed as a NinjaScriptProperty with an explicit range
//    and display group. The ranges are not decoration. They bound the search
//    space the host optimiser may explore, so an optimisation run cannot wander
//    into settings that are untradeable in the live market.
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
using System.Globalization;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
using System.Windows.Media;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum KeltnerDisplacementMode
    {
        PhaseLeadProjection, 
        PositiveBarShift,    
        NoDisplacement       
    }

    public enum TradeManagementMode
    {
        StructuralTrailing,
        PyramidingWithTargets
    }

    public class KeltnerRenkoV3 : MasterTerminalBase
    {
        #region State Machine Enums & Variables
        private enum StructureStage
        {
            None,
            BandBroken,
            PullbackStarted,
            StructureFormed
        }

        private KeltnerChannel keltner;

        private StructureStage longStage;
        private StructureStage shortStage;
        private int pullbackBrickCount;
        private double structureBreakPrice;
        private double structureExtremeWick;
        private bool wasInsideChannelLong;
        private bool wasInsideChannelShort;

        private StructureStage trailStage;
        private int trailPullbackBricks;
        private double trailBreakPrice;
        private double trailPullbackWick;
        private double activeStopPrice;
        private int initialEntryQty;
        private int pyramidCount;

        private double sessionStartRealizedPnL;
        private bool dailyLimitHit;
        #endregion

        #region V3 Parameters
        [NinjaScriptProperty, Range(10, 200), Display(Name = "Keltner Period", GroupName = "1. Keltner & Displacement", Order = 1)]
        public int KeltnerPeriod { get; set; } = 30;

        [NinjaScriptProperty, Range(0.1, 10.0), Display(Name = "Keltner Multiplier", GroupName = "1. Keltner & Displacement", Order = 2)]
        public double KeltnerMultiplier { get; set; } = 1.5;

        [NinjaScriptProperty, Display(Name = "Displacement Mode", GroupName = "1. Keltner & Displacement", Order = 3)]
        public KeltnerDisplacementMode DisplacementMode { get; set; } = KeltnerDisplacementMode.PhaseLeadProjection;

        [NinjaScriptProperty, Range(0, 500), Display(Name = "Displacement Bars", GroupName = "1. Keltner & Displacement", Order = 4)]
        public int DisplacementBars { get; set; } = 75;

        [NinjaScriptProperty, Range(2, 50), Display(Name = "Projection Lookback (Momentum)", GroupName = "1. Keltner & Displacement", Order = 5)]
        public int ProjectionLookback { get; set; } = 10;

        [NinjaScriptProperty, Range(0.1, 5.0), Display(Name = "Projection Sensitivity", GroupName = "1. Keltner & Displacement", Order = 6)]
        public double ProjectionSensitivity { get; set; } = 0.95;

        [NinjaScriptProperty, Range(1, 10), Display(Name = "Min Bricks Per Pullback", GroupName = "2. Structure Confirmation", Order = 1)]
        public int MinSwingBricks { get; set; } = 1;

        [NinjaScriptProperty, Range(2, 50), Display(Name = "Max Pullback Bricks", GroupName = "2. Structure Confirmation", Order = 2)]
        public int MaxPullbackBricks { get; set; } = 8;

        [NinjaScriptProperty, Range(0.0, 500.0), Display(Name = "Stop Buffer (Points)", GroupName = "2. Structure Confirmation", Order = 3)]
        public double StopBufferPoints { get; set; } = 1.0;

        [NinjaScriptProperty, Display(Name = "Trade Management Mode", GroupName = "3. Trade Management", Order = 1)]
        public TradeManagementMode ManagementMode { get; set; } = TradeManagementMode.PyramidingWithTargets;

        [NinjaScriptProperty, Range(1.0, 20.0), Display(Name = "Per-Entry Target (R-Multiple)", GroupName = "3. Trade Management", Order = 2)]
        public double TargetRMultiple { get; set; } = 5.0;

        [NinjaScriptProperty, Range(1, 20), Display(Name = "Pyramid Quantity", GroupName = "3. Trade Management", Order = 3)]
        public int PyramidQuantity { get; set; } = 1;

        [NinjaScriptProperty, Display(Name = "Use Dynamic Risk", GroupName = "4. Risk & Sizing", Order = 1)]
        public bool UseDynamicRisk { get; set; } = true;

        [NinjaScriptProperty, Range(10.0, 100000.0), Display(Name = "Dollar Risk Per Trade ($)", GroupName = "4. Risk & Sizing", Order = 2)]
        public double DollarRiskPerTrade { get; set; } = 250.0;

        [NinjaScriptProperty, Range(1, 100), Display(Name = "Default Contracts", GroupName = "4. Risk & Sizing", Order = 3)]
        public int DefaultContracts { get; set; } = 1;

        [NinjaScriptProperty, Range(1, 100), Display(Name = "Max Contracts Cap", GroupName = "4. Risk & Sizing", Order = 4)]
        public int MaxContracts { get; set; } = 6;

        [NinjaScriptProperty, Range(0.0, 100000.0), Display(Name = "Max Daily Loss ($)", GroupName = "5. Prop Guardrails", Order = 1)]
        public double MaxDailyLoss { get; set; } = 1000.0;

        [NinjaScriptProperty, Range(0.0, 100000.0), Display(Name = "Max Daily Profit ($)", GroupName = "5. Prop Guardrails", Order = 2)]
        public double MaxDailyProfit { get; set; } = 2000.0;

        [NinjaScriptProperty, Display(Name = "Enable Chart Time Filter", GroupName = "6. Chart Time Filter", Order = 1)]
        public bool EnableTimeFilter { get; set; } = true;

        [NinjaScriptProperty, Range(0, 2359), Display(Name = "Chart Start Time (HHMM)", GroupName = "6. Chart Time Filter", Order = 2)]
        public int StartTimeHHMM { get; set; } = 800;

        [NinjaScriptProperty, Range(0, 2359), Display(Name = "Chart End Time (HHMM)", GroupName = "6. Chart Time Filter", Order = 3)]
        public int EndTimeHHMM { get; set; } = 900;

        [NinjaScriptProperty, Display(Name = "Flatten At End Time", GroupName = "6. Chart Time Filter", Order = 4)]
        public bool FlattenAtEndTime { get; set; } = true;
        #endregion

        protected override void OnStateChange()
        {
            base.OnStateChange();
            if (State == State.SetDefaults)
            {
                Name = "Keltner V3 Trend Continuation";
                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "DisplacedUpper");
                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "DisplacedLower");
                AddPlot(new Stroke(Brushes.Gold, 2), PlotStyle.Dot, "ActiveStop");
            }
        }

        protected override void OnStrategyInitialize()
        {
            keltner = KeltnerChannel(KeltnerMultiplier, KeltnerPeriod);
            ResetEntryState();
            ResetTrailState();
            dailyLimitHit = false;
        }

        protected override string GetStrategyDNA()
        {
            return "Breakout"; 
        }

        #region Master Terminal Agnostic Contracts
        public override void GetStrategySessionWindow(out int startHHMM, out int endHHMM)
        {
            startHHMM = StartTimeHHMM;
            endHHMM = EndTimeHHMM;
        }

        public override List<OptimizationParam> GetOptimizableParameters()
        {
            return new List<OptimizationParam>
            {
                new OptimizationParam { 
                    Name = "KeltnerPeriod", 
                    Min = 20, Max = 60, Step = 10, 
                    CurrentValue = KeltnerPeriod, 
                    ApplyValue = (v) => { KeltnerPeriod = (int)v; keltner = KeltnerChannel(KeltnerMultiplier, KeltnerPeriod); } 
                },
                new OptimizationParam { 
                    Name = "KeltnerMultiplier", 
                    Min = 1.0, Max = 2.5, Step = 0.5, 
                    CurrentValue = KeltnerMultiplier, 
                    ApplyValue = (v) => { KeltnerMultiplier = v; keltner = KeltnerChannel(KeltnerMultiplier, KeltnerPeriod); } 
                },
                new OptimizationParam { 
                    Name = "StopBufferPoints", 
                    Min = 0.5, Max = 2.5, Step = 0.5, 
                    CurrentValue = StopBufferPoints, 
                    ApplyValue = (v) => StopBufferPoints = v 
                },
                new OptimizationParam { 
                    Name = "TargetRMultiple", 
                    Min = 2.0, Max = 6.0, Step = 1.0, 
                    CurrentValue = TargetRMultiple, 
                    ApplyValue = (v) => TargetRMultiple = v 
                }
            };
        }

        public override List<SimTrade> EvaluateCandidateParameters(List<BrickData> sessionBars, Dictionary<string, double> candidateValues)
        {
            List<SimTrade> results = new List<SimTrade>();
            int period = candidateValues.ContainsKey("KeltnerPeriod") ? (int)candidateValues["KeltnerPeriod"] : KeltnerPeriod;
            double mult = candidateValues.ContainsKey("KeltnerMultiplier") ? candidateValues["KeltnerMultiplier"] : KeltnerMultiplier;
            double stopBuffer = candidateValues.ContainsKey("StopBufferPoints") ? candidateValues["StopBufferPoints"] : StopBufferPoints;
            double rMult = candidateValues.ContainsKey("TargetRMultiple") ? candidateValues["TargetRMultiple"] : TargetRMultiple;

            if (sessionBars.Count < period + 10) return results;

            double[] ema = new double[sessionBars.Count];
            double[] atr = new double[sessionBars.Count];
            double[] upper = new double[sessionBars.Count];
            double[] lower = new double[sessionBars.Count];
            
            double k = 2.0 / (period + 1);
            ema[0] = sessionBars[0].C; 
            atr[0] = sessionBars[0].H - sessionBars[0].L;

            for (int i = 1; i < sessionBars.Count; i++)
            {
                ema[i] = (sessionBars[i].C * k) + (ema[i-1] * (1 - k));
                double tr = Math.Max(sessionBars[i].H - sessionBars[i].L, Math.Max(Math.Abs(sessionBars[i].H - sessionBars[i-1].C), Math.Abs(sessionBars[i].L - sessionBars[i-1].C)));
                atr[i] = (tr * k) + (atr[i-1] * (1 - k));
                upper[i] = ema[i] + (mult * atr[i]);
                lower[i] = ema[i] - (mult * atr[i]);
            }

            int pos = 0;
            double entry = 0, stop = 0, target = 0;
            double ptVal = Instrument.MasterInstrument.PointValue;

            for (int i = period + 2; i < sessionBars.Count; i++)
            {
                if (pos == 0)
                {
                    if (sessionBars[i-1].C > upper[i-1] && sessionBars[i].C > sessionBars[i-1].H)
                    {
                        pos = 1;
                        entry = sessionBars[i].C;
                        stop = sessionBars[i].L - stopBuffer;
                        double risk = entry - stop;
                        target = entry + (risk * rMult);
                    }
                    else if (sessionBars[i-1].C < lower[i-1] && sessionBars[i].C < sessionBars[i-1].L)
                    {
                        pos = -1;
                        entry = sessionBars[i].C;
                        stop = sessionBars[i].H + stopBuffer;
                        double risk = stop - entry;
                        target = entry - (risk * rMult);
                    }
                }
                else
                {
                    if (pos == 1)
                    {
                        if (sessionBars[i].L <= stop) { results.Add(new SimTrade { PnL = (stop - entry) * ptVal, RiskAmount = (entry - stop) * ptVal }); pos = 0; }
                        else if (sessionBars[i].H >= target) { results.Add(new SimTrade { PnL = (target - entry) * ptVal, RiskAmount = (entry - stop) * ptVal }); pos = 0; }
                    }
                    else if (pos == -1)
                    {
                        if (sessionBars[i].H >= stop) { results.Add(new SimTrade { PnL = (entry - stop) * ptVal, RiskAmount = (stop - entry) * ptVal }); pos = 0; }
                        else if (sessionBars[i].L <= target) { results.Add(new SimTrade { PnL = (entry - target) * ptVal, RiskAmount = (stop - entry) * ptVal }); pos = 0; }
                    }
                }
            }

            return results;
        }
        #endregion

        protected override void OnStrategyBarUpdate()
        {
            int requiredBars = Math.Max(KeltnerPeriod, Math.Max(ProjectionLookback, Math.Abs(DisplacementBars))) + 5;
            if (CurrentBar < requiredBars) return;

            double upperBand, lowerBand;
            GetDisplacedBands(out upperBand, out lowerBand);

            Values[0][0] = upperBand;
            Values[1][0] = lowerBand;

            if (Position.MarketPosition != MarketPosition.Flat && activeStopPrice > 0)
                Values[2][0] = activeStopPrice;

            CheckDailyRiskLimits();
            if (dailyLimitHit) return;

            bool isWithinTimeWindow = IsChartTimeAllowed();

            if (!isWithinTimeWindow && FlattenAtEndTime && Position.MarketPosition != MarketPosition.Flat)
            {
                if (Position.MarketPosition == MarketPosition.Long) FlattenAllLongs("TimeWindow_Exit");
                else if (Position.MarketPosition == MarketPosition.Short) FlattenAllShorts("TimeWindow_Exit");
                ResetEntryState();
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManageOpenPosition();
            }
            else
            {
                if (isWithinTimeWindow && (CurrentMode == SystemMode.ArmAuto || CurrentMode == SystemMode.ArmLong || CurrentMode == SystemMode.ArmShort))
                {
                    ScanForTrendContinuation(upperBand, lowerBand);
                }
                else
                {
                    ResetEntryState();
                }
            }
        }

        #region Core Strategy Mechanics
        private bool IsChartTimeAllowed()
        {
            if (!EnableTimeFilter) return true;
            int currentChartHHMM = (Time[0].Hour * 100) + Time[0].Minute;
            if (StartTimeHHMM <= EndTimeHHMM)
                return currentChartHHMM >= StartTimeHHMM && currentChartHHMM <= EndTimeHHMM;
            return currentChartHHMM >= StartTimeHHMM || currentChartHHMM <= EndTimeHHMM;
        }

        private void GetDisplacedBands(out double upperBand, out double lowerBand)
        {
            int lookback = Math.Min(CurrentBar, Math.Max(1, ProjectionLookback));
            double midlineSlopePerBar = (keltner.Midline[0] - keltner.Midline[lookback]) / Math.Max(1, lookback);

            if (DisplacementMode == KeltnerDisplacementMode.PositiveBarShift)
            {
                int shift = Math.Min(CurrentBar, Math.Abs(DisplacementBars));
                upperBand = keltner.Upper[shift];
                lowerBand = keltner.Lower[shift];
            }
            else if (DisplacementMode == KeltnerDisplacementMode.PhaseLeadProjection)
            {
                double projectedShift = midlineSlopePerBar * Math.Abs(DisplacementBars) * ProjectionSensitivity;
                upperBand = keltner.Upper[0] + projectedShift;
                lowerBand = keltner.Lower[0] + projectedShift;
            }
            else
            {
                upperBand = keltner.Upper[0];
                lowerBand = keltner.Lower[0];
            }
        }

        private void ScanForTrendContinuation(double upperBand, double lowerBand)
        {
            bool isUpBrick   = Close[0] > Open[0];
            bool isDownBrick = Close[0] < Open[0];

            if (Close[0] <= upperBand) wasInsideChannelLong = true;
            if (Close[0] >= lowerBand) wasInsideChannelShort = true;

            if (CurrentMode != SystemMode.ArmShort)
            {
                if (longStage == StructureStage.None && Close[0] > upperBand && wasInsideChannelLong)
                {
                    longStage = StructureStage.BandBroken;
                }
                else if (longStage == StructureStage.BandBroken)
                {
                    if (isDownBrick)
                    {
                        longStage = StructureStage.PullbackStarted;
                        structureBreakPrice = High[1]; 
                        structureExtremeWick = Low[0];
                        pullbackBrickCount = 1;
                    }
                    else if (Close[0] < lowerBand) ResetEntryState();
                }
                else if (longStage == StructureStage.PullbackStarted)
                {
                    if (isDownBrick)
                    {
                        structureExtremeWick = Math.Min(structureExtremeWick, Low[0]);
                        pullbackBrickCount++;
                        if (pullbackBrickCount > MaxPullbackBricks || Close[0] < lowerBand) 
                            ResetEntryState(); 
                    }
                    else if (isUpBrick)
                    {
                        if (pullbackBrickCount >= MinSwingBricks)
                            longStage = StructureStage.StructureFormed;
                        else
                            ResetEntryState(); 
                    }
                }
                else if (longStage == StructureStage.StructureFormed)
                {
                    if (isDownBrick)
                    {
                        structureExtremeWick = Math.Min(structureExtremeWick, Low[0]);
                    }
                    else if (isUpBrick && Close[0] > structureBreakPrice)
                    {
                        double stopPrice = Instrument.MasterInstrument.RoundToTickSize(structureExtremeWick - StopBufferPoints);
                        double stopDistPoints = Math.Max(TickSize, Close[0] - stopPrice);
                        
                        int qty = CalculateOrderQuantity(stopDistPoints);
                        initialEntryQty = qty;
                        activeStopPrice = stopPrice;
                        pyramidCount = 0;
                        ResetTrailState();

                        activeLiveRisk = stopDistPoints * Instrument.MasterInstrument.PointValue * qty;
                        EnterLong(qty, "Long_BOS");

                        if (ManagementMode == TradeManagementMode.PyramidingWithTargets)
                        {
                            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + (stopDistPoints * TargetRMultiple));
                            ExitLongLimit(0, true, qty, targetPrice, "Target_BOS", "Long_BOS");
                        }
                        
                        ExitLongStopMarket(0, true, qty, activeStopPrice, "Long_Stop", "Long_BOS");
                        ResetEntryState();
                        return;
                    }
                }
            }

            if (CurrentMode != SystemMode.ArmLong)
            {
                if (shortStage == StructureStage.None && Close[0] < lowerBand && wasInsideChannelShort)
                {
                    shortStage = StructureStage.BandBroken;
                }
                else if (shortStage == StructureStage.BandBroken)
                {
                    if (isUpBrick)
                    {
                        shortStage = StructureStage.PullbackStarted;
                        structureBreakPrice = Low[1]; 
                        structureExtremeWick = High[0];
                        pullbackBrickCount = 1;
                    }
                    else if (Close[0] > upperBand) ResetEntryState();
                }
                else if (shortStage == StructureStage.PullbackStarted)
                {
                    if (isUpBrick)
                    {
                        structureExtremeWick = Math.Max(structureExtremeWick, High[0]);
                        pullbackBrickCount++;
                        if (pullbackBrickCount > MaxPullbackBricks || Close[0] > upperBand) 
                            ResetEntryState(); 
                    }
                    else if (isDownBrick)
                    {
                        if (pullbackBrickCount >= MinSwingBricks)
                            shortStage = StructureStage.StructureFormed;
                        else
                            ResetEntryState(); 
                    }
                }
                else if (shortStage == StructureStage.StructureFormed)
                {
                    if (isUpBrick)
                    {
                        structureExtremeWick = Math.Max(structureExtremeWick, High[0]);
                    }
                    else if (isDownBrick && Close[0] < structureBreakPrice)
                    {
                        double stopPrice = Instrument.MasterInstrument.RoundToTickSize(structureExtremeWick + StopBufferPoints);
                        double stopDistPoints = Math.Max(TickSize, stopPrice - Close[0]);
                        
                        int qty = CalculateOrderQuantity(stopDistPoints);
                        initialEntryQty = qty;
                        activeStopPrice = stopPrice;
                        pyramidCount = 0;
                        ResetTrailState();

                        activeLiveRisk = stopDistPoints * Instrument.MasterInstrument.PointValue * qty;
                        EnterShort(qty, "Short_BOS");

                        if (ManagementMode == TradeManagementMode.PyramidingWithTargets)
                        {
                            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] - (stopDistPoints * TargetRMultiple));
                            ExitShortLimit(0, true, qty, targetPrice, "Target_BOS", "Short_BOS");
                        }

                        ExitShortStopMarket(0, true, qty, activeStopPrice, "Short_Stop", "Short_BOS");
                        ResetEntryState();
                        return;
                    }
                }
            }
        }

        private void ManageOpenPosition()
        {
            bool isUpBrick   = Close[0] > Open[0];
            bool isDownBrick = Close[0] < Open[0];

            if (Position.MarketPosition == MarketPosition.Long)
            {
                if (Close[0] <= activeStopPrice)
                {
                    FlattenAllLongs("Stop_Structural");
                    return;
                }

                if (trailStage == StructureStage.None)
                {
                    if (isDownBrick)
                    {
                        trailStage = StructureStage.PullbackStarted;
                        trailBreakPrice = High[1]; 
                        trailPullbackWick = Low[0];
                        trailPullbackBricks = 1;
                    }
                }
                else if (trailStage == StructureStage.PullbackStarted)
                {
                    if (isDownBrick)
                    {
                        trailPullbackWick = Math.Min(trailPullbackWick, Low[0]);
                        trailPullbackBricks++;
                    }
                    else if (isUpBrick)
                    {
                        if (trailPullbackBricks >= MinSwingBricks) trailStage = StructureStage.StructureFormed;
                        else trailStage = StructureStage.None; 
                    }
                }
                else if (trailStage == StructureStage.StructureFormed)
                {
                    if (isDownBrick)
                    {
                        trailPullbackWick = Math.Min(trailPullbackWick, Low[0]);
                    }
                    else if (isUpBrick && Close[0] > trailBreakPrice)
                    {
                        double candidateStop = Instrument.MasterInstrument.RoundToTickSize(trailPullbackWick - StopBufferPoints);
                        if (candidateStop > activeStopPrice)
                        {
                            activeStopPrice = candidateStop;
                            UpdateLongStops(activeStopPrice);
                        }

                        if (ManagementMode == TradeManagementMode.PyramidingWithTargets && (Position.Quantity + PyramidQuantity <= MaxContracts))
                        {
                            pyramidCount++;
                            string sigName = "Long_Pyr_" + pyramidCount;
                            double riskPts = Close[0] - activeStopPrice;
                            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + (riskPts * TargetRMultiple));

                            EnterLong(PyramidQuantity, sigName);
                            ExitLongLimit(0, true, PyramidQuantity, targetPrice, "Target_Pyr_" + pyramidCount, sigName);
                            ExitLongStopMarket(0, true, PyramidQuantity, activeStopPrice, "Long_Stop_" + pyramidCount, sigName);
                        }

                        trailStage = StructureStage.None; 
                    }
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                if (Close[0] >= activeStopPrice)
                {
                    FlattenAllShorts("Stop_Structural");
                    return;
                }

                if (trailStage == StructureStage.None)
                {
                    if (isUpBrick)
                    {
                        trailStage = StructureStage.PullbackStarted;
                        trailBreakPrice = Low[1]; 
                        trailPullbackWick = High[0];
                        trailPullbackBricks = 1;
                    }
                }
                else if (trailStage == StructureStage.PullbackStarted)
                {
                    if (isUpBrick)
                    {
                        trailPullbackWick = Math.Max(trailPullbackWick, High[0]);
                        trailPullbackBricks++;
                    }
                    else if (isDownBrick)
                    {
                        if (trailPullbackBricks >= MinSwingBricks) trailStage = StructureStage.StructureFormed;
                        else trailStage = StructureStage.None; 
                    }
                }
                else if (trailStage == StructureStage.StructureFormed)
                {
                    if (isUpBrick)
                    {
                        trailPullbackWick = Math.Max(trailPullbackWick, High[0]);
                    }
                    else if (isDownBrick && Close[0] < trailBreakPrice)
                    {
                        double candidateStop = Instrument.MasterInstrument.RoundToTickSize(trailPullbackWick + StopBufferPoints);
                        if (candidateStop < activeStopPrice)
                        {
                            activeStopPrice = candidateStop;
                            UpdateShortStops(activeStopPrice);
                        }

                        if (ManagementMode == TradeManagementMode.PyramidingWithTargets && (Position.Quantity + PyramidQuantity <= MaxContracts))
                        {
                            pyramidCount++;
                            string sigName = "Short_Pyr_" + pyramidCount;
                            double riskPts = activeStopPrice - Close[0];
                            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] - (riskPts * TargetRMultiple));

                            EnterShort(PyramidQuantity, sigName);
                            ExitShortLimit(0, true, PyramidQuantity, targetPrice, "Target_Pyr_" + pyramidCount, sigName);
                            ExitShortStopMarket(0, true, PyramidQuantity, activeStopPrice, "Short_Stop_" + pyramidCount, sigName);
                        }

                        trailStage = StructureStage.None; 
                    }
                }
            }
        }

        private void UpdateLongStops(double stopPrice)
        {
            ExitLongStopMarket(0, true, initialEntryQty, stopPrice, "Long_Stop", "Long_BOS");
            for (int i = 1; i <= pyramidCount; i++)
                ExitLongStopMarket(0, true, PyramidQuantity, stopPrice, "Long_Stop_" + i, "Long_Pyr_" + i);
        }

        private void UpdateShortStops(double stopPrice)
        {
            ExitShortStopMarket(0, true, initialEntryQty, stopPrice, "Short_Stop", "Short_BOS");
            for (int i = 1; i <= pyramidCount; i++)
                ExitShortStopMarket(0, true, PyramidQuantity, stopPrice, "Short_Stop_" + i, "Short_Pyr_" + i);
        }

        private void FlattenAllLongs(string exitReason)
        {
            ExitLong(exitReason, "Long_BOS");
            for (int i = 1; i <= pyramidCount; i++) ExitLong(exitReason + "_" + i, "Long_Pyr_" + i);
        }

        private void FlattenAllShorts(string exitReason)
        {
            ExitShort(exitReason, "Short_BOS");
            for (int i = 1; i <= pyramidCount; i++) ExitShort(exitReason + "_" + i, "Short_Pyr_" + i);
        }

        private void ResetEntryState()
        {
            longStage = StructureStage.None;
            shortStage = StructureStage.None;
            pullbackBrickCount = 0;
            structureBreakPrice = 0;
            structureExtremeWick = 0;
            wasInsideChannelLong = false;
            wasInsideChannelShort = false;
        }

        private void ResetTrailState()
        {
            trailStage = StructureStage.None;
            trailPullbackBricks = 0;
            trailBreakPrice = 0;
            trailPullbackWick = 0;
        }

        private int CalculateOrderQuantity(double stopDistancePoints)
        {
            if (!UseDynamicRisk) return DefaultContracts;
            double dollarRiskPerContract = stopDistancePoints * Instrument.MasterInstrument.PointValue;
            if (dollarRiskPerContract <= 0) return DefaultContracts;
            int qty = (int)Math.Floor(DollarRiskPerTrade / dollarRiskPerContract);
            return Math.Max(1, Math.Min(qty, MaxContracts));
        }

        private void CheckDailyRiskLimits()
        {
            if (Bars.IsFirstBarOfSession)
            {
                sessionStartRealizedPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                dailyLimitHit = false;
                ResetEntryState();
            }

            double closedPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartRealizedPnL;
            double openPnL   = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);
            double totalPnL  = closedPnL + openPnL;

            if ((MaxDailyLoss > 0 && totalPnL <= -Math.Abs(MaxDailyLoss)) || (MaxDailyProfit > 0 && totalPnL >= Math.Abs(MaxDailyProfit)))
            {
                dailyLimitHit = true;
                ResetEntryState();
                if (Position.MarketPosition == MarketPosition.Long) FlattenAllLongs("DailyLimit_Exit");
                else if (Position.MarketPosition == MarketPosition.Short) FlattenAllShorts("DailyLimit_Exit");
            }
        }
        #endregion
    }
}