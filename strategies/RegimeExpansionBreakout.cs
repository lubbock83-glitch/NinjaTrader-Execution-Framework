// =============================================================================
//  RegimeExpansion_Breakout - volatility squeeze release
//  DNA: Breakout
// =============================================================================
//
//  THE IDEA
//  Compression precedes expansion. Track how long the market has been squeezing
//  and take the release, on the premise that the duration of the squeeze is
//  informative about the size of the move that ends it.
//
//  DESIGN DECISIONS AND WHY
//
//  * Squeeze duration is a counter that resets only while flat. Resetting on
//    every non-squeezing bar would discard the compression history the entry
//    depends on, the moment price first ticks outside the band.
//
//  * There is an explicit exhaustion exit separate from the stop. A breakout
//    that stalls is a different failure from one that reverses, and waiting for
//    the stop in the stall case pays full risk for information already in hand.
//
//  * A hard time exit flattens before the close. Overnight gap risk is not part
//    of the edge being tested, so holding would contaminate the strategy
//    statistics with exposure the model never evaluated.
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
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class RegimeExpansion_Breakout : MasterTerminalBase
    {
        private Bollinger bb;
        private KeltnerChannel kc;
        private SMA volSma;
        private EMA trailEma;

        private int squeezeDuration = 0;

        [NinjaScriptProperty, Range(10.0, 10000.0), Display(Name = "Dollar Risk Per Trade ($)", GroupName = "1. Risk Management", Order = 1)]
        public double DollarRiskPerTrade { get; set; } = 300.0;

        protected override void OnStateChange()
        {
            base.OnStateChange();
            if (State == State.SetDefaults) Name = "Strategy 4: Regime Expansion Breakout";
        }

        protected override void OnStrategyInitialize()
        {
            bb = Bollinger(2.0, 20);
            kc = KeltnerChannel(1.5, 20);
            volSma = SMA(Volume, 20);
            trailEma = EMA(Close, 20);
        }

        protected override string GetStrategyDNA() => "Breakout";

        public override void GetStrategySessionWindow(out int startHHMM, out int endHHMM)
        {
            startHHMM = 930;
            endHHMM = 1600;
        }

        protected override void OnStrategyBarUpdate()
        {
            if (CurrentBar < 25) return;

            int timeHHMM = Time[0].Hour * 100 + Time[0].Minute;
            
            if (timeHHMM >= 1555 && Position.MarketPosition != MarketPosition.Flat)
            {
                if (Position.MarketPosition == MarketPosition.Long) ExitLong("TimeExit", "Vol_Exp_Long");
                if (Position.MarketPosition == MarketPosition.Short) ExitShort("TimeExit", "Vol_Exp_Short");
                return;
            }

            // 1. Compression Counter
            bool isSqueezing = bb.Upper[0] < kc.Upper[0] && bb.Lower[0] > kc.Lower[0];
            if (isSqueezing) squeezeDuration++;
            else if (Position.MarketPosition == MarketPosition.Flat) squeezeDuration = 0; 

            // 2. Trailing & Exhaustion Exits
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                bool exhaustion = Close[0] < kc.Upper[0] && Close[0] > kc.Lower[0] && Close[1] < kc.Upper[1] && Close[1] > kc.Lower[1];
                
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (exhaustion) { ExitLong("Exhaustion", "Vol_Exp_Long"); return; }
                    ExitLongStopMarket(0, true, Position.Quantity, trailEma[0], "TrailEMA", "Vol_Exp_Long");
                }
                else
                {
                    if (exhaustion) { ExitShort("Exhaustion", "Vol_Exp_Short"); return; }
                    ExitShortStopMarket(0, true, Position.Quantity, trailEma[0], "TrailEMA", "Vol_Exp_Short");
                }
                return;
            }

            // 3. Expansion Entry Logic
            if (CurrentMode == SystemMode.ArmAuto || CurrentMode == SystemMode.ArmLong || CurrentMode == SystemMode.ArmShort)
            {
                bool volSurge = Volume[0] >= 1.40 * volSma[0];
                double riskPts = Math.Abs(Close[0] - trailEma[0]);
                int qty = Math.Max(1, (int)Math.Floor(DollarRiskPerTrade / (riskPts * Instrument.MasterInstrument.PointValue)));

                if (bb.Upper[0] > kc.Upper[0] && squeezeDuration >= 4 && Close[0] > kc.Upper[0] && volSurge && CurrentMode != SystemMode.ArmShort)
                {
                    activeLiveRisk = riskPts * Instrument.MasterInstrument.PointValue * qty;
                    EnterLong(qty, "Vol_Exp_Long");
                    ExitLongStopMarket(0, true, qty, trailEma[0], "InitStop", "Vol_Exp_Long");
                }
                else if (bb.Lower[0] < kc.Lower[0] && squeezeDuration >= 4 && Close[0] < kc.Lower[0] && volSurge && CurrentMode != SystemMode.ArmLong)
                {
                    activeLiveRisk = riskPts * Instrument.MasterInstrument.PointValue * qty;
                    EnterShort(qty, "Vol_Exp_Short");
                    ExitShortStopMarket(0, true, qty, trailEma[0], "InitStop", "Vol_Exp_Short");
                }
            }
        }
    }
}