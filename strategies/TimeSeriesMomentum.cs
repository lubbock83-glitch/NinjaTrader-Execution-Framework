// =============================================================================
//  TSMOM_VolatilityNormalized - time series momentum, volatility scaled
//  DNA: Breakout
// =============================================================================
//
//  THE IDEA
//  Classic time-series momentum, with exposure scaled inversely to volatility so
//  each trade contributes comparable risk rather than comparable size.
//
//  DESIGN DECISIONS AND WHY
//
//  * Volatility is estimated with Garman-Klass rather than close-to-close.
//    Garman-Klass uses the full OHLC range and is materially more efficient per
//    observation, which matters because the estimate is rebuilt from a rolling
//    window only a few weeks long.
//
//  * The estimator warms up on at least five daily observations and holds a
//    rolling twenty. Sizing off a single day makes position size a function of
//    the previous day noise; holding far more makes it blind to a real regime
//    change.
//
//  * Volatility is sampled once per session on the first bar, not continuously.
//    Re-sizing mid-position as intraday volatility moves would mean the risk
//    being carried no longer matches the risk the entry was evaluated under.
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
    public class TSMOM_VolatilityNormalized : MasterTerminalBase
    {
        private EMA trendEma;
        private ATR atr;
        private List<double> dailyGkVolatilities = new List<double>();
        private double currentGkVol;
        private double thresholdVol;

        private double activeStopPrice = 0;
        private int entryQty = 0;
        private bool ratcheted = false;

        [NinjaScriptProperty, Range(10.0, 10000.0), Display(Name = "Dollar Risk Per Trade ($)", GroupName = "1. Risk Management", Order = 1)]
        public double DollarRiskPerTrade { get; set; } = 300.0;

        protected override void OnStateChange()
        {
            base.OnStateChange();
            if (State == State.SetDefaults) Name = "Strategy 1: TSMOM Volatility Normalized";
        }

        protected override void OnStrategyInitialize()
        {
            trendEma = EMA(Close, 50);
            atr = ATR(14);
            dailyGkVolatilities.Clear();
        }

        protected override string GetStrategyDNA() => "Breakout";

        public override void GetStrategySessionWindow(out int startHHMM, out int endHHMM)
        {
            startHHMM = 945;
            endHHMM = 1330;
        }

        public override List<OptimizationParam> GetOptimizableParameters()
        {
            return new List<OptimizationParam>
            {
                new OptimizationParam { Name = "DollarRisk", Min = 100, Max = 500, Step = 50, CurrentValue = DollarRiskPerTrade, ApplyValue = (v) => DollarRiskPerTrade = v }
            };
        }

        protected override void OnStrategyBarUpdate()
        {
            if (CurrentBar < 50) return;

            // 1. Garman-Klass Volatility Calculation
            double gkSum = 0;
            for (int i = 0; i < 20; i++)
            {
                double hl = Math.Log(High[i] / Low[i]);
                double co = Math.Log(Close[i] / Open[i]);
                gkSum += (0.5 * hl * hl) - ((2 * Math.Log(2) - 1) * co * co);
            }
            currentGkVol = Math.Sqrt(gkSum / 20.0);

            // Maintain 20-day distribution for 25th percentile gate
            if (Bars.IsFirstBarOfSession && currentGkVol > 0)
            {
                dailyGkVolatilities.Add(currentGkVol);
                if (dailyGkVolatilities.Count > 20) dailyGkVolatilities.RemoveAt(0);
                
                if (dailyGkVolatilities.Count >= 5)
                {
                    var sorted = dailyGkVolatilities.OrderBy(x => x).ToList();
                    thresholdVol = sorted[(int)Math.Floor(sorted.Count * 0.25)];
                }
            }

            // Time gates
            int timeHHMM = Time[0].Hour * 100 + Time[0].Minute;
            bool isTradingWindow = timeHHMM >= 945 && timeHHMM <= 1330;
            
            if (timeHHMM >= 1555 && Position.MarketPosition != MarketPosition.Flat)
            {
                if (Position.MarketPosition == MarketPosition.Long) ExitLong("TimeExit", "TSMOM_Long");
                if (Position.MarketPosition == MarketPosition.Short) ExitShort("TimeExit", "TSMOM_Short");
                return;
            }

            // 2. Normalized Trend Signal (S_t)
            double s_t = 0;
            if (currentGkVol > 0)
            {
                double term1 = (Close[0] - Close[4]) / (currentGkVol * Math.Sqrt(4));
                double term2 = (Close[0] - Close[16]) / (currentGkVol * Math.Sqrt(16));
                s_t = 0.5 * (term1 + term2);
            }

            // 3. Trade Management
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (s_t < 0) { ExitLong("Invalidated", "TSMOM_Long"); return; }
                    if (Close[0] <= activeStopPrice) { ExitLong("Stop", "TSMOM_Long"); return; }
                    
                    if (!ratcheted && Close[0] >= Position.AveragePrice + (1.50 * atr[0])) ratcheted = true;
                    if (ratcheted) activeStopPrice = Math.Max(activeStopPrice, High[0] - (2.0 * atr[0]));
                    ExitLongStopMarket(0, true, entryQty, activeStopPrice, "TrailStop", "TSMOM_Long");
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    if (s_t > 0) { ExitShort("Invalidated", "TSMOM_Short"); return; }
                    if (Close[0] >= activeStopPrice) { ExitShort("Stop", "TSMOM_Short"); return; }

                    if (!ratcheted && Close[0] <= Position.AveragePrice - (1.50 * atr[0])) ratcheted = true;
                    if (ratcheted) activeStopPrice = Math.Min(activeStopPrice, Low[0] + (2.0 * atr[0]));
                    ExitShortStopMarket(0, true, entryQty, activeStopPrice, "TrailStop", "TSMOM_Short");
                }
                return;
            }

            // 4. Entry Logic
            if (isTradingWindow && currentGkVol > thresholdVol && (CurrentMode == SystemMode.ArmAuto || CurrentMode == SystemMode.ArmLong || CurrentMode == SystemMode.ArmShort))
            {
                double riskPts = 1.75 * atr[0];
                int qty = Math.Max(1, (int)Math.Floor(DollarRiskPerTrade / (riskPts * Instrument.MasterInstrument.PointValue)));

                if (s_t > 1.50 && Close[0] > trendEma[0] && CurrentMode != SystemMode.ArmShort)
                {
                    entryQty = qty;
                    activeStopPrice = Close[0] - riskPts;
                    activeLiveRisk = riskPts * Instrument.MasterInstrument.PointValue * qty;
                    ratcheted = false;
                    EnterLong(qty, "TSMOM_Long");
                    ExitLongStopMarket(0, true, qty, activeStopPrice, "InitStop", "TSMOM_Long");
                }
                else if (s_t < -1.50 && Close[0] < trendEma[0] && CurrentMode != SystemMode.ArmLong)
                {
                    entryQty = qty;
                    activeStopPrice = Close[0] + riskPts;
                    activeLiveRisk = riskPts * Instrument.MasterInstrument.PointValue * qty;
                    ratcheted = false;
                    EnterShort(qty, "TSMOM_Short");
                    ExitShortStopMarket(0, true, qty, activeStopPrice, "InitStop", "TSMOM_Short");
                }
            }
        }
    }
}