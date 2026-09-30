// =============================================================================
//  CVD_AbsorptionDivergence - order flow absorption reversal
//  DNA: Mean-Reversion
// =============================================================================
//
//  THE IDEA
//  When price slopes down while cumulative delta slopes up, sellers are hitting
//  bids into buyers who are absorbing them. Price is falling on paper while the
//  order book says the opposite. That divergence is the signal.
//
//  DESIGN DECISIONS AND WHY
//
//  * The slope threshold is scaled by ATR rather than fixed in points, so one
//    configuration means "a meaningful move" in both a quiet overnight session
//    and a volatile open.
//
//  * Divergence alone does not trigger an entry: the signal also requires the
//    current bar to close up and above the previous close. Absorption can run
//    for a long time before it resolves, and without a confirmation bar the
//    model enters at every step on the way down.
//
//  * Requires the NinjaTrader Order Flow addon and will not compile without it.
//    That is a hard dependency rather than a graceful degradation on purpose: a
//    silently disabled CVD input would leave the strategy trading on price alone
//    while still reporting itself as an order flow model.
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
    public class CVD_AbsorptionDivergence : MasterTerminalBase
    {
        private OrderFlowCumulativeDelta cvd;
        private ATR atr;

        [NinjaScriptProperty, Range(10.0, 10000.0), Display(Name = "Dollar Risk Per Trade ($)", GroupName = "1. Risk Management", Order = 1)]
        public double DollarRiskPerTrade { get; set; } = 200.0;

        protected override void OnStateChange()
        {
            base.OnStateChange();
            if (State == State.SetDefaults) Name = "Strategy 3: CVD Absorption Divergence";
        }

        protected override void OnStrategyInitialize()
        {
            atr = ATR(5);
            // Will fail to compile if NT8 Order Flow addon is not licensed/enabled.
            cvd = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Bar, 0); 
        }

        protected override string GetStrategyDNA() => "Mean-Reversion";

        public override void GetStrategySessionWindow(out int startHHMM, out int endHHMM)
        {
            startHHMM = 930;
            endHHMM = 1600;
        }

        protected override void OnStrategyBarUpdate()
        {
            if (CurrentBar < 10) return;

            // 1. Calculate 6-Bar Linear Regression Slopes
            double[] prices = new double[6];
            double[] deltas = new double[6];
            for (int i = 0; i < 6; i++)
            {
                prices[i] = Close[i];
                deltas[i] = cvd.DeltaClose[i]; // Accessing internal Delta value
            }

            double priceSlope = CalculateSlope(prices);
            double cvdSlope = CalculateSlope(deltas);
            double atrThreshold = 0.15 * atr[0];

            if (Position.MarketPosition == MarketPosition.Flat && (CurrentMode == SystemMode.ArmAuto || CurrentMode == SystemMode.ArmLong || CurrentMode == SystemMode.ArmShort))
            {
                // Bullish Absorption
                if (priceSlope < -atrThreshold && cvdSlope > 0.0 && Close[0] > Open[0] && Close[0] > Close[1] && CurrentMode != SystemMode.ArmShort)
                {
                    double lowestLow = double.MaxValue;
                    for (int i = 0; i < 6; i++) lowestLow = Math.Min(lowestLow, Low[i]);

                    double stopPrice = lowestLow - TickSize;
                    double riskPts = Close[0] - stopPrice;
                    int qty = Math.Max(1, (int)Math.Floor(DollarRiskPerTrade / (riskPts * Instrument.MasterInstrument.PointValue)));
                    
                    activeLiveRisk = riskPts * Instrument.MasterInstrument.PointValue * qty;
                    double targetPrice = Close[0] + (riskPts * 2.0);

                    SetStopLoss("CVD_Abs_Long", CalculationMode.Price, stopPrice, false);
                    SetProfitTarget("CVD_Abs_Long", CalculationMode.Price, targetPrice);
                    EnterLong(qty, "CVD_Abs_Long");
                }
                // Bearish Absorption
                else if (priceSlope > atrThreshold && cvdSlope < 0.0 && Close[0] < Open[0] && Close[0] < Close[1] && CurrentMode != SystemMode.ArmLong)
                {
                    double highestHigh = double.MinValue;
                    for (int i = 0; i < 6; i++) highestHigh = Math.Max(highestHigh, High[i]);

                    double stopPrice = highestHigh + TickSize;
                    double riskPts = stopPrice - Close[0];
                    int qty = Math.Max(1, (int)Math.Floor(DollarRiskPerTrade / (riskPts * Instrument.MasterInstrument.PointValue)));

                    activeLiveRisk = riskPts * Instrument.MasterInstrument.PointValue * qty;
                    double targetPrice = Close[0] - (riskPts * 2.0);

                    SetStopLoss("CVD_Abs_Short", CalculationMode.Price, stopPrice, false);
                    SetProfitTarget("CVD_Abs_Short", CalculationMode.Price, targetPrice);
                    EnterShort(qty, "CVD_Abs_Short");
                }
            }
        }

        private double CalculateSlope(double[] y)
        {
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            int n = y.Length;
            for (int i = 0; i < n; i++)
            {
                double x = n - i; // reverse index so recent is higher X
                sumX += x;
                sumY += y[i];
                sumXY += x * y[i];
                sumX2 += x * x;
            }
            return ((n * sumXY) - (sumX * sumY)) / ((n * sumX2) - (sumX * sumX));
        }
    }
}