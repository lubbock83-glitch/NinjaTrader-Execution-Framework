// =============================================================================
//  TPO_ValueAreaRotation - market profile rotation
//  DNA: Mean-Reversion
// =============================================================================
//
//  THE IDEA
//  Build a session volume profile, and trade rotations back toward value when
//  price extends away from it without finding acceptance.
//
//  DESIGN DECISIONS AND WHY
//
//  * The profile accumulates live into a price-keyed map during the session, and
//    the point of control is only fixed at the close. Computing a POC from a
//    partial profile produces a reference that moves under the strategy while
//    the trade is open.
//
//  * Entries reference the PRIOR session POC. The developing profile is not a
//    level yet, it is a level being discovered, and using it as a target is
//    circular.
//
//  * The initial balance is captured at a fixed early time rather than by range
//    detection, so the reference is reproducible across runs and cannot shift
//    as a side effect of a parameter change.
//
//  * The time stop fires earlier than in the other strategies here. A rotation
//    that has not completed by late session is unlikely to complete into the
//    close, and holding it competes with closing auction flow.
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
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TPO_ValueAreaRotation : MasterTerminalBase
    {
        private Dictionary<double, double> sessionVolume = new Dictionary<double, double>();
        private double prevVAH = 0, prevVAL = 0, prevPOC = 0;
        private double b1High, b1Low, b2High, b2Low;
        
        private double activeStop = 0;
        private int unitSize = 0;
        private bool scale1Hit = false;

        [NinjaScriptProperty, Range(10.0, 10000.0), Display(Name = "Dollar Risk Per Trade ($)", GroupName = "1. Risk Management", Order = 1)]
        public double DollarRiskPerTrade { get; set; } = 250.0;

        protected override void OnStateChange()
        {
            base.OnStateChange();
            if (State == State.SetDefaults) Name = "Strategy 2: TPO 80% Rotation";
        }

        protected override void OnStrategyInitialize() { }
        protected override string GetStrategyDNA() => "Mean-Reversion";

        public override void GetStrategySessionWindow(out int startHHMM, out int endHHMM)
        {
            startHHMM = 930;
            endHHMM = 1030;
        }

        protected override void OnStrategyBarUpdate()
        {
            if (CurrentBar < 10) return;

            int timeHHMM = Time[0].Hour * 100 + Time[0].Minute;

            // 1. Internal TPO Volume Profiler (09:30 - 16:00)
            if (timeHHMM > 930 && timeHHMM <= 1600)
            {
                double tick = TickSize;
                for (double p = Low[0]; p <= High[0]; p += tick)
                {
                    double rp = Math.Round(p / tick) * tick;
                    if (!sessionVolume.ContainsKey(rp)) sessionVolume[rp] = 0;
                    sessionVolume[rp] += Volume[0]; // Simplified volume distribution
                }

                if (timeHHMM == 1600)
                {
                    CalculateTPO(out prevPOC, out prevVAH, out prevVAL);
                    sessionVolume.Clear();
                }
            }

            if (prevPOC == 0) return; // Need prior day profile

            // Hardware time flatten
            if (timeHHMM >= 1545 && Position.MarketPosition != MarketPosition.Flat)
            {
                if (Position.MarketPosition == MarketPosition.Long) ExitLong("TimeStop", "TPO_Long");
                if (Position.MarketPosition == MarketPosition.Short) ExitShort("TimeStop", "TPO_Short");
                return;
            }

            // Track Bracket 1 (closes at 10:00) and Bracket 2 (closes at 10:30)
            if (timeHHMM == 1000) { b1High = High[0]; b1Low = Low[0]; }
            if (timeHHMM == 1030) { b2High = High[0]; b2Low = Low[0]; }

            // 2. Trade Management (FIFO Strict)
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (!scale1Hit && High[0] >= prevPOC) 
                    { 
                        scale1Hit = true; 
                        activeStop = Position.AveragePrice + TickSize; // Breakeven + 1
                    }
                    ExitLongStopMarket(0, true, Position.Quantity, activeStop, "TrailStop", "TPO_Long");
                    if (scale1Hit) ExitLongLimit(0, true, Position.Quantity, prevVAH - (2 * TickSize), "Target2", "TPO_Long");
                }
                else
                {
                    if (!scale1Hit && Low[0] <= prevPOC) 
                    { 
                        scale1Hit = true; 
                        activeStop = Position.AveragePrice - TickSize; // Breakeven + 1
                    }
                    ExitShortStopMarket(0, true, Position.Quantity, activeStop, "TrailStop", "TPO_Short");
                    if (scale1Hit) ExitShortLimit(0, true, Position.Quantity, prevVAL + (2 * TickSize), "Target2", "TPO_Short");
                }
                return;
            }

            // 3. Entry Trigger at exactly 10:30 bar open
            if (timeHHMM == 1030 && (CurrentMode == SystemMode.ArmAuto || CurrentMode == SystemMode.ArmLong || CurrentMode == SystemMode.ArmShort))
            {
                bool longCond = Open[1] < prevVAL && Close[1] > prevVAL && Close[0] > prevVAL;
                bool shortCond = Open[1] > prevVAH && Close[1] < prevVAH && Close[0] < prevVAH;

                if (longCond && CurrentMode != SystemMode.ArmShort)
                {
                    activeStop = Math.Min(b1Low, b2Low) - (2 * TickSize);
                    double riskPts = Close[0] - activeStop;
                    unitSize = Math.Max(1, (int)Math.Floor(DollarRiskPerTrade / (2 * riskPts * Instrument.MasterInstrument.PointValue)));
                    
                    activeLiveRisk = riskPts * Instrument.MasterInstrument.PointValue * (unitSize * 2);
                    scale1Hit = false;

                    EnterLong(unitSize * 2, "TPO_Long");
                    ExitLongLimit(0, true, unitSize, prevPOC, "Target_POC", "TPO_Long");
                    ExitLongStopMarket(0, true, unitSize * 2, activeStop, "InitStop", "TPO_Long");
                }
                else if (shortCond && CurrentMode != SystemMode.ArmLong)
                {
                    activeStop = Math.Max(b1High, b2High) + (2 * TickSize);
                    double riskPts = activeStop - Close[0];
                    unitSize = Math.Max(1, (int)Math.Floor(DollarRiskPerTrade / (2 * riskPts * Instrument.MasterInstrument.PointValue)));
                    
                    activeLiveRisk = riskPts * Instrument.MasterInstrument.PointValue * (unitSize * 2);
                    scale1Hit = false;

                    EnterShort(unitSize * 2, "TPO_Short");
                    ExitShortLimit(0, true, unitSize, prevPOC, "Target_POC", "TPO_Short");
                    ExitShortStopMarket(0, true, unitSize * 2, activeStop, "InitStop", "TPO_Short");
                }
            }
        }

        private void CalculateTPO(out double poc, out double vah, out double val)
        {
            poc = 0; vah = 0; val = 0;
            if (sessionVolume.Count == 0) return;
            var sorted = sessionVolume.OrderByDescending(x => x.Value).ToList();
            poc = sorted.First().Key;
            
            double targetVol = sessionVolume.Sum(x => x.Value) * 0.70;
            double currentVol = sorted.First().Value;
            double highPtr = poc, lowPtr = poc;

            while (currentVol < targetVol)
            {
                double nextH = highPtr + TickSize, nextL = lowPtr - TickSize;
                double volH = sessionVolume.ContainsKey(nextH) ? sessionVolume[nextH] : 0;
                double volL = sessionVolume.ContainsKey(nextL) ? sessionVolume[nextL] : 0;

                if (volH >= volL && volH > 0) { currentVol += volH; highPtr = nextH; }
                else if (volL > 0) { currentVol += volL; lowPtr = nextL; }
                else break;
            }
            vah = highPtr; val = lowPtr;
        }
    }
}
