// =============================================================================
//  RegimeFilter - market state classification
// =============================================================================
//
//  WHAT IT DOES
//  Emits a single plotted integer describing the current market state:
//
//      0   Range
//      1   Trend up
//     -1   Trend down
//      2   High volatility
//
//  WHY IT EXISTS
//  Each strategy in /strategies declares a family through GetStrategyDNA -
//  Breakout or Mean-Reversion. This indicator supplies the other half of that
//  pairing: what the market currently is. A breakout model and a rotation model
//  are not both wrong in the same conditions, they are wrong in opposite ones,
//  and the host uses the two together to judge whether an idea is being run in
//  conditions it was designed for.
//
//  DESIGN DECISIONS AND WHY
//
//  * Volatility is measured as ATR expressed as a PERCENTAGE of price, not in
//    absolute points. An absolute threshold is silently instrument-specific and
//    drifts as price levels change over a year; a percentage keeps one
//    configuration meaningful across instruments and across time.
//
//  * High volatility outranks trend direction. A violently expanding market is
//    its own regime regardless of which way it is pointing, and classifying it
//    as an ordinary trend would let trend-following models size into conditions
//    where their stop assumptions no longer hold.
//
//  * Trend classification requires three things to agree: trend strength, the
//    position of price within its range, and the sign of the EMA slope. Any one
//    of them alone flips frequently around the threshold, and a regime signal
//    that oscillates is worse than none, because strategies gate on it.
//
//  * Non-finite intermediate values resolve to Range rather than propagating.
//    Division by a near-zero range can produce NaN or Infinity, and emitting
//    that as a regime would make every downstream comparison silently false.
//    Failing to the neutral state is the conservative direction to fail in.
//
// =============================================================================

#region Using declarations
using System;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class RegimeFilter : Indicator
    {
        private ADX adx;
        private ATR atr;
        private EMA emaFast;
        private EMA emaSlow;
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Regime Filter: 0=Range, 1=TrendUp, -1=TrendDown, 2=HighVol";
                Name = "RegimeFilter";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                DisplayInDataBox = true;
                PaintPriceMarkers = false;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;
                
                TrendThreshold = 25;
                HighVolThresholdPct = 3.0;
                LookbackPeriod = 14;
                
                AddPlot(new Stroke(Brushes.Blue, 2), PlotStyle.Line, "RegimeState");
                AddLine(Brushes.Green, 1, "TrendUp");
                AddLine(Brushes.Red, -1, "TrendDown");
                AddLine(Brushes.Gray, 0, "Range");
            }
            else if (State == State.DataLoaded)
            {
                adx = ADX(LookbackPeriod);
                atr = ATR(LookbackPeriod);
                emaFast = EMA(LookbackPeriod / 2);
                emaSlow = EMA(LookbackPeriod);
            }
        }
        
        protected override void OnBarUpdate()
        {
            if (CurrentBar < LookbackPeriod * 2)
            {
                Value[0] = 0;
                return;
            }
            
            double trendStrength = adx[0];
            double volatilityPct = (atr[0] / Close[0]) * 100;
            double pricePosition = Close[0] > emaSlow[0] ? 1 : -1;
            double emaSlope = (emaFast[0] - emaFast[1]) / emaFast[1];
            
            if (double.IsNaN(trendStrength) || double.IsInfinity(trendStrength) ||
                double.IsNaN(volatilityPct) || double.IsInfinity(volatilityPct))
            {
                Value[0] = 0;
                return;
            }
            
            int regimeState = 0;
            
            if (volatilityPct > HighVolThresholdPct)
            {
                regimeState = 2;
            }
            else if (trendStrength > TrendThreshold && pricePosition > 0 && emaSlope > 0.0001)
            {
                regimeState = 1;
            }
            else if (trendStrength > TrendThreshold && pricePosition < 0 && emaSlope < -0.0001)
            {
                regimeState = -1;
            }
            else
            {
                regimeState = 0;
            }
            
            Value[0] = regimeState;
        }
        
        #region Properties
        [NinjaScriptProperty]
        [Range(15, 50)]
        [Display(Name="ADX Trend Threshold", Order=1, GroupName="Regime Settings")]
        public double TrendThreshold { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="High Vol Threshold (%)", Order=2, GroupName="Regime Settings")]
        public double HighVolThresholdPct { get; set; }
        
        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name="Lookback Period", Order=3, GroupName="Regime Settings")]
        public int LookbackPeriod { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private RegimeFilter[] cacheRegimeFilter;
		public RegimeFilter RegimeFilter(double trendThreshold, double highVolThresholdPct, int lookbackPeriod)
		{
			return RegimeFilter(Input, trendThreshold, highVolThresholdPct, lookbackPeriod);
		}

		public RegimeFilter RegimeFilter(ISeries<double> input, double trendThreshold, double highVolThresholdPct, int lookbackPeriod)
		{
			if (cacheRegimeFilter != null)
				for (int idx = 0; idx < cacheRegimeFilter.Length; idx++)
					if (cacheRegimeFilter[idx] != null && cacheRegimeFilter[idx].TrendThreshold == trendThreshold && cacheRegimeFilter[idx].HighVolThresholdPct == highVolThresholdPct && cacheRegimeFilter[idx].LookbackPeriod == lookbackPeriod && cacheRegimeFilter[idx].EqualsInput(input))
						return cacheRegimeFilter[idx];
			return CacheIndicator<RegimeFilter>(new RegimeFilter(){ TrendThreshold = trendThreshold, HighVolThresholdPct = highVolThresholdPct, LookbackPeriod = lookbackPeriod }, input, ref cacheRegimeFilter);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.RegimeFilter RegimeFilter(double trendThreshold, double highVolThresholdPct, int lookbackPeriod)
		{
			return indicator.RegimeFilter(Input, trendThreshold, highVolThresholdPct, lookbackPeriod);
		}

		public Indicators.RegimeFilter RegimeFilter(ISeries<double> input , double trendThreshold, double highVolThresholdPct, int lookbackPeriod)
		{
			return indicator.RegimeFilter(input, trendThreshold, highVolThresholdPct, lookbackPeriod);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.RegimeFilter RegimeFilter(double trendThreshold, double highVolThresholdPct, int lookbackPeriod)
		{
			return indicator.RegimeFilter(Input, trendThreshold, highVolThresholdPct, lookbackPeriod);
		}

		public Indicators.RegimeFilter RegimeFilter(ISeries<double> input , double trendThreshold, double highVolThresholdPct, int lookbackPeriod)
		{
			return indicator.RegimeFilter(input, trendThreshold, highVolThresholdPct, lookbackPeriod);
		}
	}
}

#endregion
